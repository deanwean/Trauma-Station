// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Goobstation.Shared.VoiceChat;
using Content.Shared.GameTicking;
using Robust.Client.Audio;
using Robust.Client.Console;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Configuration;
using Robust.Shared.Console;
using Robust.Shared.Timing;

namespace Content.Goobstation.Client.VoiceChat;

public sealed partial class ProximityVoiceSystem : EntitySystem
{
    [Dependency] private IAudioManager _audioManager = default!;
    [Dependency] private AudioSystem _audio = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IClientConsoleHost _console = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private INetManager _net = default!;
    [Dependency] private IUriOpener _uri = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private const int MaxSpeakers = 16;
    private readonly Dictionary<EntityUid, ProximityVoiceStream> _speakers = new();
    private readonly List<EntityUid> _retired = new();
    private DefaultWindow? _window;
    private TimeSpan? _requestDeadline;
    private bool _listening;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<VoicePairingResponseEvent>(OnPairingResponse);
        SubscribeNetworkEvent<RoundRestartCleanupEvent>(_ => ClearPlayback());
    }

    public override void Shutdown()
    {
        _window?.Close();
        ClearPlayback();
        base.Shutdown();
    }

    public void RequestPairing(IConsoleShell shell)
    {
        if (!_net.IsConnected || !_cfg.GetCVar(ProximityVoiceCVars.Enabled))
        {
            shell.WriteError(Loc.GetString("trauma-voice-unavailable"));
            return;
        }

        if (_requestDeadline is not null)
            return;

        _window?.Close();
        _requestDeadline = _timing.RealTime + TimeSpan.FromSeconds(10);
        RaiseNetworkEvent(new RequestVoicePairingEvent());
        shell.WriteLine(Loc.GetString("trauma-voice-requested"));
    }

    public void Leave(IConsoleShell shell)
    {
        _requestDeadline = null;
        _listening = false;
        _window?.Close();
        ClearPlayback();
        if (_net.IsConnected)
            RaiseNetworkEvent(new LeaveProximityVoiceEvent());
        shell.WriteLine(Loc.GetString("trauma-voice-left"));
    }

    private void OnPairingResponse(VoicePairingResponseEvent message)
    {
        if (_requestDeadline is not { } deadline || _timing.RealTime > deadline)
            return;

        _requestDeadline = null;
        if (!_cfg.GetCVar(ProximityVoiceCVars.Enabled) ||
            !Uri.TryCreate(message.Url, UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 ||
            !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            _console.LocalShell.WriteError(Loc.GetString("trauma-voice-unavailable"));
            return;
        }

        _listening = true;
        var window = new DefaultWindow { Title = Loc.GetString("trauma-voice-title") };
        var open = new Button { Text = Loc.GetString("trauma-voice-browser", ("host", uri.Authority)) };
        open.OnPressed += _ =>
        {
            _uri.OpenUri(uri);
            window.Close();
        };
        window.Contents.AddChild(open);
        window.OnClose += () => _window = null;
        _window = window;
        window.OpenCentered();
    }

    internal void ReceiveFrame(MsgProximityVoiceFrame message)
    {
        if (!_listening || !_cfg.GetCVar(ProximityVoiceCVars.Enabled) ||
            !VoiceCodec.IsValidFrame(message.Payload) || !TryGetEntity(message.Source, out var uid))
            return;

        if (!_speakers.TryGetValue(uid.Value, out var stream))
        {
            if (_speakers.Count >= MaxSpeakers)
                return;

            _speakers.Add(uid.Value, stream = new ProximityVoiceStream(_audioManager));
        }

        stream.AddFrame(message.Sequence, message.Payload, _timing.RealTime);
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);
        var now = _timing.RealTime;
        if (_requestDeadline is { } deadline && now > deadline)
        {
            _requestDeadline = null;
            _console.LocalShell.WriteError(Loc.GetString("trauma-voice-expired"));
        }

        if (!_listening || !_cfg.GetCVar(ProximityVoiceCVars.Enabled))
        {
            if (_listening)
            {
                _listening = false;
                _window?.Close();
                ClearPlayback();
            }
            return;
        }

        if (_speakers.Count == 0)
            return;

        var listener = _audio.GetListenerCoordinates();
        var range = Math.Clamp(_cfg.GetCVar(ProximityVoiceCVars.Range), 1, 64);
        foreach (var (uid, stream) in _speakers)
        {
            if (now - stream.LastReceived > TimeSpan.FromSeconds(1) ||
                !TryComp(uid, out TransformComponent? xform) || xform.MapID != listener.MapId)
            {
                _retired.Add(uid);
                continue;
            }

            var position = _transform.GetWorldPosition(xform);
            var delta = position - listener.Position;
            var distance = delta.Length();
            if (distance > range)
            {
                _retired.Add(uid);
                continue;
            }

            if (now >= stream.NextOcclusionCheck)
            {
                stream.Occlusion = _audio.GetOcclusion(listener, delta, distance, uid);
                stream.NextOcclusionCheck = now + TimeSpan.FromMilliseconds(100);
            }

            stream.Update(now, position, _audio.GetAudioDistance(1f), _audio.GetAudioDistance(range));
        }

        foreach (var uid in _retired)
        {
            _speakers[uid].Dispose();
            _speakers.Remove(uid);
        }
        _retired.Clear();
    }

    private void ClearPlayback()
    {
        foreach (var stream in _speakers.Values)
            stream.Dispose();
        _speakers.Clear();
        _retired.Clear();
    }
}
