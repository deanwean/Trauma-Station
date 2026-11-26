// SPDX-License-Identifier: AGPL-3.0-or-later

using Lidgren.Network;
using Robust.Shared.Map;

namespace Content.Goobstation.Shared.VoiceChat;

[Serializable, NetSerializable]
public sealed class RequestVoicePairingEvent : EntityEventArgs;

[Serializable, NetSerializable]
public sealed class JoinVoiceListeningEvent(bool hearSelf) : EntityEventArgs
{
    public readonly bool HearSelf = hearSelf;
}

[Serializable, NetSerializable]
public enum VoiceInputMode : byte
{
    Browser,
    Local,
    Radio,
    Muted,
}

[Serializable, NetSerializable]
public sealed class SetVoiceInputModeEvent(VoiceInputMode mode) : EntityEventArgs
{
    public readonly VoiceInputMode Mode = mode;
}

[Serializable, NetSerializable]
public sealed class LeaveProximityVoiceEvent : EntityEventArgs;

[Serializable, NetSerializable]
public sealed class VoicePairingResponseEvent(string url) : EntityEventArgs
{
    public readonly string Url = url;
}

[Serializable, NetSerializable]
public sealed class VoiceRadioSelectEvent(string? channel, bool queryOnly = false) : EntityEventArgs
{
    public readonly string? Channel = channel;
    public readonly bool QueryOnly = queryOnly;
}

[Serializable, NetSerializable]
public sealed class VoiceRadioStatusEvent(string[] channels, string? selected, bool micConnected = false) : EntityEventArgs
{
    public readonly string[] Channels = channels;
    public readonly string? Selected = selected;
    public readonly bool MicConnected = micConnected;
}

[Serializable, NetSerializable]
public sealed class VoiceUnfamiliarSpeechEvent(NetEntity source) : EntityEventArgs
{
    public readonly NetEntity Source = source;
}

[Serializable, NetSerializable]
public sealed class VoiceTalkStateEvent(bool blocked, float low, float mid, float high, float overall) : EntityEventArgs
{
    public readonly bool Blocked = blocked;
    public readonly float Low = low;
    public readonly float Mid = mid;
    public readonly float High = high;
    public readonly float Overall = overall;
}

public enum VoiceDeliveryRoute : byte
{
    Local,
    Radio,
    Handset,
    Speakerphone,
    Dead,
    RadioSpeaker,
    Holopad,
    Camera,
    Broadcast,
    Monitor,
    Sidetone,
    Megaphone,
}

/// <summary>A self-contained 20 ms frame. A dropped packet never blocks newer speech.</summary>
public sealed class MsgProximityVoiceFrame : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.Core;
    public override NetDeliveryMethod DeliveryMethod => NetDeliveryMethod.Unreliable;

    public NetEntity Source;
    // A distant caller need not be visible. Spatial playback follows the local emitter.
    public NetEntity Emitter;
    public VoiceDeliveryRoute Route;
    public bool Audible;
    public MapCoordinates DeadPosition;
    public string RadioChannel = string.Empty;
    public ushort Sequence;
    public byte[] Payload = [];

    private const byte AudibleFlag = 0x80;
    private const int BaseSize = 2 * sizeof(int) + sizeof(byte) + sizeof(ushort) + VoiceCodec.FrameBytes;
    private const int PositionSize = sizeof(int) + 2 * sizeof(float);

    private bool IncludesChannel => Route is VoiceDeliveryRoute.Radio or VoiceDeliveryRoute.RadioSpeaker;

    public override int EstimateBufferSize() => BaseSize + (Route == VoiceDeliveryRoute.Dead ? PositionSize : 0) +
        (IncludesChannel ? System.Text.Encoding.UTF8.GetByteCount(RadioChannel) + sizeof(int) : 0);

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
    {
        buffer.Write(Source);
        buffer.Write(Emitter);
        buffer.Write((byte) ((byte) Route | (Audible ? AudibleFlag : 0)));
        if (Route == VoiceDeliveryRoute.Dead)
        {
            buffer.Write(DeadPosition.Position);
            buffer.Write((int) DeadPosition.MapId);
        }
        buffer.Write(Sequence);
        buffer.Write(Payload);
        if (IncludesChannel)
            buffer.Write(RadioChannel);
    }

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
    {
        var bits = buffer.LengthBits - buffer.Position;
        if (bits < BaseSize * 8)
            return;

        Source = buffer.ReadNetEntity();
        Emitter = buffer.ReadNetEntity();
        var flags = buffer.ReadByte();
        Route = (VoiceDeliveryRoute) (flags & ~AudibleFlag);
        Audible = (flags & AudibleFlag) != 0;
        if (Route > VoiceDeliveryRoute.Megaphone ||
            (IncludesChannel ? bits <= BaseSize * 8 : bits != EstimateBufferSize() * 8))
            return;
        if (Route == VoiceDeliveryRoute.Dead)
            DeadPosition = new MapCoordinates(buffer.ReadVector2(), new MapId(buffer.ReadInt32()));
        Sequence = buffer.ReadUInt16();
        Payload = buffer.ReadBytes(VoiceCodec.FrameBytes);
        if (IncludesChannel)
            RadioChannel = buffer.ReadString();
    }
}
