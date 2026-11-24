// SPDX-License-Identifier: AGPL-3.0-or-later

using Lidgren.Network;

namespace Content.Goobstation.Shared.VoiceChat;

[Serializable, NetSerializable]
public sealed class RequestVoicePairingEvent : EntityEventArgs;

[Serializable, NetSerializable]
public sealed class LeaveProximityVoiceEvent : EntityEventArgs;

[Serializable, NetSerializable]
public sealed class VoicePairingResponseEvent(string url) : EntityEventArgs
{
    public readonly string Url = url;
}

/// <summary>A self-contained 20 ms frame. A dropped packet never blocks newer speech.</summary>
public sealed class MsgProximityVoiceFrame : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.Core;
    public override NetDeliveryMethod DeliveryMethod => NetDeliveryMethod.Unreliable;

    public NetEntity Source;
    public ushort Sequence;
    public byte[] Payload = [];

    public override int EstimateBufferSize() => sizeof(int) + sizeof(ushort) + VoiceCodec.FrameBytes;

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
    {
        buffer.Write(Source);
        buffer.Write(Sequence);
        buffer.Write(Payload);
    }

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
    {
        if (buffer.LengthBits - buffer.Position != EstimateBufferSize() * 8)
            return;

        Source = buffer.ReadNetEntity();
        Sequence = buffer.ReadUInt16();
        Payload = buffer.ReadBytes(VoiceCodec.FrameBytes);
    }
}
