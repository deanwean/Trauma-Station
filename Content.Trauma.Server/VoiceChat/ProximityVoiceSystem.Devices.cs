// SPDX-License-Identifier: AGPL-3.0-or-later
// Camera selection and holopad relays adapted from Goob Station's VoiceChatSystem.

using System.Numerics;
using Content.Goobstation.Server.VoiceChat;
using Content.Goobstation.Shared.Loudspeaker.Components;
using Content.Shared.Clothing.Components;
using Content.Shared.Inventory;
using Content.Shared.Nutrition.Components;
using Content.Goobstation.Shared.VoiceChat;
using Content.Server.Power.EntitySystems;
using Content.Server.Telephone;
using Content.Shared.Holopad;
using Content.Shared.Interaction;
using Content.Shared.Silicons.StationAi;
using Content.Shared.StationAi;
using Content.Shared.SurveillanceCamera.Components;
using Content.Shared.Telephone;
using Robust.Shared.Map;
using Robust.Shared.Player;

namespace Content.Trauma.Server.VoiceChat;

public sealed partial class ProximityVoiceSystem
{
    [Dependency] private SharedStationAiSystem _stationAi = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private TelephoneSystem _telephone = default!;
    [Dependency] private PowerReceiverSystem _power = default!;
    [Dependency] private VoiceBroadcastSystem _broadcast = default!;

    [Dependency] private InventorySystem _voiceInventory = default!;

    private readonly HashSet<Entity<StationAiVisionComponent>> _cameras = new();
    private readonly HashSet<ICommonSession> _broadcastRecipients = new();
    private readonly List<(EntityUid Emitter, MapCoordinates Position, float Range)> _holopadRelays = new();
    private readonly short[] _effectSamples = new short[VoiceCodec.FrameSamples];

    internal bool HasConnectedMicrophone(ICommonSession session)
        => _enabled && _participants.TryGetValue(session, out var participant) && participant.Capture.IsConnected;

    private void CollectBroadcastRecipients(EntityUid speaker, bool devices)
    {
        _broadcastRecipients.Clear();
        if (devices && _broadcast.TryGetBroadcastConsole(speaker, out var console))
            _broadcast.GetRecipients(console, _broadcastRecipients);
    }

    private EntityUid? FindAiEmitter(EntityUid speaker, float range, out VoiceDeliveryRoute route)
    {
        route = VoiceDeliveryRoute.Camera;
        if (!_stationAi.TryGetCore(speaker, out var core) || core.Comp?.RemoteEntity is not { } eye)
            return null;

        if (!core.Comp.Remote)
        {
            route = VoiceDeliveryRoute.Holopad;
            return eye;
        }

        var origin = _transform.GetMapCoordinates(eye);
        if (origin.MapId == MapId.Nullspace)
            return null;

        _cameras.Clear();
        _lookup.GetEntitiesInRange(origin, range, _cameras);
        EntityUid? best = null;
        var bestDistance = float.MaxValue;
        foreach (var camera in _cameras)
        {
            if (!camera.Comp.Enabled || !TryComp<SurveillanceCameraComponent>(camera, out var surveillance) ||
                !surveillance.Active || camera.Comp.NeedsPower && !_power.IsPowered(camera))
                continue;

            var xform = Transform(camera);
            if (camera.Comp.NeedsAnchoring && !xform.Anchored)
                continue;

            var distance = Vector2.DistanceSquared(_transform.GetWorldPosition(xform), origin.Position);
            if (distance > camera.Comp.Range * camera.Comp.Range || distance >= bestDistance)
                continue;

            bestDistance = distance;
            best = camera;
        }
        return best;
    }

