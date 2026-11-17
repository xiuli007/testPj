namespace JapaneseTranscriptionPrototype.Services;

public sealed class AudioSegmenter
{
    public const int SampleRate = 16000;
    public const int SegmentSamples = SampleRate * 6;
    private readonly float[] _buffer = new float[SegmentSamples];
    private int _count;
    private int _silence;
    public event Action<float[]>? SegmentReady;

    public void Add(float sample)
    {
        _buffer[_count++] = sample;
        _silence = Math.Abs(sample) < 0.008 ? _silence + 1 : 0;
        if (_silence >= SampleRate && _count >= SampleRate * 2)
        {
            Flush();
            return;
        }
        if (_count != _buffer.Length) return;
        var segment = _buffer.ToArray();
        _count = 0;
        _silence = 0;
        if (HasSpeechEnergy(segment)) SegmentReady?.Invoke(segment);
    }

    public void Flush()
    {
        if (_count < SampleRate / 2) { _count = 0; return; }
        var segment = _buffer[.._count];
        _count = 0;
        _silence = 0;
        if (HasSpeechEnergy(segment)) SegmentReady?.Invoke(segment);
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
