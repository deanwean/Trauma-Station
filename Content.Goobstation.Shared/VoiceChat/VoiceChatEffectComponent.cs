// SPDX-License-Identifier: AGPL-3.0-or-later
// Ported from Goob Station voice chat.

namespace Content.Goobstation.Shared.VoiceChat;

public enum VoiceEffect : byte
{
    None,
    Robot,
    Alien,
    Deep,
    High,
    Radio,
    Muffled,
    Abductor,
    Whisper,
}

[RegisterComponent]
public sealed partial class VoiceChatEffectComponent : Component
{
    [DataField]
    public VoiceEffect Effect = VoiceEffect.None;
}
