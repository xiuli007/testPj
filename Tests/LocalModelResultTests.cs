using System.Text.Json;
using JapaneseTranscriptionPrototype.Services;
using Xunit;

namespace JapaneseTranscriptionPrototype.Tests;

public sealed class LocalModelResultTests
{
    [Theory]
    [InlineData("no_speech", LocalModelOutcome.NoSpeech)]
    [InlineData("unrecognized", LocalModelOutcome.Unrecognized)]
    [InlineData("too_short", LocalModelOutcome.TooShort)]
    [InlineData("non_japanese", LocalModelOutcome.NonJapanese)]
    public void NormalSkippedResponsesDoNotBecomeTranslationOrExceptions(string outcome, LocalModelOutcome expected)
    {
        using var document = MakeResponse(outcome);
        var result = LocalModelResult.FromResponse(document.RootElement);

        Assert.Equal(expected, result.Outcome);
        Assert.False(result.HasTranslation);
        Assert.Empty(result.Japanese);
        Assert.Empty(result.Chinese);
        Assert.Contains("本段", result.StatusMessage);
    }

    [Fact]
    public void RealWorkerErrorStillThrows()
    {
        using var document = JsonDocument.Parse("""{"error":"ValueError: decoder failed"}""");
        Assert.Throws<InvalidOperationException>(() => LocalModelResult.FromResponse(document.RootElement));
    }

    [Fact]
    public void TranslatedStatusWithEmptyTextIsRejected()
    {
        using var document = MakeResponse("translated");
        Assert.Throws<InvalidDataException>(() => LocalModelResult.FromResponse(document.RootElement));
    }

    [Fact]
    public void UnknownStatusIsNotTreatedAsSilence()
    {
        using var document = MakeResponse("unexpected");
        Assert.Throws<InvalidDataException>(() => LocalModelResult.FromResponse(document.RootElement));
    }

    private static JsonDocument MakeResponse(string outcome) => JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        outcome,
        japanese = "",
        chinese = "",
        asr_seconds = 0.1,
        mt_seconds = 0.0,
        working_set_mb = 1,
        diagnostics = new
        {
            sample_rate = 16000,
            duration_seconds = 2,
            rms = 0,
            peak = 0,
            active_ratio = 0,
            language = "unreported"
        }
    }));
}
