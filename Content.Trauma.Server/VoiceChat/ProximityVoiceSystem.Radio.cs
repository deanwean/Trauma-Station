// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Goobstation.Shared.VoiceChat;
using Content.Server.Radio.EntitySystems;
using Content.Shared.Chat;
using Content.Shared.Radio;
using Content.Shared.Radio.Components;
using Content.Shared.Radio.EntitySystems;
using Content.Shared.Speech.Components;
using Robust.Shared.Map;
using Content.Shared.Whitelist;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Trauma.Server.VoiceChat;

public sealed partial class ProximityVoiceSystem
{
    [Dependency] private RadioSystem _radio = default!;
    [Dependency] private EntityWhitelistSystem _radioWhitelist = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedRadioDeviceSystem _radioDevices = default!;
    private readonly List<(EntityUid Emitter, MapCoordinates Position)> _radioSpeakers = new();

    private RadioSystem.RadioTransmission? GetNearbyRadioMicrophone(EntityUid speaker, MapCoordinates origin)
    {
        if (!_config.GetCVar(ProximityVoiceCVars.RadioEnabled) || HasComp<RadioSpeakerComponent>(speaker))
            return null;

        var microphones = EntityQueryEnumerator<RadioMicrophoneComponent, ActiveListenerComponent>();
        var nearest = float.MaxValue;
        RadioSystem.RadioTransmission? result = null;
        while (microphones.MoveNext(out var uid, out var mic, out var active))
        {
            var position = _transform.GetMapCoordinates(uid);
            var distance = (origin.Position - position.Position).LengthSquared();
            if (position.MapId != origin.MapId || distance > active.Range * active.Range || distance >= nearest ||
                !IsRadioChannelEnabled(mic.BroadcastChannel.Id) ||
                !_prototypes.TryIndex(mic.BroadcastChannel, out var channel) ||
                !_radioWhitelist.IsWhitelistPassOrNull(channel.SendWhitelist, speaker) ||
                !_radioDevices.CanPickUpSpeech((uid, mic), speaker) ||
                !_radio.TryStartTransmission(speaker, uid, channel, out var transmission))
                continue;

            nearest = distance;
            result = transmission;
        }
        return result;
    }

    private void CollectRadioSpeakers(RadioSystem.RadioTransmission? radio)
    {
        _radioSpeakers.Clear();
        if (radio is not {} transmission)
            return;

        var speakers = EntityQueryEnumerator<RadioSpeakerComponent>();
        while (speakers.MoveNext(out var uid, out var speaker))
        {
            if (uid != transmission.Source && _radioDevices.IsVoiceSpeakerEnabled((uid, speaker)) &&
                _radio.CanReceiveTransmission(transmission, uid))
                _radioSpeakers.Add((uid, _transform.GetMapCoordinates(uid)));
        }
    }

    private EntityUid? GetRadioSpeaker(MapCoordinates listener, int range)
    {
        EntityUid? result = null;
        var closest = (float) (range * range);
        foreach (var (emitter, position) in _radioSpeakers)
        {
            var distance = (listener.Position - position.Position).LengthSquared();
            if (listener.MapId != position.MapId || distance >= closest)
                continue;
            closest = distance;
            result = emitter;
        }
        return result;
    }

    private void OnRadioSelect(VoiceRadioSelectEvent request, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (!_enabled || !_participants.TryGetValue(session, out var participant))
            return;

        var channels = session.AttachedEntity is {} actor
            ? AvailableRadioChannels(actor)
            : Array.Empty<string>();
        var previous = participant.RadioChannel;
        if (participant.RadioChannel is {} old && !channels.Contains(old))
            participant.RadioChannel = null;

        if (!request.QueryOnly)
        {
            if (request.Channel == null)
                participant.RadioChannel = null;
            else if (channels.FirstOrDefault(id => string.Equals(id, request.Channel, StringComparison.OrdinalIgnoreCase)) is {} selected)
                participant.RadioChannel = selected;
        }

        if (previous != participant.RadioChannel)
            participant.Capture.Clear();

        RaiseNetworkEvent(new VoiceRadioStatusEvent(channels, participant.RadioChannel, participant.Capture.IsConnected), session);
    }

