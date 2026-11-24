// SPDX-License-Identifier: AGPL-3.0-or-later
// Adapted from Goob Station's VoicePlaybackStream at 8d81b444c5f6cab92e4756578bcf68457ec13de0.

using Content.Goobstation.Shared.VoiceChat;
using Robust.Client.Audio;
using Robust.Shared.Audio.Sources;

namespace Content.Goobstation.Client.VoiceChat;

internal sealed class ProximityVoiceStream(IAudioManager audio) : IDisposable
{
    private const int FrameSamples = VoiceCodec.FrameSamples;
    private const int SampleRate = VoiceCodec.SampleRate;
    private const int RampSamples = SampleRate * 5 / 1000;
    private const int HoldSamples = SampleRate * 45 / 1000;
    private const int OverlapSamples = HoldSamples + RampSamples;
    private const int StartSamples = FrameSamples * 4;
    private const int MaxLatencySamples = FrameSamples * 12;
    private const int TargetSamples = FrameSamples * 6;
    private const int MaxForwardGap = 12;
    private const int MaxPlayingChunks = 5;
    private static readonly TimeSpan JitterWait = TimeSpan.FromMilliseconds(60);

    private readonly Dictionary<ushort, byte[]> _pending = new();
    private readonly List<Chunk> _playing = new();
    private readonly short[] _pcm = new short[SampleRate / 2];
    private readonly short[] _mix = new short[SampleRate / 2];
    private int _length;
    private long _start;
    private long _scheduledUntil;
    private ushort? _next;
    private Chunk? _current;

    private long End => _start + _length;
    public TimeSpan LastReceived { get; private set; }
    public TimeSpan NextOcclusionCheck;
    public float Occlusion;

    public void AddFrame(ushort sequence, byte[] frame, TimeSpan now)
    {
        if (_next is not { } next || now - LastReceived > TimeSpan.FromMilliseconds(500))
        {
            _next = sequence;
            _pending.Clear();
        }
        else
        {
            var ahead = (short) (sequence - next);
            if (ahead < 0)
                return;
            if (ahead > MaxForwardGap)
            {
                _pending.Clear();
                _next = sequence;
            }
        }

        LastReceived = now;
        _pending.TryAdd(sequence, frame);
        DrainPending(false);
    }

    private void DrainPending(bool flushGap)
    {
        while (_pending.Count > 0 && _next is { } next)
        {
            if (_pending.Remove(next, out var frame))
                Append(frame);
            else if (flushGap || _pending.Count >= 3)
                Append(null);
            else
                break;
            _next = unchecked((ushort) (next + 1));
        }
    }

    private void Append(byte[]? frame)
    {
        if (_length + FrameSamples > _pcm.Length)
        {
            // Keep the old chunk's overlap for crossfading. A stalled client only needs recent speech.
            var keepFrom = Math.Max(_scheduledUntil - OverlapSamples, End - MaxLatencySamples);
            var discard = (int) Math.Clamp(keepFrom - _start, 0L, _length);
            _length -= discard;
            Array.Copy(_pcm, discard, _pcm, 0, _length);
            _start += discard;
        }

        var samples = _pcm.AsSpan(_length, FrameSamples);
        if (frame == null)
            samples.Clear();
        else
            VoiceCodec.Decode(frame, samples);
        _length += FrameSamples;
    }

    public void Update(TimeSpan now, Vector2 position, float referenceDistance, float maxDistance)
    {
        var quiet = now - LastReceived >= JitterWait;
        if (quiet)
            DrainPending(true);

        for (var i = _playing.Count - 1; i >= 0; i--)
        {
            var chunk = _playing[i];
            if (!chunk.Source.Playing)
            {
                if (_current == chunk)
                    _current = null;
                chunk.Dispose();
                _playing.RemoveAt(i);
                continue;
            }
            chunk.Source.Position = position;
            chunk.Source.Occlusion = Occlusion;
        }

        var start = Math.Max(_start, _scheduledUntil);
        var offset = 0L;
        if (_current is { } current)
        {
            // QuietToolbox exposes static audio sources to content. Start a second source inside
            // the first one's tail and crossfade them so timing survives render-frame jitter.
            if (current.End - current.Start < OverlapSamples + RampSamples || End - current.End < FrameSamples)
                return;
            start = current.End - OverlapSamples;
            var cursor = current.Start + (long) MathF.Round(current.Source.PlaybackPosition * SampleRate);
            offset = cursor - start;
            if (offset < 0 || offset >= HoldSamples)
                return;
            if (End - cursor > MaxLatencySamples)
                start += End - cursor - TargetSamples;
        }
        else
        {
            if (End <= start || End - start < StartSamples && !quiet)
                return;
            if (End - start > MaxLatencySamples)
                start = End - TargetSamples;
        }

        if (start < _start || start >= End)
            return;

        if (_playing.Count >= MaxPlayingChunks)
        {
            _playing[0].Dispose();
            _playing.RemoveAt(0);
        }

        var length = (int) (End - start);
        var sourceOffset = (int) (start - _start);
        var hold = _current != null ? HoldSamples : 0;
        for (var i = 0; i < length; i++)
        {
            var head = Math.Clamp((i - hold + 0.5f) / RampSamples, 0f, 1f);
            var tail = Math.Clamp((length - i - 0.5f) / RampSamples, 0f, 1f);
            _mix[i] = (short) (_pcm[sourceOffset + i] * head * tail);
        }

        var stream = audio.LoadAudioRaw(_mix.AsSpan(0, length), 1, SampleRate);
        var source = audio.CreateAudioSource(stream);
        if (source == null)
        {
            stream.Dispose();
            _scheduledUntil = End;
            return;
        }

        source.Global = false;
        source.Position = position;
        source.Occlusion = Occlusion;
        source.Gain = 1f;
        source.RolloffFactor = 1f;
        source.ReferenceDistance = referenceDistance;
        source.MaxDistance = maxDistance;
        if (offset > 0)
            source.PlaybackPosition = offset / (float) SampleRate;
        source.StartPlaying();
        _current = new Chunk(stream, source, start, End);
        _playing.Add(_current);
        _scheduledUntil = End;
    }

    public void Dispose()
    {
        foreach (var chunk in _playing)
            chunk.Dispose();
        _playing.Clear();
        _current = null;
    }

    private sealed record Chunk(AudioStream Stream, IAudioSource Source, long Start, long End) : IDisposable
    {
        public void Dispose()
        {
            Source.Dispose();
            Stream.Dispose();
        }
    }
}