    private void CollectHolopadRelays(EntityUid speaker, EntityUid input, MapCoordinates origin, int range, bool audible)
    {
        _holopadRelays.Clear();
        var query = EntityQueryEnumerator<HolopadComponent, TelephoneComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var pad, out var telephone, out var xform))
        {
            if (telephone.LinkedTelephones.Count == 0 || telephone.Muted ||
                telephone.CurrentState != TelephoneState.InCall ||
                !_telephone.IsTelephonePowered((uid, telephone)))
                continue;

            var isProjectedHere = pad.User == speaker;
            if (!isProjectedHere &&
                (xform.MapID != origin.MapId ||
                 Vector2.DistanceSquared(_transform.GetWorldPosition(xform), origin.Position) >
                 telephone.ListeningRange * telephone.ListeningRange ||
                 !_interaction.InRangeUnobstructed(input, uid, telephone.ListeningRange)))
                continue;

            foreach (var linked in telephone.LinkedTelephones)
            {
                if (linked == uid || TerminatingOrDeleted(linked) ||
                    !TryComp<HolopadComponent>(linked, out var linkedPad) ||
                    !TryComp<TelephoneComponent>(linked, out var linkedPhone) ||
                    linkedPhone.CurrentState != TelephoneState.InCall ||
                    !_telephone.IsTelephonePowered((linked, linkedPhone)))
                    continue;

                var emitter = linkedPad.Hologram ?? linkedPhone.Speaker ?? linked;
                if (TerminatingOrDeleted(emitter))
                    continue;
                var position = _transform.GetMapCoordinates(emitter);
                if (position.MapId == MapId.Nullspace)
                    continue;

                _holopadRelays.Add((emitter, position, linkedPhone.SpeakerVolume == TelephoneVolume.Speak ? range : 2f));
                if (audible)
                {
                    _telephone.KeepCallAlive((uid, telephone));
                    _telephone.KeepCallAlive((linked, linkedPhone));
                }
            }
        }
    }

    private EntityUid? FindHolopadRelay(MapCoordinates listener)
    {
        EntityUid? result = null;
        var closest = float.MaxValue;
        foreach (var (emitter, position, range) in _holopadRelays)
        {
            if (position.MapId != listener.MapId)
                continue;

            var distance = Vector2.DistanceSquared(position.Position, listener.Position);
            if (distance > range * range || distance >= closest)
                continue;

            closest = distance;
            result = emitter;
        }
        return result;
    }

    private bool HasActiveMegaphone(EntityUid speaker)
    {
        if (!TryComp<LoudspeakerHolderComponent>(speaker, out var holder))
            return false;

        foreach (var item in holder.Loudspeakers)
        {
            if (TryComp<LoudspeakerComponent>(item, out var loudspeaker) && loudspeaker.IsActive && loudspeaker.AffectChat)
                return true;
        }
        return false;
    }

    private VoiceMuffle GetVoiceMuffle(EntityUid speaker)
    {
        return (_voiceInventory.TryGetSlotEntity(speaker, "mask", out var mask) && CoversMouth(mask.Value)) ||
               (_voiceInventory.TryGetSlotEntity(speaker, "head", out var head) && CoversMouth(head.Value))
            ? VoiceMuffle.Light
            : VoiceMuffle.None;
    }

    private bool CoversMouth(EntityUid item)
        => TryComp<IngestionBlockerComponent>(item, out var blocker) && blocker.Enabled &&
           !(TryComp<MaskComponent>(item, out var mask) && mask.IsToggled);

    private void UpdateVoiceEffect(Participant participant, EntityUid speaker, bool dead, bool megaphone)
    {
        var effect = TryComp<VoiceChatEffectComponent>(speaker, out var builtIn) ? builtIn.Effect : VoiceEffect.None;
        var settings = dead ? default : new VoiceEffectSettings(effect, GetVoiceMuffle(speaker), megaphone);
        if (participant.AppliedEffect != settings)
        {
            participant.AppliedEffect = settings;
            participant.Effect = settings == default ? null : new VoiceEffectProcessor();
        }
    }

    private void ApplyVoiceEffect(Participant participant, byte[] frame)
    {
        if (participant.Effect == null || !VoiceCodec.Decode(frame, _effectSamples))
            return;

        participant.Effect.Process(_effectSamples, 0, _effectSamples.Length, participant.AppliedEffect);
        int predictor = _effectSamples[0];
        int index = frame[2];
        VoiceCodec.Encode(_effectSamples, ref predictor, ref index, frame);
    }
}
