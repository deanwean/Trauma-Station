// SPDX-License-Identifier: AGPL-3.0-or-later
// Adapted from Goob Station's VoicePlaybackStream at 8d81b444c5f6cab92e4756578bcf68457ec13de0.

using Content.Goobstation.Shared.VoiceChat;
using Robust.Client.Audio;
using Robust.Shared.Map;
using Robust.Shared.Audio.Sources;

namespace Content.Goobstation.Client.VoiceChat;

internal sealed class ProximityVoiceStream(IAudioManager audio, VoiceDeliveryRoute route, NetEntity emitter) : IDisposable
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
    private const float PlaybackGain = 3f;
    private const float LimiterKnee = short.MaxValue * 0.8f;
    private static readonly TimeSpan JitterWait = TimeSpan.FromMilliseconds(60);

    private readonly Dictionary<ushort, byte[]> _pending = new();
    private readonly List<Chunk> _playing = new();
    private readonly short[] _pcm = new short[SampleRate / 2];
    private readonly short[] _mix = new short[SampleRate / 2];
    private int _length;
    private long _start;
    private long _scheduledUntil;
    private ushort? _next;
    private ushort _latestSequence;
    private readonly VoiceLevelAnalyzer _levelAnalyzer = new();
    private VoiceLevels _targetLevels;
    private VoiceLevels _levels;
    public VoiceLevels Levels => _levels;
    public float Audibility { get; set; }
    public bool Playing => _playing.Count > 0;
    public float Activity => Playing ? Math.Clamp(_levels.Overall * 3f, 0f, 1f) : 0f;
    public float Volume { get; set; } = 1f;
    public string RadioChannel { get; init; } = string.Empty;
    public NetEntity Speaker { get; init; }
    private Chunk? _current;

    private long End => _start + _length;
    public TimeSpan LastReceived { get; private set; }
    public VoiceDeliveryRoute Route { get; } = route;
    public NetEntity Emitter { get; } = emitter;
    public MapCoordinates DeadPosition;
    public bool Global => Route is VoiceDeliveryRoute.Radio or VoiceDeliveryRoute.Handset or
        VoiceDeliveryRoute.Broadcast or VoiceDeliveryRoute.Monitor or VoiceDeliveryRoute.Sidetone;
    public TimeSpan NextOcclusionCheck;
    public float Occlusion;

    public bool IsNewer(ushort sequence, TimeSpan now)
        => _next == null || now - LastReceived > TimeSpan.FromMilliseconds(500) ||
           (short) (sequence - _latestSequence) > 0;

    public void AddFrame(ushort sequence, byte[] frame, TimeSpan now)
    {
        if (IsNewer(sequence, now))
            _latestSequence = sequence;

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
        _targetLevels = _levelAnalyzer.Analyze(samples);
        _length += FrameSamples;
    }

    public void UpdateIndicator(TimeSpan now, float frameTime)
    {
        var target = now - LastReceived < TimeSpan.FromMilliseconds(200) ? _targetLevels : default;
        VoiceLevelSmoothing.Apply(ref _levels, target, frameTime);
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
            chunk.Source.Gain = Math.Min(Volume, 1f);
        }

        var start = Math.Max(_start, _scheduledUntil);
        var offset = 0L;
        if (_current is { } current)
        {
            // QuietToolbox exposes static audio sources to content. Start a second source inside
            // the first one's tail and crossfade them so timing survives render-frame jitter.
            if (End <= current.End)
                return;
            start = Math.Max(_start, Math.Max(current.Start, current.End - OverlapSamples));
            var cursor = current.Start + (long) MathF.Round(current.Source.PlaybackPosition * SampleRate);
            offset = cursor - start;
            if (offset < 0 || cursor >= current.End)
                return;
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
        var hold = _current is { } previous ? Math.Max(0, previous.End - start - RampSamples) : 0;
        var boost = Math.Max(1f, Volume);
        for (var i = 0; i < length; i++)
        {
            var head = Math.Clamp((i - hold + 0.5f) / RampSamples, 0f, 1f);
            var tail = Math.Clamp((length - i - 0.5f) / RampSamples, 0f, 1f);
            _mix[i] = (short) (Amplify(_pcm[sourceOffset + i], boost) * head * tail);
        }

        var stream = audio.LoadAudioRaw(_mix.AsSpan(0, length), 1, SampleRate);
        var source = audio.CreateAudioSource(stream);
        if (source == null)
        {
            stream.Dispose();
            _scheduledUntil = End;
            return;
        }

        source.Global = Global;
        source.Position = position;
        source.Occlusion = Occlusion;
        source.Gain = Math.Min(Volume, 1f);
        source.RolloffFactor = Global ? 0f : 1f;
        source.ReferenceDistance = referenceDistance;
        source.MaxDistance = maxDistance;
        if (offset > 0)
            source.PlaybackPosition = offset / (float) SampleRate;
        source.StartPlaying();
        _current = new Chunk(stream, source, start, End);
        _playing.Add(_current);
        _scheduledUntil = End;
    }

    private static float Amplify(short sample, float boost)
    {
        var amplified = sample * PlaybackGain * boost;
        var excess = MathF.Abs(amplified) - LimiterKnee;
        if (excess <= 0)
            return amplified;

        // Compress peaks above the knee so louder voices don't hard-clip.
        var headroom = short.MaxValue - LimiterKnee;
        return MathF.CopySign(LimiterKnee + headroom * excess / (headroom + excess), amplified);
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
