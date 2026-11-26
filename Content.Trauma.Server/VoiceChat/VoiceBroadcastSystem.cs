// SPDX-License-Identifier: AGPL-3.0-or-later
// Ported from Goob Station voice chat.

using Content.Goobstation.Shared.VoiceChat;
using Content.Server.Administration.Logs;
using Content.Server.Communications;
using Content.Server.Power.EntitySystems;
using Content.Shared.Station.Systems;
using Content.Shared.GameTicking;
using Content.Shared.Silicons.StationAi;
using Content.Shared.Access.Systems;
using Content.Shared.Database;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Robust.Server.Player;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Trauma.Server.VoiceChat;

public sealed partial class VoiceBroadcastSystem : EntitySystem
{
    [Dependency] private IAdminLogManager _adminLog = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private ProximityVoiceSystem _voice = default!;
    [Dependency] private AccessReaderSystem _access = default!;
    [Dependency] private PowerReceiverSystem _power = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private StationSystem _station = default!;

    [Dependency] private GameTicker _ticker = default!;

    private readonly Dictionary<EntityUid, EntityUid> _active = new();
    private readonly List<EntityUid> _toStop = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CommunicationsConsoleComponent, VoiceBroadcastToggleMessage>(OnToggle);
        SubscribeLocalEvent<VoiceBroadcastConsoleComponent, ComponentShutdown>(OnShutdown);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_active.Count == 0)
            return;

        var now = _timing.CurTime;
        foreach (var (user, console) in _active)
        {
            if (!TryComp<VoiceBroadcastConsoleComponent>(console, out var broadcast) ||
                now >= broadcast.EndTime ||
                TerminatingOrDeleted(user) ||
                !CanOperate(user, console) ||
                !_player.TryGetSessionByEntity(user, out var session) ||
                session.Status != SessionStatus.InGame || !_voice.HasConnectedMicrophone(session))
            {
                _toStop.Add(user);
            }
        }

        foreach (var user in _toStop)
        {
            if (_active.Remove(user, out var console) && TryComp<VoiceBroadcastConsoleComponent>(console, out var broadcast))
                Stop((console, broadcast), user);
        }

        _toStop.Clear();
    }

    public bool TryGetBroadcastConsole(EntityUid user, out EntityUid console)
    {
        return _active.TryGetValue(user, out console) &&
            TryComp<VoiceBroadcastConsoleComponent>(console, out var broadcast) &&
            _timing.CurTime < broadcast.EndTime && CanOperate(user, console);
    }

    public void GetRecipients(EntityUid console, HashSet<ICommonSession> recipients)
    {
        if (TryComp<CommunicationsConsoleComponent>(console, out var comms) && comms.Global)
        {
            foreach (var session in _player.Sessions)
            {
                if (session.Status == SessionStatus.InGame)
                    recipients.Add(session);
            }

            return;
        }

        foreach (var session in _station.GetInOwningStation(console).Recipients)
        {
            recipients.Add(session);
        }
    }

    private void OnShutdown(Entity<VoiceBroadcastConsoleComponent> ent, ref ComponentShutdown args)
    {
        if (ent.Comp.Broadcaster is { } user)
            _active.Remove(user);
    }

    private void OnToggle(Entity<CommunicationsConsoleComponent> ent, ref VoiceBroadcastToggleMessage args)
    {
        if (!TryComp<VoiceBroadcastConsoleComponent>(ent, out var broadcast))
            return;

        var user = args.Actor;
        var console = new Entity<VoiceBroadcastConsoleComponent>(ent, broadcast);

        if (broadcast.Broadcaster == user)
        {
            _active.Remove(user);
            Stop(console, user);
            return;
        }

        if (broadcast.Broadcaster != null || _timing.CurTime < broadcast.NextBroadcast || !_cfg.GetCVar(ProximityVoiceCVars.Enabled) || _ticker.RunLevel != GameRunLevel.InRound)
            return;

        if (!CanOperate(user, ent))
        {
            _popup.PopupEntity(Loc.GetString("voice-broadcast-denied"), ent, user, PopupType.SmallCaution);
            return;
        }

        if (!_player.TryGetSessionByEntity(user, out var session) || !_voice.HasConnectedMicrophone(session))
        {
            _popup.PopupEntity(Loc.GetString("voice-broadcast-not-connected"), ent, user, PopupType.SmallCaution);
            return;
        }

        if (_active.ContainsKey(user))
            return;

        broadcast.Broadcaster = user;
        broadcast.EndTime = _timing.CurTime + broadcast.Duration;
        Dirty(console);
        _active[user] = ent;

        var recipients = new HashSet<ICommonSession>();
        GetRecipients(ent, recipients);
        _audio.PlayGlobal(ent.Comp.Sound, Filter.Empty().AddPlayers(recipients), true);

        _popup.PopupEntity(Loc.GetString("voice-broadcast-started", ("seconds", (int) broadcast.Duration.TotalSeconds)), ent, user);
        _adminLog.Add(LogType.Chat, LogImpact.Medium, $"{ToPrettyString(user):user} started a station voice broadcast from {ToPrettyString(ent):console}");
    }

    private bool CanOperate(EntityUid user, EntityUid console)
    {
        if (TerminatingOrDeleted(user) || TerminatingOrDeleted(console) || !_access.IsAllowed(user, console))
            return false;

        if (user == console && HasComp<StationAiHeldComponent>(user))
            return true;

        return _power.IsPowered(console) && _interaction.InRangeUnobstructed(user, console);
    }

    private void Stop(Entity<VoiceBroadcastConsoleComponent> console, EntityUid user)
    {
        if (console.Comp.Broadcaster != user)
            return;

        console.Comp.Broadcaster = null;
        console.Comp.NextBroadcast = _timing.CurTime + console.Comp.Cooldown;
        Dirty(console);

        if (!TerminatingOrDeleted(user))
            _popup.PopupEntity(Loc.GetString("voice-broadcast-ended"), console, user);

        _adminLog.Add(LogType.Chat, LogImpact.Low, $"{ToPrettyString(user):user} ended a station voice broadcast from {ToPrettyString(console):console}");
    }
}
