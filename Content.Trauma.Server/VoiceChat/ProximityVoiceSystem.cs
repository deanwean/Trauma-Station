// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Security.Cryptography;
using System.Threading;
using Content.Goobstation.Common.Traits;
using Content.Goobstation.Shared.VoiceChat;
using Content.Shared.ActionBlocker;
using Content.Shared.Chat;
using Content.Shared.Examine;
using Content.Shared.GameTicking;
using Content.Shared.Ghost.Components;
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
        public ushort Sequence;
    }

    // Only ticket redemption and VoiceCapture's bounded queue run on HTTP worker threads.
    private readonly Dictionary<string, Participant> _tickets = new(StringComparer.Ordinal);
    private readonly Dictionary<ICommonSession, Participant> _participants = new();
    private readonly List<INetChannel> _listeners = new();
    private readonly List<byte[]> _frames = new();
    private readonly CancellationTokenSource _shutdown = new();
    private volatile bool _enabled;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<RequestVoicePairingEvent>(OnPairingRequested);
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
        participant.Capture.Dispose();
    }

    private void OnPairingRequested(RequestVoicePairingEvent ev, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        var now = Environment.TickCount64;
        if (!_enabled || !Uri.TryCreate(_config.GetCVar(ProximityVoiceCVars.PublicUrl), UriKind.Absolute, out var page)
            || !(page.Scheme == Uri.UriSchemeHttps || page.Scheme == Uri.UriSchemeHttp && page.IsLoopback)
            || page.UserInfo.Length != 0 || page.Query.Length != 0 || page.Fragment.Length != 0
            || session.Status != SessionStatus.InGame)
        {
            RaiseNetworkEvent(new VoicePairingResponseEvent(string.Empty), session);
            return;
        }

        if (_participants.TryGetValue(session, out var previous) && now - previous.IssuedAt < 1000)
        {
            // Repeated clicks should not invalidate a page that has just opened.
            return;
        }

        Remove(session);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var participant = new Participant
        {
            Capture = new VoiceCapture(),
            Token = token,
            IssuedAt = now,
            Sequence = previous?.Sequence ?? 0,
        };
        _participants.Add(session, participant);
        lock (_tickets)
        {
            _tickets.Add(token, participant);
        }
        RaiseNetworkEvent(new VoicePairingResponseEvent(new UriBuilder(page) { Fragment = token }.Uri.AbsoluteUri), session);
    }

    private VoiceCapture? ClaimTicket(string token)
    {
        lock (_tickets)
        {
            if (!_enabled || !_tickets.Remove(token, out var participant)
                || Environment.TickCount64 - participant.IssuedAt > 120_000)
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
            if (_frames.Count == 0 || _ticker.RunLevel != GameRunLevel.InRound
                || participant.NextSpeechAttempt > now || session.AttachedEntity is not {} speaker
                || !CanParticipate(speaker) || !TryComp<SpeechComponent>(speaker, out var speech) || !speech.Enabled)
                continue;

            var language = _language.GetLanguage(speaker);
            // Physical microphones only carry ordinary, spoken local languages.
            if (!language.SpeechOverride.RequireSpeech || language.SpeechOverride.ChatTypeOverride is not (null or InGameICChatType.Speak)
                || !_language.CanSpeak(speaker, language.ID))
                continue;

            if (!_action.CanSpeak(speaker))
            {
                // Some blockers display a popup. Avoid showing it for every 20 ms of audio.
                participant.NextSpeechAttempt = now + 1000;
                continue;
            }

            var origin = _transform.GetMapCoordinates(speaker);
            if (origin.MapId == MapId.Nullspace)
                continue;

            _listeners.Clear();
            foreach (var other in _participants.Keys)
            {
                if (other == session || other.AttachedEntity is not {} listener || !CanParticipate(listener)
                    || HasComp<DeafComponent>(listener) || !origin.InRange(_transform.GetMapCoordinates(listener), range)
                    || !_language.CanUnderstand(listener, language.ID)
                    || language.SpeechOverride.RequireLOS && !_examine.InRangeUnOccluded(speaker, listener, range))
                    continue;

                _listeners.Add(other.Channel);
            }

            if (_listeners.Count == 0)
                continue;

            var source = GetNetEntity(speaker);
            foreach (var frame in _frames)
            {
                _net.ServerSendToMany(new MsgProximityVoiceFrame
                {
                    Source = source,
                    Sequence = participant.Sequence++,
                    Payload = frame,
                }, _listeners);
            }
        }
    }

    private bool CanParticipate(EntityUid uid)
        => !TerminatingOrDeleted(uid) && !HasComp<GhostComponent>(uid) && _mob.IsAlive(uid);
}
