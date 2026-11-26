// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Client.Popups;
using Content.Goobstation.Shared.VoiceChat;
using Content.Shared.Examine;
using Content.Shared.GameTicking;
using Content.Shared.Input;
using Content.Shared.Radio;
using Robust.Shared.Prototypes;
using Robust.Client.Audio;
using Robust.Client.Console;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Configuration;
using Robust.Shared.Console;
using Robust.Shared.Input.Binding;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Goobstation.Client.VoiceChat;

public sealed partial class ProximityVoiceSystem : EntitySystem
{
    [Dependency] private IAudioManager _audioManager = default!;
    [Dependency] private AudioSystem _audio = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IClientConsoleHost _console = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private ExamineSystemShared _examine = default!;
    [Dependency] private PopupSystem _popups = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private INetManager _net = default!;
    [Dependency] private IUriOpener _uri = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private IOverlayManager _overlays = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IInputManager _input = default!;

    private const int MaxSpeakers = 16;
    private readonly Dictionary<NetEntity, ProximityVoiceStream> _speakers = new();
    private readonly List<NetEntity> _retired = new();
    private DefaultWindow? _window;
    private TimeSpan? _requestDeadline;
    private bool _listening;
    private string[] _radioChannels = [];
    private string? _selectedRadio;
    private bool _optedOut;
    private bool _registeredForListening;
    private TimeSpan _nextRadioRefresh;
    private TimeSpan _talkUntil;
    private bool _talkBlocked;
    private VoiceLevels _selfTarget;
    private VoiceLevels _selfLevels;
    private bool _localHeld;
    private bool _radioHeld;
    private bool _muted;
    private readonly HashSet<string> _mutedRadio = new(StringComparer.Ordinal);
    private readonly Dictionary<NetEntity, float> _speakerVolume = new();

