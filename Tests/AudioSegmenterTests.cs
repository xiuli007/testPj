using JapaneseTranscriptionPrototype.Services;
using Xunit;

namespace JapaneseTranscriptionPrototype.Tests;

public sealed class AudioSegmenterTests
{
    [Fact]
    public void SilenceDoesNotCreateRecognitionWork()
    {
        var segmenter = new AudioSegmenter();
        var calls = 0;
        segmenter.SegmentReady += _ => calls++;
        for (var index = 0; index < AudioSegmenter.SampleRate * 30; index++) segmenter.Add(0);
        segmenter.Flush();
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ContinuousSignalIsBoundedToSixSeconds()
    {
        var segmenter = new AudioSegmenter();
        var segments = new List<float[]>();
        segmenter.SegmentReady += segments.Add;
        for (var index = 0; index < AudioSegmenter.SampleRate * 13; index++) segmenter.Add(0.1f);
        segmenter.Flush();
        Assert.Equal(new[] { 96000, 96000, 16000 }, segments.Select(segment => segment.Length));
    }

    [Fact]
    public void PauseFlushesSpeechWithoutWaitingForSixSeconds()
    {
        var segmenter = new AudioSegmenter();
        var segments = new List<float[]>();
        segmenter.SegmentReady += segments.Add;
        for (var index = 0; index < AudioSegmenter.SampleRate; index++) segmenter.Add(0.05f);
        for (var index = 0; index < AudioSegmenter.SampleRate * 2; index++) segmenter.Add(0);
        segmenter.Flush();
        Assert.Single(segments);
        Assert.Equal(32000, segments[0].Length);
    }
}
