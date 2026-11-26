// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Hands.EntitySystems;
using Content.Shared.Radio.Components;
using Content.Shared.Speech;
using Content.Shared.Speech.Components;
using Content.Trauma.Shared.Phones.Components;
using Robust.Shared.Map;

namespace Content.Trauma.Server.VoiceChat;

public sealed partial class ProximityVoiceSystem
{
    [Dependency] private SharedHandsSystem _phoneHands = default!;

    private readonly record struct PhoneReceiver(EntityUid Handset, MapCoordinates Position, EntityUid? PrivateHolder);
    private readonly List<PhoneReceiver> _phoneReceivers = new();

    private void CollectPhoneReceivers(EntityUid speaker, MapCoordinates origin)
    {
        _phoneReceivers.Clear();
        if (HasComp<RotaryPhoneComponent>(speaker) || HasComp<RadioSpeakerComponent>(speaker))
            return;

        var query = EntityQueryEnumerator<RotaryPhoneComponent, ActiveListenerComponent, TransformComponent>();
        while (query.MoveNext(out var handset, out var phone, out var mic, out var xform))
        {
            // Dialing sets ConnectedPhone before anyone has actually answered.
            if (!phone.Connected || phone.ConnectedPhone is not {} farHandset ||
                xform.MapID != origin.MapId || TerminatingOrDeleted(handset) || TerminatingOrDeleted(farHandset))
                continue;

            var distanceSquared = (_transform.GetWorldPosition(xform) - origin.Position).LengthSquared();
            if (distanceSquared > mic.Range * mic.Range ||
                !TryComp<RotaryPhoneComponent>(farHandset, out var farPhone) ||
                !farPhone.Connected || farPhone.ConnectedPhone != handset)
                continue;

            var attempt = new ListenAttemptEvent(speaker);
            RaiseLocalEvent(handset, attempt);
            if (attempt.Cancelled)
                continue;

            EntityUid? privateHolder = null;
            if (!farPhone.SpeakerPhone && !_phoneHands.IsHeld(farHandset, out privateHolder))
                continue;

            var position = _transform.GetMapCoordinates(farHandset);
            if (position.MapId != MapId.Nullspace)
                _phoneReceivers.Add(new PhoneReceiver(farHandset, position, privateHolder));
        }
    }

    private bool TryGetPhoneReceiver(EntityUid listener, MapCoordinates listenerPosition, int range,
        out EntityUid receiver, out bool privateHandset)
    {
        receiver = EntityUid.Invalid;
        privateHandset = false;
        var closestSquared = (float) (range * range);
        foreach (var phone in _phoneReceivers)
        {
            if (phone.PrivateHolder is {} holder)
            {
                if (holder != listener)
                    continue;

                receiver = phone.Handset;
                privateHandset = true;
                return true;
            }

            if (phone.Position.MapId != listenerPosition.MapId)
                continue;

            var distanceSquared = (phone.Position.Position - listenerPosition.Position).LengthSquared();
            if (distanceSquared >= closestSquared)
                continue;

            closestSquared = distanceSquared;
            receiver = phone.Handset;
        }

        return receiver.IsValid();
    }
}