    private string[] AvailableRadioChannels(EntityUid actor)
    {
        if (!_config.GetCVar(ProximityVoiceCVars.RadioEnabled))
            return Array.Empty<string>();

        var channels = new HashSet<string>();
        if (TryGetWornHeadset(actor, out var headset) && TryComp<EncryptionKeyHolderComponent>(headset, out var keys))
            AddAllowed(keys.Channels);
        if (TryComp<IntrinsicRadioTransmitterComponent>(actor, out var intrinsic))
            AddAllowed(intrinsic.Channels);
        return channels.Order(StringComparer.Ordinal).ToArray();

        void AddAllowed(HashSet<ProtoId<RadioChannelPrototype>> candidates)
        {
            foreach (var id in candidates)
            {
                if (IsRadioChannelEnabled(id.Id) && _prototypes.TryIndex(id, out var channel) && _radioWhitelist.IsWhitelistPassOrNull(channel.SendWhitelist, actor))
                    channels.Add(channel.ID);
            }
        }
    }

    private bool IsRadioChannelEnabled(string id)
    {
        return _config.GetCVar(ProximityVoiceCVars.RadioEnabled) &&
               (id != SharedChatSystem.CommonChannel.Id || _config.GetCVar(ProximityVoiceCVars.RadioCommon));
    }

    private void ClearDisabledRadioChannels()
    {
        foreach (var (session, participant) in _participants)
        {
            if (participant.RadioChannel is not {} id || IsRadioChannelEnabled(id))
                continue;

            participant.RadioChannel = null;
            participant.Capture.Clear();
            var channels = session.AttachedEntity is {} actor ? AvailableRadioChannels(actor) : Array.Empty<string>();
            RaiseNetworkEvent(new VoiceRadioStatusEvent(channels, null, participant.Capture.IsConnected), session);
        }
    }

    private bool TryGetWornHeadset(EntityUid uid, out EntityUid headset)
    {
        if (TryComp<WearingHeadsetComponent>(uid, out var wearing) &&
            TryComp<HeadsetComponent>(wearing.Headset, out var radio) && radio.Enabled && radio.IsEquipped &&
            Transform(wearing.Headset).ParentUid == uid)
        {
            headset = wearing.Headset;
            return true;
        }
        headset = default;
        return false;
    }

    private RadioSystem.RadioTransmission? GetVoiceTransmission(EntityUid speaker, Participant participant)
    {
        var selected = participant.Capture.Mode switch
        {
            VoiceInputMode.Radio => participant.PushToTalkChannel,
            VoiceInputMode.Browser => participant.RadioChannel,
            _ => null,
        };
        if (selected is not {} id || !IsRadioChannelEnabled(id) ||
            !_prototypes.TryIndex<RadioChannelPrototype>(id, out var channel) ||
            !_radioWhitelist.IsWhitelistPassOrNull(channel.SendWhitelist, speaker))
            return null;

        EntityUid source;
        if (TryGetWornHeadset(speaker, out var headset) &&
            TryComp<EncryptionKeyHolderComponent>(headset, out var keys) && keys.Channels.Contains(id))
            source = headset;
        else if (TryComp<IntrinsicRadioTransmitterComponent>(speaker, out var intrinsic) && intrinsic.Channels.Contains(id))
            source = speaker;
        else
            return null;

        return _radio.TryStartTransmission(speaker, source, channel, out var transmission) ? transmission : null;
    }

    private bool ReceivesHeadsetVoice(EntityUid listener, RadioSystem.RadioTransmission transmission)
    {
        return (TryGetWornHeadset(listener, out var headset) && _radio.CanReceiveTransmission(transmission, headset)) ||
               (HasComp<IntrinsicRadioReceiverComponent>(listener) && _radio.CanReceiveTransmission(transmission, listener));
    }
}
