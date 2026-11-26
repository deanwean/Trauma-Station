// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.Goobstation.Shared.VoiceChat;
using Robust.Server.ServerStatus;
using Robust.Shared.Log;

namespace Content.Trauma.Server.VoiceChat;

internal sealed class ProximityVoiceWeb
{
    // The status host outlives entity systems and has no RemoveHandler API.
    private static readonly ConditionalWeakTable<IStatusHost, ProximityVoiceWeb> Hosts = new();
    private static readonly byte[] Ready = "ready"u8.ToArray();
    private static readonly Lazy<Dictionary<string, (string Mime, byte[] Bytes)>> Assets = new(LoadAssets);
    private readonly SemaphoreSlim _pendingPairs = new(8, 8);
    private readonly ISawmill _log = Logger.GetSawmill("voice");
    private Registration? _current;

    private sealed record Registration(Func<bool> Enabled, Func<string, string, VoiceCapture?> Pair, CancellationToken Shutdown);

    public static void Register(IStatusHost host, Func<bool> enabled, Func<string, string, VoiceCapture?> pair, CancellationToken shutdown)
    {
        var web = Hosts.GetValue(host, static statusHost =>
        {
            var instance = new ProximityVoiceWeb();
            statusHost.AddHandler(instance.Handle);
            return instance;
        });
        var registration = new Registration(enabled, pair, shutdown);
        Volatile.Write(ref web._current, registration);
        shutdown.Register(() => Interlocked.CompareExchange(ref web._current, null, registration));
    }

    private static Dictionary<string, (string, byte[])> LoadAssets()
    {
        var assets = new Dictionary<string, (string, byte[])>();
        Add("/voice", "mic.html", "text/html; charset=utf-8");
        Add("/voice/mic.js", "mic.js", "application/javascript");
        Add("/voice/capture.js", "capture.js", "application/javascript");
        return assets;

        void Add(string route, string file, string mime)
        {
            using var resource = typeof(ProximityVoiceWeb).Assembly.GetManifestResourceStream("Trauma.Voice." + file)
                ?? throw new InvalidOperationException($"Missing embedded microphone asset {file}");
            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            assets.Add(route, (mime, buffer.ToArray()));
        }
    }

    private async Task<bool> Handle(IStatusHandlerContext context)
    {
        var path = context.Url.AbsolutePath;
        if (path != "/voice" && !path.StartsWith("/voice/", StringComparison.Ordinal))
            return false;

        var registration = Volatile.Read(ref _current);
        if (registration == null || registration.Shutdown.IsCancellationRequested || !registration.Enabled())
            return false;

        if (path == "/voice/ws")
        {
            if (!context.IsWebSocketRequest)
                await context.RespondErrorAsync(HttpStatusCode.BadRequest);
            else if (!_pendingPairs.Wait(0))
                await context.RespondErrorAsync(HttpStatusCode.TooManyRequests);
            else
            {
                WebSocket socket;
                try
                {
                    socket = await context.AcceptWebSocketAsync();
                }
                catch
                {
                    _pendingPairs.Release();
                    throw;
                }

                // Free the status host's HTTP concurrency slot once upgraded.
                _ = ReceiveAudio(socket, registration);
            }

            return true;
        }

        if (!context.IsGetLike || !Assets.Value.TryGetValue(path.TrimEnd('/'), out var asset))
            return false;

        context.ResponseHeaders["Cache-Control"] = "no-store";
        context.ResponseHeaders["Referrer-Policy"] = "no-referrer";
        context.ResponseHeaders["X-Content-Type-Options"] = "nosniff";
        context.ResponseHeaders["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'unsafe-inline'; worker-src 'self'; connect-src 'self' wss: ws:; frame-ancestors 'none'; base-uri 'none'";
        await context.RespondAsync(asset.Bytes, HttpStatusCode.OK, asset.Mime);
        return true;
    }

    private async Task ReceiveAudio(WebSocket socket, Registration registration)
    {
        using (socket)
        {
            try
            {
                VoiceCapture? capture;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(registration.Shutdown);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    var credentials = new byte[128];
                    if (!await ReceiveFixed(socket, credentials, WebSocketMessageType.Text, timeout.Token))
                        return;

                    capture = registration.Pair(Encoding.ASCII.GetString(credentials, 0, 64).TrimEnd(' '),
                        Encoding.ASCII.GetString(credentials, 64, 64));
                }
                finally
                {
                    _pendingPairs.Release();
                }

                if (capture == null)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Microphone link rejected", registration.Shutdown);
                    return;
                }

                using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(registration.Shutdown, capture.Revoked))
                {
                    var connection = capture.OpenStream();
                    try
                    {
                        await socket.SendAsync(Ready.AsMemory(), WebSocketMessageType.Text, true, lifetime.Token);
                        var receive = ReceiveFrames(socket, capture, connection, lifetime.Token);
                        var send = SendInputMode(socket, capture, lifetime.Token);
                        await Task.WhenAny(receive, send);
                        lifetime.Cancel();
                        await Task.WhenAll(receive, send);
                    }
                    finally
                    {
                        capture.CloseStream(connection);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (IOException) { }
            catch (Exception error)
            {
                // This task continues after Handle returns, so the status host cannot log its failures.
                _log.Error($"Microphone connection ended unexpectedly: {error.GetType().Name}");
            }
        }
    }

    private static async Task ReceiveFrames(WebSocket socket, VoiceCapture capture, long connection, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            var frame = new byte[VoiceCodec.FrameBytes];
            if (!await ReceiveFixed(socket, frame, WebSocketMessageType.Binary, cancel) ||
                !capture.TrySubmit(connection, frame))
                return;
        }
    }

    private static async Task SendInputMode(WebSocket socket, VoiceCapture capture, CancellationToken cancel)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(35));
        VoiceInputMode? previous = null;
        do
        {
            var mode = capture.Mode;
            if (mode == previous)
                continue;
            previous = mode;
            var frame = Encoding.ASCII.GetBytes($"mode:{(int) mode}");
            await socket.SendAsync(frame.AsMemory(), WebSocketMessageType.Text, true, cancel);
        } while (await timer.WaitForNextTickAsync(cancel));
    }

    private static async Task<bool> ReceiveFixed(WebSocket socket, byte[] bytes, WebSocketMessageType type, CancellationToken cancel)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            var result = await socket.ReceiveAsync(bytes.AsMemory(offset), cancel);
            if (result.MessageType != type || result.Count == 0)
                return false;

            offset += result.Count;
            if (result.EndOfMessage)
                return offset == bytes.Length;
        }

        return false;
    }
}
