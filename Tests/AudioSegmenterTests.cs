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
    public void ContinuousSignalIsBoundedAndReportsEachForcedBoundary()
    {
        var segmenter = new AudioSegmenter();
        var segments = new List<float[]>();
        var limits = 0;
        segmenter.SegmentReady += segments.Add;
        segmenter.SegmentLimitReached += () => limits++;
        AddSignal(segmenter, 65);
        segmenter.Flush();
        Assert.Equal(new[] { 320000, 320000, 320000, 80000 }, segments.Select(segment => segment.Length));
        Assert.Equal(3, limits);
    }

    [Fact]
    public void LongUtteranceWaitsPastSixSecondsUntilPause()
    {
        var segmenter = new AudioSegmenter();
        var segments = new List<float[]>();
        segmenter.SegmentReady += segments.Add;
        AddSignal(segmenter, 9);
        Assert.Empty(segments);
        for (var index = 0; index < AudioSegmenter.PauseSamples - 1; index++) segmenter.Add(0);
        Assert.Empty(segments);
        segmenter.Add(0);

        Assert.Single(segments);
        Assert.Equal(156800, segments[0].Length);
    }

    [Fact]
    public void ShortHesitationStaysInSameSegment()
    {
        var segmenter = new AudioSegmenter();
        var segments = new List<float[]>();
        segmenter.SegmentReady += segments.Add;
        AddSignal(segmenter, 3);
        AddSilence(segmenter, 0.4);
        AddSignal(segmenter, 5);
        Assert.Empty(segments);
        AddSilence(segmenter, 0.8);

        Assert.Single(segments);
        Assert.Equal(147200, segments[0].Length);
    }

    [Fact]
    public void LowEnergyPeaksDoNotPreventPauseDetection()
    {
        var segmenter = new AudioSegmenter();
        var segments = new List<float[]>();
        segmenter.SegmentReady += segments.Add;
        AddSignal(segmenter, 1);
        for (var index = 0; index < AudioSegmenter.PauseSamples; index++)
            segmenter.Add(index % AudioSegmenter.FrameSamples == 0 ? 0.01f : 0);

        Assert.Single(segments);
        Assert.Equal(28800, segments[0].Length);
    }

    [Fact]
    public void LongQuietInputKeepsOnlyShortPreRollBeforeSpeech()
    {
        var segmenter = new AudioSegmenter();
        var segments = new List<float[]>();
        segmenter.SegmentReady += segments.Add;
        AddSilence(segmenter, 120);
        Assert.Empty(segments);
        AddSignal(segmenter, 1);
        AddSilence(segmenter, 10);
        segmenter.Flush();

        Assert.Single(segments);
        Assert.Equal(32000, segments[0].Length);
        Assert.All(segments[0].Take(3200), sample => Assert.Equal(0f, sample));
        Assert.True(AudioSegmenter.HasSpeechEnergy(segments[0].AsSpan(3200, 16000)));
    }

    [Fact]
    public void SeparateUtterancesAreEmittedIndependently()
    {
        var segmenter = new AudioSegmenter();
        var segments = new List<float[]>();
        segmenter.SegmentReady += segments.Add;
        AddSignal(segmenter, 1);
        AddSilence(segmenter, 0.8);
        AddSignal(segmenter, 2);
        AddSilence(segmenter, 0.8);
        segmenter.Flush();

        Assert.Equal(new[] { 28800, 44800 }, segments.Select(segment => segment.Length));
    }

    [Fact]
    public void ExplicitIdleFlushEmitsTailOnceAndResetsPartialFrame()
    {
        var segmenter = new AudioSegmenter();
        var segments = new List<float[]>();
        segmenter.SegmentReady += segments.Add;
        AddSignal(segmenter, 1);
        segmenter.Add(0.05f);
        segmenter.Flush();
        segmenter.Flush();
        AddSignal(segmenter, 1);
        AddSilence(segmenter, 0.8);

        Assert.Equal(new[] { 16001, 28800 }, segments.Select(segment => segment.Length));
    }

    [Fact]
    public void DiscardingTooShortTailDoesNotLeakStateIntoNextUtterance()
    {
        var segmenter = new AudioSegmenter();
        var segments = new List<float[]>();
        segmenter.SegmentReady += segments.Add;
        AddSignal(segmenter, 0.1);
        segmenter.Flush();
        AddSignal(segmenter, 1);
        AddSilence(segmenter, 0.8);

        Assert.Single(segments);
        Assert.Equal(28800, segments[0].Length);
    }

    private static void AddSignal(AudioSegmenter segmenter, double seconds)
    {
        for (var index = 0; index < (int)(AudioSegmenter.SampleRate * seconds); index++)
            segmenter.Add((float)(0.05 * Math.Sin(2 * Math.PI * 220 * index / AudioSegmenter.SampleRate)));
    }

    private static void AddSilence(AudioSegmenter segmenter, double seconds)
    {
        for (var index = 0; index < (int)(AudioSegmenter.SampleRate * seconds); index++) segmenter.Add(0);
    }
}
