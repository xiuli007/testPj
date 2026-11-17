namespace JapaneseTranscriptionPrototype.Services;

public sealed class AudioSegmenter
{
    public const int SampleRate = 16000;
    public const int SegmentSamples = SampleRate * 20;
    public const int FrameSamples = SampleRate / 50;
    public const int PauseSamples = SampleRate * 8 / 10;
    private const int PreRollSamples = SampleRate / 5;
    private readonly float[] _buffer = new float[SegmentSamples];
    private int _count;
    private int _frameCount;
    private int _silence;
    private bool _hasSpeech;
    public event Action<float[]>? SegmentReady;
    public event Action? SegmentLimitReached;

    public void Add(float sample)
    {
        _buffer[_count++] = sample;
        if (++_frameCount < FrameSamples) return;
        _frameCount = 0;

        // Frame energy tolerates waveform zero crossings; it is not semantic VAD.
        if (HasSpeechEnergy(_buffer.AsSpan(_count - FrameSamples, FrameSamples)))
        {
            _hasSpeech = true;
            _silence = 0;
        }
        else
        {
            _silence = Math.Min(_silence + FrameSamples, PauseSamples);
        }

        if (!_hasSpeech)
        {
            if (_count > PreRollSamples)
            {
                Array.Copy(_buffer, _count - PreRollSamples, _buffer, 0, PreRollSamples);
                _count = PreRollSamples;
            }
            return;
        }
        if (_silence >= PauseSamples)
        {
            Flush();
            return;
        }
        if (_count != _buffer.Length) return;
        SegmentLimitReached?.Invoke();
        Flush();
    }

    public void Flush()
    {
        var segment = _count >= SampleRate / 2 && HasSpeechEnergy(_buffer.AsSpan(0, _count))
            ? _buffer[.._count]
            : null;
        _count = 0;
        _frameCount = 0;
        _silence = 0;
        _hasSpeech = false;
        if (segment is not null) SegmentReady?.Invoke(segment);
    }

    // Energy gating is not semantic VAD: background music/noise can still pass.
    public static bool HasSpeechEnergy(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return false;
        double sum = 0;
        var aboveThreshold = 0;
        foreach (var sample in samples)
        {
            sum += sample * sample;
            if (Math.Abs(sample) > 0.008f) aboveThreshold++;
        }
        return Math.Sqrt(sum / samples.Length) >= 0.003 &&
               aboveThreshold >= samples.Length * 0.025;
    }
}
