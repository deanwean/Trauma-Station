// SPDX-License-Identifier: AGPL-3.0-or-later
// Adapted from Goob Station's voice chat implementation.
using Content.Goobstation.Shared.VoiceChat;

namespace Content.Goobstation.Client.VoiceChat;

public static class VoiceLevelSmoothing
{
    private const float AttackTime = 0.025f;
    private const float ReleaseTime = 0.15f;

    public static void Apply(ref VoiceLevels current, VoiceLevels target, float frameTime)
    {
        var attack = 1f - MathF.Exp(-frameTime / AttackTime);
        var release = 1f - MathF.Exp(-frameTime / ReleaseTime);
        current.Low = Smooth(current.Low, target.Low, attack, release);
        current.Mid = Smooth(current.Mid, target.Mid, attack, release);
        current.High = Smooth(current.High, target.High, attack, release);
        current.Overall = Smooth(current.Overall, target.Overall, attack, release);
    }

    private static float Smooth(float current, float target, float attack, float release)
    {
        return current + (target - current) * (target > current ? attack : release);
    }
}
