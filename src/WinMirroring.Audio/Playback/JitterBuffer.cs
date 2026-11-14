using NAudio.Wave;

namespace WinMirroring.Audio.Playback;

/// <summary>
/// Playout buffer for one incoming stream, read by the mixer on the render thread.
///
/// Two things it did badly, and both were audible. It aimed at a fixed amount of sound — forty milliseconds by
/// default — which on Wi-Fi carrying a screen stream at the same time is less than one burst of queueing, so it ran
/// dry again and again; it now aims at the configured amount plus what the measured arrival jitter asks for (see
/// <see cref="Aim"/>), and comes back down slowly when the link is calm. And when it had to cut or to run out, it
/// simply jumped: up to sixty milliseconds of samples discarded in the middle of a waveform, or an abrupt switch to
/// silence. Both are a click, and clock drift between two devices makes the cutting regular, so the clicks came on
/// a schedule. Every join is now a short ramp from the last value that was actually played.
/// </summary>
internal sealed class JitterBuffer : ISampleProvider
{
    private const int CapacityMs = 1000;
    private const int TrimAboveExtraMs = 60;

    /// <summary>Never aim higher than this: past a fifth of a second the delay is worse than the dropouts.</summary>
    private const int MaxTargetMs = 200;

    /// <summary>Trim towards the target in steps this big, so each join is a small one.</summary>
    private const int TrimStepMs = 20;

    /// <summary>Length of the ramp that joins two pieces of sound which do not meet.</summary>
    private const int FadeMs = 3;

    private readonly object _gate = new();
    private readonly float[] _ring;
    private readonly float[] _last = new float[AudioFormat.Channels];
    private readonly int _frameMs;
    private int _read;
    private int _count;
    private bool _playing;
    private int _baseMs;
    private int _targetValues;

    /// <summary>What comes next does not follow what was played, so it has to be eased in.</summary>
    private bool _join = true;

    public JitterBuffer(int targetMs, int frameMs)
    {
        _ring = new float[AudioFormat.MillisecondsToValues(CapacityMs)];
        _frameMs = Math.Clamp(frameMs, 5, 60);
        TargetMs = targetMs;
    }

    public WaveFormat WaveFormat => AudioFormat.Wave;

    /// <summary>The configured amount of sound to hold; the buffer may aim higher on a jittery link.</summary>
    public int TargetMs
    {
        get => _baseMs;
        set
        {
            _baseMs = Math.Clamp(value, 10, MaxTargetMs);
            _targetValues = AudioFormat.MillisecondsToValues(_baseMs);
        }
    }

    /// <summary>What the buffer is actually aiming to hold right now.</summary>
    public int AimedMs => _targetValues / Math.Max(1, AudioFormat.MillisecondsToValues(1));

    public long Underruns { get; private set; }

    public long Trims { get; private set; }

    public double BufferedMs
    {
        get
        {
            lock (_gate)
            {
                return _count / (double)AudioFormat.MillisecondsToValues(1);
            }
        }
    }

    /// <summary>
    /// Takes the measured spread in arrival times and aims the buffer accordingly: the configured amount, plus one
    /// frame to have something in hand, plus three times the jitter — the usual rule, and the reason a link that
    /// goes lumpy for a while stops costing dropouts instead of just a little delay.
    /// </summary>
    public void Aim(double jitterMs)
    {
        var wanted = Math.Clamp(_baseMs + _frameMs + (3 * jitterMs), _baseMs + _frameMs, MaxTargetMs);
        var values = AudioFormat.MillisecondsToValues((int)Math.Round(wanted));
        lock (_gate)
        {
            // Up at once, down a tenth of the difference at a time: a single late packet must not undo the room
            // the buffer has just learned it needs.
            _targetValues = values > _targetValues ? values : _targetValues - ((_targetValues - values) / 10);
        }
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            if (samples.Length > _ring.Length)
            {
                samples = samples[^_ring.Length..];
            }

            var overflow = _count + samples.Length - _ring.Length;
            if (overflow > 0)
            {
                Drop(overflow);
            }

            var write = (_read + _count) % _ring.Length;
            var first = Math.Min(samples.Length, _ring.Length - write);
            samples[..first].CopyTo(_ring.AsSpan(write));
            samples[first..].CopyTo(_ring);
            _count += samples.Length;

            var trimAbove = _targetValues + AudioFormat.MillisecondsToValues(TrimAboveExtraMs);
            if (_playing && _count > trimAbove)
            {
                Drop(AlignToFrames(Math.Min(_count - _targetValues, AudioFormat.MillisecondsToValues(TrimStepMs))));
                Trims++;
            }
        }
    }

    public int Read(Span<float> buffer)
    {
        lock (_gate)
        {
            if (!_playing)
            {
                if (_count >= _targetValues)
                {
                    _playing = true;
                }
                else
                {
                    buffer.Clear();
                    return buffer.Length;
                }
            }

            var take = Math.Min(buffer.Length, _count);
            var first = Math.Min(take, _ring.Length - _read);
            _ring.AsSpan(_read, first).CopyTo(buffer);
            _ring.AsSpan(0, take - first).CopyTo(buffer[first..]);
            _read = (_read + take) % _ring.Length;
            _count -= take;

            if (_join && take > 0)
            {
                _join = false;
                EaseIn(buffer[..take]);
            }

            if (take < buffer.Length)
            {
                // Out of sound: ease down to silence rather than stopping dead, and ease back in on the refill.
                FadeOut(buffer[take..]);
                _playing = false;
                _join = true;
                Underruns++;
            }
            else
            {
                Remember(buffer[..take]);
            }

            return buffer.Length;
        }
    }

    /// <summary>Ramps the start of a block from the last value played to what the block actually holds.</summary>
    private void EaseIn(Span<float> block)
    {
        var steps = Math.Min(FadeValues(), block.Length / AudioFormat.Channels);
        for (var i = 0; i < steps; i++)
        {
            var mix = (i + 1) / (float)(steps + 1);
            for (var channel = 0; channel < AudioFormat.Channels; channel++)
            {
                ref var sample = ref block[(i * AudioFormat.Channels) + channel];
                sample = (_last[channel] * (1 - mix)) + (sample * mix);
            }
        }
    }

    private void FadeOut(Span<float> block)
    {
        var steps = Math.Min(FadeValues(), block.Length / AudioFormat.Channels);
        for (var i = 0; i < steps; i++)
        {
            var mix = 1 - ((i + 1) / (float)(steps + 1));
            for (var channel = 0; channel < AudioFormat.Channels; channel++)
            {
                block[(i * AudioFormat.Channels) + channel] = _last[channel] * mix;
            }
        }

        block[(steps * AudioFormat.Channels)..].Clear();
        Array.Clear(_last);
    }

    private void Remember(ReadOnlySpan<float> block)
    {
        if (block.Length < AudioFormat.Channels)
        {
            return;
        }

        var tail = block.Length - AudioFormat.Channels;
        for (var channel = 0; channel < AudioFormat.Channels; channel++)
        {
            _last[channel] = block[tail + channel];
        }
    }

    private static int FadeValues() => AudioFormat.SampleRate / 1000 * FadeMs;

    private void Drop(int values)
    {
        values = Math.Min(values, _count);
        if (values <= 0)
        {
            return;
        }

        _read = (_read + values) % _ring.Length;
        _count -= values;
        _join = true;
    }

    private static int AlignToFrames(int values) => values - (values % AudioFormat.Channels);
}