    public override void Initialize()
    {
        base.Initialize();
        _overlays.AddOverlay(new VoiceSpeakingOverlay(EntityManager, this));
        _input.SetInputCommand(ContentKeyFunctions.VoicePushToTalk,
            InputCmdHandler.FromDelegate(_ => SetTalkKey(false, true), _ => SetTalkKey(false, false)));
        _input.SetInputCommand(ContentKeyFunctions.VoiceRadioPushToTalk,
            InputCmdHandler.FromDelegate(_ => SetTalkKey(true, true), _ => SetTalkKey(true, false)));
        Subs.CVar(_cfg, ProximityVoiceCVars.HearSelf, value =>
        {
            if (!value && _players.LocalEntity is { } local && _speakers.Remove(GetNetEntity(local), out var self))
                self.Dispose();
            if (_listening && VoiceAvailable)
                RaiseNetworkEvent(new JoinVoiceListeningEvent(value));
        });
        Subs.CVar(_cfg, ProximityVoiceCVars.MutedRadio, value =>
        {
            _mutedRadio.Clear();
            foreach (var channel in value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                _mutedRadio.Add(channel);
        }, true);
        SubscribeNetworkEvent<VoicePairingResponseEvent>(OnPairingResponse);
        SubscribeNetworkEvent<VoiceRadioStatusEvent>(OnRadioStatus);
        SubscribeNetworkEvent<VoiceUnfamiliarSpeechEvent>(OnUnfamiliarSpeech);
        SubscribeNetworkEvent<VoiceTalkStateEvent>(message =>
        {
            if (!_listening)
                return;
            _talkBlocked = message.Blocked;
            _talkUntil = _timing.RealTime + TimeSpan.FromMilliseconds(300);
            _selfTarget = new VoiceLevels { Low = message.Low, Mid = message.Mid, High = message.High, Overall = message.Overall };
        });
        SubscribeNetworkEvent<RoundRestartCleanupEvent>(_ =>
        {
            ClearPlayback();
            _speakerVolume.Clear();
        });
    }

    public override void Shutdown()
    {
        _overlays.RemoveOverlay<VoiceSpeakingOverlay>();
        _input.SetInputCommand(ContentKeyFunctions.VoicePushToTalk, null);
        _input.SetInputCommand(ContentKeyFunctions.VoiceRadioPushToTalk, null);
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
        MicConnected = false;
        RadioDisplay = null;
        _radioChannels = [];
        _selectedRadio = null;
        _optedOut = true;
        _registeredForListening = false;
        _requestDeadline = null;
        _listening = false;
        _window?.Close();
        ClearPlayback();
        if (_net.IsConnected)
            RaiseNetworkEvent(new LeaveProximityVoiceEvent());
        shell.WriteLine(Loc.GetString("trauma-voice-left"));
    }

    public void RequestRadio(IConsoleShell shell, string? channel, bool queryOnly = false)
    {
        if (!_net.IsConnected || !_cfg.GetCVar(ProximityVoiceCVars.Enabled))
        {
            shell.WriteError(Loc.GetString("trauma-voice-unavailable"));
            return;
        }

        if (!_listening)
        {
            shell.WriteError(Loc.GetString("trauma-voice-radio-join-first"));
            return;
        }

        _showRadioStatus = true;
        RaiseNetworkEvent(new VoiceRadioSelectEvent(channel, queryOnly));
    }

    private void OnRadioStatus(VoiceRadioStatusEvent message)
    {
        if (!_listening || !_cfg.GetCVar(ProximityVoiceCVars.Enabled))
            return;
        if (!_registeredForListening)
        {
            _registeredForListening = true;
            SendInputMode();
        }

        var selected = message.Selected ?? Loc.GetString("trauma-voice-radio-local");
        if (_showRadioStatus)
        {
            _showRadioStatus = false;
            var channelSummary = message.Channels.Length == 0
                ? Loc.GetString("trauma-voice-radio-no-channels")
                : string.Join(", ", message.Channels);
            _console.LocalShell.WriteLine(Loc.GetString("trauma-voice-radio-status",
                ("selected", selected), ("channels", channelSummary)));
        }

        MicConnected = message.MicConnected;
        if (_selectedRadio == message.Selected && _radioChannels.AsSpan().SequenceEqual(message.Channels))
            return;
        _selectedRadio = message.Selected;
        _radioChannels = message.Channels;
        var channels = new List<ProtoId<RadioChannelPrototype>>(message.Channels.Length);
        foreach (var id in message.Channels)
            channels.Add(new ProtoId<RadioChannelPrototype>(id));
        RadioDisplay = new VoiceRadioDisplay(channels,
            message.Selected is {} selectedChannel ? new ProtoId<RadioChannelPrototype>(selectedChannel) : (ProtoId<RadioChannelPrototype>?) null,
            channels);
    }

    private void OnUnfamiliarSpeech(VoiceUnfamiliarSpeechEvent message)
    {
        if (!_listening || !_cfg.GetCVar(ProximityVoiceCVars.Enabled) || _players.LocalEntity is not {} listener)
            return;

        var anchor = listener;
        if (message.Source != NetEntity.Invalid && TryGetEntity(message.Source, out var speaker) &&
            _examine.CanExamine(listener, speaker.Value))
            anchor = speaker.Value;

        _popups.PopupEntity(Loc.GetString("trauma-voice-unfamiliar-speech"), anchor);
    }

    private void OnPairingResponse(VoicePairingResponseEvent message)
    {
        if (_requestDeadline is not { } deadline || _timing.RealTime > deadline)
            return;

        _requestDeadline = null;
        if (!_cfg.GetCVar(ProximityVoiceCVars.Enabled) ||
            !Uri.TryCreate(baseUri: null, message.Url, out var uri) ||
            uri.UserInfo.Length != 0 ||
            !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            _console.LocalShell.WriteError(Loc.GetString("trauma-voice-unavailable"));
            return;
        }

        _listening = true;
        _optedOut = false;
        SendInputMode();
        var window = new DefaultWindow { Title = Loc.GetString("trauma-voice-title") };
        var panel = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 8,
            MinWidth = 280,
        };
        var open = new Button { Text = Loc.GetString("trauma-voice-connect-browser") };
        open.OnPressed += _ =>
        {
            _uri.OpenUri(uri);
            window.Close();
        };
        panel.AddChild(open);

        var codePanel = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 4,
            Visible = false,
        };
        codePanel.AddChild(new Label { Text = Loc.GetString("trauma-voice-code-help", ("url", message.Url.Split('#')[0])) });
        codePanel.AddChild(new LineEdit { Text = uri.Fragment.TrimStart('#'), Editable = false });
        var showCode = new Button { Text = Loc.GetString("trauma-voice-show-code"), ToggleMode = true };
        showCode.OnToggled += args => codePanel.Visible = args.Pressed;
        panel.AddChild(showCode);
        panel.AddChild(codePanel);
        window.Contents.AddChild(panel);
        window.OnClose += () => _window = null;
        _window = window;
        window.OpenCentered();
    }

    internal void ReceiveFrame(MsgProximityVoiceFrame message)
    {
        if (!_listening || Deafened || !_cfg.GetCVar(ProximityVoiceCVars.Enabled) ||
            (message.Route == VoiceDeliveryRoute.Sidetone && !_cfg.GetCVar(ProximityVoiceCVars.HearSelf)) ||
            !VoiceCodec.IsValidFrame(message.Payload) || message.Source == NetEntity.Invalid ||
            _mutedRadio.Contains(message.RadioChannel))
            return;

        var now = _timing.RealTime;
        _speakers.TryGetValue(message.Source, out var stream);
        if (stream != null && (stream.Route != message.Route || stream.Emitter != message.Emitter ||
            stream.RadioChannel != message.RadioChannel))
        {
            // Ignore delayed packets from the old route after a speaker changes devices.
            if (!stream.IsNewer(message.Sequence, now))
                return;

            stream.Dispose();
            _speakers.Remove(message.Source);
            stream = null;
        }
        if (stream == null)
        {
            if (_speakers.Count >= MaxSpeakers)
                return;

            stream = new ProximityVoiceStream(_audioManager, message.Route, message.Emitter)
            {
                RadioChannel = message.RadioChannel,
                Speaker = message.Source,
            };
            _speakers.Add(message.Source, stream);
        }

        stream.AddFrame(message.Sequence, message.Payload, now);
        if (message.Route == VoiceDeliveryRoute.Dead)
            stream.DeadPosition = message.DeadPosition;
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);
        var now = _timing.RealTime;
        UpdateListening(now);
        VoiceLevelSmoothing.Apply(ref _selfLevels, TalkActive ? _selfTarget : default, frameTime);
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

        if (Deafened)
        {
            if (_speakers.Count > 0)
                ClearPlayback();
            return;
        }
        if (_speakers.Count == 0)
            return;

        var listener = _audio.GetListenerCoordinates();
        var range = Math.Clamp(_cfg.GetCVar(ProximityVoiceCVars.Range), 1, 64);
        var volume = Math.Clamp(_cfg.GetCVar(ProximityVoiceCVars.Volume), 0f, 1f);
        var radioVolume = Math.Clamp(_cfg.GetCVar(ProximityVoiceCVars.RadioVolume), 0f, 2f);
        foreach (var (source, stream) in _speakers)
        {
            stream.Volume = (stream.RadioChannel.Length > 0 ? volume * radioVolume : volume) *
                _speakerVolume.GetValueOrDefault(source, 1f);
            if (_mutedRadio.Contains(stream.RadioChannel))
                stream.Volume = 0f;
            if (now - stream.LastReceived > TimeSpan.FromSeconds(1))
            {
                _retired.Add(source);
                continue;
            }

            // A radio or private handset can receive a speaker outside the listener's PVS.
            if (stream.Global)
            {
                stream.Audibility = Math.Clamp(stream.Volume, 0f, 1f);
                stream.UpdateIndicator(now, frameTime);
                stream.Update(now, Vector2.Zero, 1f, 1f);
                continue;
            }

            Vector2 position;
            if (TryGetEntity(stream.Emitter, out var uid) &&
                TryComp(uid, out TransformComponent? xform) && xform.MapID == listener.MapId)
            {
                position = _transform.GetWorldPosition(xform);
            }
            else if (stream.Route == VoiceDeliveryRoute.Dead && stream.DeadPosition.MapId == listener.MapId)
            {
                position = stream.DeadPosition.Position;
            }
            else
            {
                _retired.Add(source);
                continue;
            }

            var delta = position - listener.Position;
            var distance = delta.Length();
            var playbackRange = stream.Route == VoiceDeliveryRoute.Megaphone ? VoiceCodec.MegaphoneRange(range) : range;
            if (distance > playbackRange)
            {
                _retired.Add(source);
                continue;
            }

            if (now >= stream.NextOcclusionCheck)
            {
                stream.Occlusion = _audio.GetOcclusion(listener, delta, distance, uid);
                stream.NextOcclusionCheck = now + TimeSpan.FromMilliseconds(100);
            }

            stream.Audibility = Math.Clamp((1f - distance / playbackRange) * stream.Volume, 0f, 1f);
            stream.UpdateIndicator(now, frameTime);
            stream.Update(now, position, _audio.GetAudioDistance(1f), _audio.GetAudioDistance(playbackRange));
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
        _talkUntil = TimeSpan.Zero;
        _selfTarget = _selfLevels = default;
    }

    private bool _showRadioStatus;

    internal bool VoiceAvailable => _net.IsConnected && _cfg.GetCVar(ProximityVoiceCVars.Enabled);
    internal bool Listening => _listening;
    internal bool Deafened => _cfg.GetCVar(ProximityVoiceCVars.Deafened);
    internal bool ShowIndicators => _cfg.GetCVar(ProximityVoiceCVars.ShowIndicators);
    internal bool MicConnected { get; private set; }
    internal bool TalkActive => _timing.RealTime < _talkUntil;
    internal bool TalkBlocked => _talkBlocked;
    internal bool Muted => _muted;

    internal void SetDeafened(bool deafened)
    {
        _cfg.SetCVar(ProximityVoiceCVars.Deafened, deafened);
        _cfg.SaveToFile();
        SendInputMode();
    }

    internal void SetMicMuted(bool muted)
    {
        _muted = muted;
        SendInputMode();
    }

    private void SetTalkKey(bool radio, bool held)
    {
        if (radio)
            _radioHeld = held;
        else
            _localHeld = held;
        SendInputMode();
    }

    private void SendInputMode()
    {
        if (!VoiceAvailable || !_listening)
            return;
        var mode = (_muted || Deafened) ? VoiceInputMode.Muted : _radioHeld ? VoiceInputMode.Radio :
            _localHeld ? VoiceInputMode.Local : VoiceInputMode.Browser;
        RaiseNetworkEvent(new SetVoiceInputModeEvent(mode));
    }
    internal VoiceRadioDisplay? RadioDisplay { get; private set; }
    internal IReadOnlyDictionary<NetEntity, ProximityVoiceStream> Streams => _speakers;
    internal VoiceLevels SelfLevels => _selfLevels;
    internal float SelfActivity => Math.Clamp(_selfLevels.Overall * 3f, 0f, 1f);

    internal bool IsSelf(NetEntity speaker)
        => _players.LocalEntity is {} local && speaker == GetNetEntity(local);

    private static readonly Color RadioColor = Color.FromHex("#72D6A0");
    private static readonly Color PhoneColor = Color.FromHex("#63CFFF");
    private static readonly Color HolopadColor = Color.FromHex("#66E1D4");
    private static readonly Color CameraColor = Color.FromHex("#A4CCFD");
    private static readonly Color BroadcastColor = Color.FromHex("#F2C66A");
    private static readonly Color QuietColor = Color.FromHex("#A9C1DF");
    private static readonly Color LoudColor = Color.FromHex("#FF9F43");
    private static readonly Color BlockedColor = Color.FromHex("#E04545");

    private RadioChannelPrototype? GetRadioChannel(string? id)
        => !string.IsNullOrEmpty(id) && _prototypes.TryIndex<RadioChannelPrototype>(id, out var prototype) ? prototype : null;

    private string? SelfChannel => _localHeld && !_radioHeld
        ? null
        : _selectedRadio ?? (_radioHeld && _radioChannels.Length > 0 ? _radioChannels[0] : null);

    internal Color GetSelfColor() => TalkBlocked ? BlockedColor :
        SelfChannel is {} channel ? GetRadioChannel(channel)?.Color ?? RadioColor : LevelColor(SelfLevels.Overall);

    internal string? GetSelfLabel() => TalkBlocked ? Loc.GetString("trauma-voice-hud-blocked") :
        SelfChannel is {} channel ? GetRadioChannel(channel)?.LocalizedName ?? channel : LevelLabel(SelfLevels.Overall);

    internal Color GetRouteColor(ProximityVoiceStream stream) => stream.Route switch
    {
        VoiceDeliveryRoute.Radio or VoiceDeliveryRoute.RadioSpeaker => GetRadioChannel(stream.RadioChannel)?.Color ?? RadioColor,
        VoiceDeliveryRoute.Handset or VoiceDeliveryRoute.Speakerphone => PhoneColor,
        VoiceDeliveryRoute.Holopad => HolopadColor,
        VoiceDeliveryRoute.Camera or VoiceDeliveryRoute.Monitor => CameraColor,
        VoiceDeliveryRoute.Broadcast or VoiceDeliveryRoute.Megaphone => BroadcastColor,
        _ => LevelColor(stream.Levels.Overall),
    };

    internal string? GetRouteLabel(ProximityVoiceStream stream) => stream.Route switch
    {
        VoiceDeliveryRoute.Radio or VoiceDeliveryRoute.RadioSpeaker => GetRadioChannel(stream.RadioChannel)?.LocalizedName ??
            Loc.GetString(stream.Route == VoiceDeliveryRoute.Radio ? "trauma-voice-route-radio" : "trauma-voice-route-intercom"),
        VoiceDeliveryRoute.Handset => Loc.GetString("trauma-voice-route-handset"),
        VoiceDeliveryRoute.Speakerphone => Loc.GetString("trauma-voice-route-speakerphone"),
        VoiceDeliveryRoute.Dead => Loc.GetString("trauma-voice-route-dead"),
        VoiceDeliveryRoute.Holopad => Loc.GetString("voice-route-holopad"),
        VoiceDeliveryRoute.Camera or VoiceDeliveryRoute.Monitor => Loc.GetString("voice-route-camera"),
        VoiceDeliveryRoute.Broadcast => Loc.GetString("voice-route-broadcast"),
        VoiceDeliveryRoute.Megaphone => Loc.GetString("voice-route-megaphone"),
        _ => LevelLabel(stream.Levels.Overall),
    };

    private static Color LevelColor(float level) => level > 0.85f ? LoudColor : level < 0.3f ? QuietColor : Color.White;
    private string? LevelLabel(float level) => level > 0.85f ? Loc.GetString("trauma-voice-level-loud") :
        level < 0.3f ? Loc.GetString("trauma-voice-level-quiet") : null;

    internal string DescribeSpeaker(ProximityVoiceStream stream)
    {
        if (_players.LocalEntity is {} listener && !stream.Global &&
            TryGetEntity(stream.Emitter, out var emitter) && _examine.CanExamine(listener, emitter.Value))
            return Name(emitter.Value);

        return Loc.GetString("trauma-voice-unknown-speaker");
    }

    internal void OpenSpeakerVolume(NetEntity speaker, string name)
    {
        var window = new DefaultWindow { Title = Loc.GetString("trauma-voice-speaker-volume", ("speaker", name)) };
        var slider = new Slider { MinValue = 0, MaxValue = 2, Value = _speakerVolume.GetValueOrDefault(speaker, 1f) };
        var mute = new Button { Text = Loc.GetString("trauma-voice-chat-mute") };
        slider.OnValueChanged += _ => _speakerVolume[speaker] = slider.Value;
        mute.OnPressed += _ => slider.Value = 0f;
        var controls = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = 6 };
        controls.AddChild(slider);
        controls.AddChild(mute);
        window.Contents.AddChild(controls);
        window.MinWidth = 260;
        window.OpenCentered();
    }

    internal bool TryGetVisibleSpeaker(ProximityVoiceStream stream, MapId map, out Vector2 position)
    {
        position = default;
        if (stream.Global || _players.LocalEntity is not {} listener ||
            _timing.RealTime - stream.LastReceived > TimeSpan.FromMilliseconds(400) ||
            !TryGetEntity(stream.Emitter, out var uid) || !TryComp(uid, out TransformComponent? xform) ||
            xform.MapID != map || !_examine.CanExamine(listener, uid.Value))
            return false;

        position = _transform.GetWorldPosition(xform);
        return true;
    }
    internal void OpenMicrophone() => RequestPairing(_console.LocalShell);

    internal void SelectRadio(string? channel)
    {
        if (_listening && VoiceAvailable)
            RaiseNetworkEvent(new VoiceRadioSelectEvent(channel));
    }

    private void UpdateListening(TimeSpan now)
    {
        if (!VoiceAvailable)
        {
            MicConnected = false;
            _registeredForListening = false;
            return;
        }
        if (_optedOut || _players.LocalEntity == null || now < _nextRadioRefresh)
            return;

        _nextRadioRefresh = now + TimeSpan.FromSeconds(1);
        _listening = true;
        if (!_registeredForListening)
            RaiseNetworkEvent(new JoinVoiceListeningEvent(_cfg.GetCVar(ProximityVoiceCVars.HearSelf)));
        RaiseNetworkEvent(new VoiceRadioSelectEvent(null, queryOnly: true));
    }
}
