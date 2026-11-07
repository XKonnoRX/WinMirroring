using NAudio.Wave;

namespace WinMirroring.Audio.Playback;

/// <summary>
/// Playout buffer for one incoming stream, read by the mixer on the render thread.
/// It starts playing only after <see cref="TargetMs"/> of audio is queued, refills to the target after an
/// underrun instead of stuttering, and trims itself back to the target when it grows too large (clock drift
/// between devices, or a burst after a network stall), so latency cannot creep up over time.
/// </summary>
internal sealed class JitterBuffer : ISampleProvider
{
    private const int CapacityMs = 1000;
    private const int TrimAboveExtraMs = 60;

    private readonly object _gate = new();
    private readonly float[] _ring;
    private int _read;
    private int _count;
    private bool _playing;
    private int _targetValues;

    public JitterBuffer(int targetMs)
    {
        _ring = new float[AudioFormat.MillisecondsToValues(CapacityMs)];
        TargetMs = targetMs;
    }

    public WaveFormat WaveFormat => AudioFormat.Wave;

    public int TargetMs
    {
        get => _targetValues / AudioFormat.MillisecondsToValues(1);
        set => _targetValues = AudioFormat.MillisecondsToValues(Math.Clamp(value, 10, 500));
    }

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
                Drop(AlignToFrames(_count - _targetValues));
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

            if (take < buffer.Length)
            {
                buffer[take..].Clear();
                _playing = false;
                Underruns++;
            }

            return buffer.Length;
        }
    }

    private void Drop(int values)
    {
        values = Math.Min(values, _count);
        _read = (_read + values) % _ring.Length;
        _count -= values;
    }

    private static int AlignToFrames(int values) => values - values % AudioFormat.Channels;
}
