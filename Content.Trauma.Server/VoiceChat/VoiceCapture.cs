// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;
using System.Threading;
using Content.Goobstation.Shared.VoiceChat;

namespace Content.Trauma.Server.VoiceChat;

/// <summary>Transfers validated microphone audio from one browser to the game thread.</summary>
internal sealed class VoiceCapture : IDisposable
{
    private const int MaxQueuedFrames = 12;
    private const double FramesPerSecond = (double) VoiceCodec.SampleRate / VoiceCodec.FrameSamples;
    private readonly Queue<(long ReceivedAt, byte[] Frame)> _frames = new(MaxQueuedFrames);
    private readonly CancellationTokenSource _revoke = new();
    private double _allowance = MaxQueuedFrames;
    private long _lastFrameAt = Stopwatch.GetTimestamp();
    private bool _closed;
    private string? _browserKey;
    private long _connection;
    private volatile bool _connected;
    private volatile VoiceInputMode _mode;

    public bool IsConnected => _connected;

    public VoiceInputMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
                return;
            lock (_frames)
            {
                _mode = value;
                _frames.Clear();
            }
        }
    }

    public CancellationToken Revoked { get; }

    public VoiceCapture()
    {
        Revoked = _revoke.Token;
    }

    public bool AuthorizeBrowser(string browserKey, bool initialLinkValid)
    {
        lock (_frames)
        {
            if (_closed || (_browserKey == null && !initialLinkValid))
                return false;

            _browserKey ??= browserKey;
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(_browserKey), System.Text.Encoding.ASCII.GetBytes(browserKey));
        }
    }

    public long OpenStream()
    {
        lock (_frames)
        {
            _frames.Clear();
            _allowance = MaxQueuedFrames;
            _lastFrameAt = Stopwatch.GetTimestamp();
            _connected = !_closed;
            return ++_connection;
        }
    }

    public void CloseStream(long connection)
    {
        lock (_frames)
        {
            if (connection != _connection)
                return;
            _connected = false;
            _frames.Clear();
        }
    }

    // Only the latest connection can submit. A burst of valid frames is dropped without unpairing.
    public bool TrySubmit(long connection, byte[] frame)
    {
        if (!VoiceCodec.IsValidFrame(frame))
            return false;

        lock (_frames)
        {
            if (!_connected || connection != _connection)
                return false;
            if (_mode == VoiceInputMode.Muted)
                return true;

            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_lastFrameAt, now).TotalSeconds;
            _allowance = Math.Min(MaxQueuedFrames, _allowance + elapsed * FramesPerSecond);
            _lastFrameAt = now;
            if (_allowance < 1)
                return true;

            _allowance--;
            if (_frames.Count == MaxQueuedFrames)
                _frames.Dequeue();
            _frames.Enqueue((now, frame));
            return true;
        }
    }

    public void DrainInto(List<byte[]> frames)
    {
        lock (_frames)
        {
            var now = Stopwatch.GetTimestamp();
            while (_frames.TryDequeue(out var item))
            {
                if (Stopwatch.GetElapsedTime(item.ReceivedAt, now).TotalSeconds <= MaxQueuedFrames / FramesPerSecond)
                    frames.Add(item.Frame);
            }
        }
    }

    public void Clear()
    {
        lock (_frames)
        {
            _frames.Clear();
        }
    }

    public void Dispose()
    {
        lock (_frames)
        {
            if (_closed)
                return;
            _closed = true;
            _connected = false;
            _frames.Clear();
        }

        _revoke.Cancel();
        _revoke.Dispose();
    }
}
