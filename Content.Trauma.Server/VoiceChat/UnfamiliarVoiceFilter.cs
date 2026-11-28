// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Goobstation.Shared.VoiceChat;

namespace Content.Trauma.Server.VoiceChat;

/// <summary>
/// Replaces unfamiliar speech with a synthetic sound. Only the frame's overall energy controls
/// the replacement; the mic waveform, timing within the frame and pitch are never transmitted.
/// </summary>
internal sealed class UnfamiliarVoiceFilter
{
    private float _noise;
    private float _phase;
    private float _gain;

    public byte[] Replace(ReadOnlySpan<byte> frame)
    {
        Span<short> samples = stackalloc short[VoiceCodec.FrameSamples];
        if (VoiceCodec.Decode(frame, samples))
        {
            var sum = 0f;
            foreach (var sample in samples)
                sum += (float) sample * sample;

            var rms = MathF.Sqrt(sum / samples.Length);
            var target = rms < 330f ? 0f : MathF.Min(5800f, rms * 0.6f);
            for (var i = 0; i < samples.Length; i++)
            {
                _gain += (target - _gain) * 0.025f;
                _noise += (Random.Shared.NextSingle() * 2f - 1f - _noise) * 0.22f;
                _phase = (_phase + 0.031f) % 1f;
                var carrier = _phase < 0.5f ? 0.75f : -0.75f;
                samples[i] = (short) (_gain * (_noise + carrier));
            }
        }
        else
        {
            samples.Clear();
        }

        var replacement = new byte[VoiceCodec.FrameBytes];
        var predictor = (int) samples[0];
        var index = 0;
        VoiceCodec.Encode(samples, ref predictor, ref index, replacement);
        return replacement;
    }
}
