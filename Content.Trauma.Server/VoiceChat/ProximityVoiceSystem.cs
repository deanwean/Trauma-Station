// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using System.Security.Cryptography;
using System.Threading;
using Content.Goobstation.Common.Traits;
using Content.Goobstation.Shared.VoiceChat;
using Content.Goobstation.Server.VoiceChat;
using Content.Shared.Silicons.StationAi;
using Content.Shared.ActionBlocker;
using Content.Shared.Chat;
using Content.Shared.Examine;
using Content.Shared.GameTicking;
using Content.Shared.Ghost.Components;
using Content.Shared.IdentityManagement;
using Content.Shared.Mobs.Systems;
using Content.Shared.Speech;
using Content.Trauma.Shared.Language.Systems;
using Robust.Server.Player;
using Robust.Server.ServerStatus;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Player;

namespace Content.Trauma.Server.VoiceChat;

public sealed partial class ProximityVoiceSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private IServerNetManager _net = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IStatusHost _statusHost = default!;
    [Dependency] private ActionBlockerSystem _action = default!;
    [Dependency] private ExamineSystemShared _examine = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private MobStateSystem _mob = default!;
    [Dependency] private SharedLanguageSystem _language = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private sealed class Participant
    {
        public required VoiceCapture Capture;
        public required string Token;
        public long IssuedAt;
        public long NextSpeechAttempt;
        public long NextTalkState;
        public bool TalkBlocked;
        public readonly UnfamiliarVoiceFilter UnfamiliarFilter = new();
        public readonly VoiceLevelAnalyzer LevelAnalyzer = new();
        public VoiceEffectProcessor? Effect;
        public VoiceEffectSettings AppliedEffect;
        public ushort Sequence;
        public string? RadioChannel;
        public string? PushToTalkChannel;
        public bool HearSelf;
        public readonly Dictionary<ICommonSession, long> NextUnfamiliarCue = new();
    }

    // Only ticket redemption and VoiceCapture's bounded queue run on HTTP worker threads.
    private readonly Dictionary<string, Participant> _tickets = new(StringComparer.Ordinal);
    private readonly Dictionary<ICommonSession, Participant> _participants = new();
    private readonly Dictionary<(VoiceDeliveryRoute Route, NetEntity Emitter, bool Unfamiliar), List<INetChannel>> _destinations = new();
    private readonly List<byte[]> _frames = new();
    private readonly CancellationTokenSource _shutdown = new();
    private volatile bool _enabled;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<RequestVoicePairingEvent>(OnPairingRequested);
        SubscribeNetworkEvent<JoinVoiceListeningEvent>(OnJoinListening);
        SubscribeNetworkEvent<SetVoiceInputModeEvent>(OnInputMode);
        SubscribeNetworkEvent<VoiceRadioSelectEvent>(OnRadioSelect);
        Subs.CVar(_config, ProximityVoiceCVars.RadioEnabled, _ => ClearDisabledRadioChannels());
        Subs.CVar(_config, ProximityVoiceCVars.RadioCommon, _ => ClearDisabledRadioChannels());
        SubscribeNetworkEvent<LeaveProximityVoiceEvent>((_, args) => Remove(args.SenderSession));
        SubscribeLocalEvent<PlayerAttachedEvent>(OnPlayerAttached);
        _players.PlayerStatusChanged += OnStatusChanged;
        Subs.CVar(_config, ProximityVoiceCVars.Enabled, OnEnabledChanged, true);
        ProximityVoiceWeb.Register(_statusHost, () => _enabled, ClaimTicket, _shutdown.Token);
    }

    public override void Shutdown()
    {
        _players.PlayerStatusChanged -= OnStatusChanged;
        OnEnabledChanged(false);
        _shutdown.Cancel();
        _shutdown.Dispose();
        base.Shutdown();
    }

    private void OnEnabledChanged(bool enabled)
    {
        _enabled = enabled;
        if (enabled)
            return;

        lock (_tickets)
        {
            _tickets.Clear();
        }
        foreach (var participant in _participants.Values)
            participant.Capture.Dispose();
        _participants.Clear();
    }

    private void OnStatusChanged(object? sender, SessionStatusEventArgs args)
    {
        if (args.NewStatus == SessionStatus.Disconnected)
            Remove(args.Session);
    }

    private void OnPlayerAttached(PlayerAttachedEvent args)
    {
        if (_participants.TryGetValue(args.Player, out var participant))
        {
            participant.Capture.Clear();
            participant.NextSpeechAttempt = 0;
            participant.NextTalkState = 0;
            participant.RadioChannel = null;
            participant.PushToTalkChannel = null;
            if (participant.Capture.Mode is VoiceInputMode.Local or VoiceInputMode.Radio)
                participant.Capture.Mode = VoiceInputMode.Browser;
            participant.NextUnfamiliarCue.Clear();
        }
    }

    private void Remove(ICommonSession session)
    {
        if (!_participants.Remove(session, out var participant))
            return;

        lock (_tickets)
        {
            _tickets.Remove(participant.Token);
        }
        foreach (var other in _participants.Values)
            other.NextUnfamiliarCue.Remove(session);
        participant.Capture.Dispose();
    }

    private void OnPairingRequested(RequestVoicePairingEvent ev, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        var now = Environment.TickCount64;
        if (!_enabled || !Uri.TryCreate(GetMicrophonePageUrl(session.Channel), UriKind.Absolute, out var page)
            || !(page.Scheme == Uri.UriSchemeHttps || page.Scheme == Uri.UriSchemeHttp && page.IsLoopback)
            || page.UserInfo.Length != 0 || page.Query.Length != 0 || page.Fragment.Length != 0
            || session.Status != SessionStatus.InGame)
        {
            RaiseNetworkEvent(new VoicePairingResponseEvent(string.Empty), session);
            return;
        }

        if (_participants.TryGetValue(session, out var previous) &&
            previous.Token.Length != 0 && now - previous.IssuedAt < 1000)
        {
            // Repeated clicks should not invalidate a page that has just opened.
            return;
        }

        Remove(session);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(10));
        var participant = new Participant
        {
            Capture = new VoiceCapture(),
            Token = token,
            IssuedAt = now,
            Sequence = previous?.Sequence ?? 0,
            RadioChannel = previous?.RadioChannel,
            HearSelf = previous?.HearSelf ?? false,
        };
        _participants.Add(session, participant);
        lock (_tickets)
        {
            _tickets.Add(token, participant);
        }
        RaiseNetworkEvent(new VoicePairingResponseEvent(new UriBuilder(page) { Fragment = token }.Uri.AbsoluteUri), session);
    }

    private void OnJoinListening(JoinVoiceListeningEvent message, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (!_enabled || session.Status != SessionStatus.InGame)
            return;

        if (_participants.TryGetValue(session, out var participant))
            participant.HearSelf = message.HearSelf;
        else
            _participants.Add(session, new Participant
            {
                Capture = new VoiceCapture(),
                Token = string.Empty,
                HearSelf = message.HearSelf,
            });
    }

    private void OnInputMode(SetVoiceInputModeEvent message, EntitySessionEventArgs args)
    {
        if (!_enabled || message.Mode > VoiceInputMode.Muted ||
            !_participants.TryGetValue(args.SenderSession, out var participant))
            return;

        participant.PushToTalkChannel = message.Mode == VoiceInputMode.Radio &&
            args.SenderSession.AttachedEntity is {} actor
            ? participant.RadioChannel ?? System.Linq.Enumerable.FirstOrDefault(AvailableRadioChannels(actor))
            : null;
        participant.Capture.Mode = message.Mode;
    }

    private string GetMicrophonePageUrl(INetChannel channel)
    {
        var url = _config.GetCVar(ProximityVoiceCVars.PublicUrl);
        if (url.Length != 0 || !IPAddress.IsLoopback(channel.RemoteEndPoint.Address))
            return url;

        // The status host accepts an address:port prefix (including *:port or [::1]:port).
        var bind = _config.GetCVar(Robust.Shared.CVars.StatusBind);
        var port = int.TryParse(bind.AsSpan(bind.LastIndexOf(':') + 1), out var boundPort) ? boundPort : _net.Port;
        return $"http://localhost:{port}/voice";
    }

    private VoiceCapture? ClaimTicket(string token, string browserKey)
    {
        lock (_tickets)
        {
            if (!_enabled || !_tickets.TryGetValue(token, out var participant) ||
                !participant.Capture.AuthorizeBrowser(browserKey, Environment.TickCount64 - participant.IssuedAt <= 120_000))
                return null;

            return participant.Capture;
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_enabled || _participants.Count == 0)
            return;

        var range = Math.Clamp(_config.GetCVar(ProximityVoiceCVars.Range), 1, 64);
        var now = Environment.TickCount64;
        foreach (var (session, participant) in _participants)
        {
            _frames.Clear();
            participant.Capture.DrainInto(_frames);
            if (_frames.Count == 0 || session.AttachedEntity is not {} speaker)
                continue;

            var audible = TryGetSpeechLevels(participant.LevelAnalyzer, out var levels);
            var dead = IsDeadVoice(speaker);
            var language = dead ? null : _language.GetLanguage(speaker);
            var blocked = _ticker.RunLevel != GameRunLevel.InRound || !dead &&
                (!CanParticipate(speaker) || !TryComp<SpeechComponent>(speaker, out var speech) || !speech.Enabled ||
                 language == null || !language.SpeechOverride.RequireSpeech ||
                 language.SpeechOverride.ChatTypeOverride is not (null or InGameICChatType.Speak) ||
                 !_language.CanSpeak(speaker, language.ID));

            if (!blocked && !dead && (participant.NextSpeechAttempt > now || !_action.CanSpeak(speaker)))
            {
                if (participant.NextSpeechAttempt <= now)
                    participant.NextSpeechAttempt = now + 1000;
                blocked = true;
            }

            if (audible && (now >= participant.NextTalkState || participant.TalkBlocked != blocked))
            {
                participant.NextTalkState = now + 110;
                participant.TalkBlocked = blocked;
                RaiseNetworkEvent(new VoiceTalkStateEvent(blocked, levels.Low, levels.Mid, levels.High, levels.Overall), session);
            }
            if (blocked)
                continue;

            var ai = !dead && HasComp<StationAiHeldComponent>(speaker);
            var directRoute = VoiceDeliveryRoute.Local;
            var emitter = ai ? FindAiEmitter(speaker, range, out directRoute) : speaker;
            var origin = _transform.GetMapCoordinates(emitter ?? speaker);
            if (origin.MapId == MapId.Nullspace)
                continue;

            var source = GetNetEntity(speaker);
            var visual = language?.SpeechOverride.RequireLOS ?? false;
            var devices = !dead && !visual;
            var megaphone = devices && HasActiveMegaphone(speaker);
            if (megaphone && directRoute == VoiceDeliveryRoute.Local)
                directRoute = VoiceDeliveryRoute.Megaphone;
            var directRange = directRoute == VoiceDeliveryRoute.Megaphone ? VoiceCodec.MegaphoneRange(range) : range;
            CollectBroadcastRecipients(speaker, devices);
            var radio = devices ? GetVoiceTransmission(speaker, participant) : null;
            if (devices)
            {
                if (participant.Capture.Mode == VoiceInputMode.Browser)
                    radio ??= GetNearbyRadioMicrophone(speaker, origin);
                CollectPhoneReceivers(speaker, origin);
                CollectHolopadRelays(speaker, emitter ?? speaker, origin, range, audible);
            }
            CollectRadioSpeakers(radio);

            _destinations.Clear();
            if (participant.HearSelf)
                AddDestination(VoiceDeliveryRoute.Sidetone, NetEntity.Invalid, session.Channel);
            foreach (var other in _participants.Keys)
            {
                if (other == session || other.AttachedEntity is not {} listener ||
                    (dead ? !IsDeadVoice(listener) : !CanParticipate(listener) || HasComp<DeafComponent>(listener)))
                    continue;

                var position = _transform.GetMapCoordinates(listener);
                var local = emitter is { } localEmitter && origin.InRange(position, directRange) &&
                    (!visual || _examine.InRangeUnOccluded(localEmitter, listener, range));
                if (dead)
                {
                    if (local)
                        AddDestination(VoiceDeliveryRoute.Dead, source, other.Channel);
                    continue;
                }
                var camera = HasComp<StationAiHeldComponent>(listener) ? FindAiEmitter(listener, range, out _) : null;
                var cameraPosition = camera is { } cameraUid ? _transform.GetMapCoordinates(cameraUid) : MapCoordinates.Nullspace;
                var monitor = devices && emitter != null && camera != null && origin.InRange(cameraPosition, range);
                var unfamiliar = !_language.CanUnderstand(listener, language!.ID);
                if (unfamiliar)
                {
                    if (local && now >= participant.NextUnfamiliarCue.GetValueOrDefault(other) && audible)
                    {
                        participant.NextUnfamiliarCue[other] = now + 3000;
                        var visibleSource = _examine.CanExamine(listener, speaker) ? source : NetEntity.Invalid;
                        RaiseNetworkEvent(new VoiceUnfamiliarSpeechEvent(visibleSource), other);
                    }
                }

                if (local)
                    AddDestination(directRoute, GetNetEntity(emitter!.Value), other.Channel, unfamiliar);
                else if (monitor)
                    AddDestination(VoiceDeliveryRoute.Monitor, NetEntity.Invalid, other.Channel, unfamiliar);
                else if (devices && FindHolopadRelay(position) is {} holopad)
                    AddDestination(VoiceDeliveryRoute.Holopad, GetNetEntity(holopad), other.Channel, unfamiliar);
                else if (_broadcastRecipients.Contains(other))
                    AddDestination(VoiceDeliveryRoute.Broadcast, NetEntity.Invalid, other.Channel, unfamiliar);
                else if (radio is {} transmission && ReceivesHeadsetVoice(listener, transmission))
                    AddDestination(VoiceDeliveryRoute.Radio, NetEntity.Invalid, other.Channel, unfamiliar);
                else if (GetRadioSpeaker(position, range) is {} radioSpeaker)
                    AddDestination(VoiceDeliveryRoute.RadioSpeaker, GetNetEntity(radioSpeaker), other.Channel, unfamiliar);
                else if (devices && TryGetPhoneReceiver(listener, position, range, out var receiver, out var privateHandset))
                {
                    if (privateHandset)
                        AddDestination(VoiceDeliveryRoute.Handset, NetEntity.Invalid, other.Channel, unfamiliar);
                    else
                        AddDestination(VoiceDeliveryRoute.Speakerphone, GetNetEntity(receiver), other.Channel, unfamiliar);
                }
            }

            if (_destinations.Count == 0)
                continue;

            var needsReplacement = false;
            var needsClearAudio = false;
            var speakerName = string.Empty;
            foreach (var destination in _destinations.Keys)
            {
                needsReplacement |= destination.Unfamiliar;
                needsClearAudio |= !destination.Unfamiliar;
                if (speakerName.Length == 0 && destination.Route is VoiceDeliveryRoute.Holopad or VoiceDeliveryRoute.Broadcast)
                {
                    var name = Name(Identity.Entity(speaker, EntityManager));
                    speakerName = name.Length > 80 ? name[..80] : name;
                }
            }

            UpdateVoiceEffect(participant, speaker, dead, megaphone);
            foreach (var frame in _frames)
            {
                var replacement = needsReplacement ? participant.UnfamiliarFilter.Replace(frame) : null;
                if (needsClearAudio)
                    ApplyVoiceEffect(participant, frame);
                var sequence = participant.Sequence++;
                foreach (var (destination, listeners) in _destinations)
                {
                    _net.ServerSendToMany(new MsgProximityVoiceFrame
                    {
                        Source = source,
                        Emitter = destination.Emitter,
                        Route = destination.Route,
                        Audible = audible,
                        DeadPosition = dead ? origin : default,
                        RadioChannel = destination.Route is VoiceDeliveryRoute.Radio or VoiceDeliveryRoute.RadioSpeaker
                            ? radio?.Channel.ID ?? string.Empty
                            : string.Empty,
                        SpeakerName = destination.Route is VoiceDeliveryRoute.Holopad or VoiceDeliveryRoute.Broadcast
                            ? speakerName
                            : string.Empty,
                        Sequence = sequence,
                        Payload = destination.Unfamiliar ? replacement! : frame,
                    }, listeners);
                }
            }
        }
    }

    private void AddDestination(VoiceDeliveryRoute route, NetEntity emitter, INetChannel listener, bool unfamiliar = false)
    {
        if (!_destinations.TryGetValue((route, emitter, unfamiliar), out var listeners))
            _destinations[(route, emitter, unfamiliar)] = listeners = new List<INetChannel>();
        listeners.Add(listener);
    }

    // Match the browser's voice-activation threshold for speech indicators.
    private bool TryGetSpeechLevels(VoiceLevelAnalyzer analyzer, out VoiceLevels levels)
    {
        Span<short> samples = stackalloc short[VoiceCodec.FrameSamples];
        foreach (var frame in _frames)
        {
            if (!VoiceCodec.Decode(frame, samples))
                continue;
            long energy = 0;
            foreach (var sample in samples)
                energy += sample * sample;
            if (energy > samples.Length * (32768 * 0.01) * (32768 * 0.01))
            {
                levels = analyzer.Analyze(samples);
                return true;
            }
        }
        levels = default;
        return false;
    }

    private bool CanParticipate(EntityUid uid)
        => !TerminatingOrDeleted(uid) && !HasComp<GhostComponent>(uid) && _mob.IsAlive(uid);

    private bool IsDeadVoice(EntityUid uid)
        => !TerminatingOrDeleted(uid) && (HasComp<GhostComponent>(uid) || _mob.IsDead(uid));
}
