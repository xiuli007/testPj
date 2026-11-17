using JapaneseTranscriptionPrototype.Services;
using Xunit;

namespace JapaneseTranscriptionPrototype.Tests;

public sealed class CharacterErrorRateTests
{
    [Theory]
    [InlineData("こんにちは", "こんにちは", 0)]
    [InlineData("猫です", "犬です", 1.0 / 3.0)]
    [InlineData("猫 です", "猫です", 0)]
    [InlineData("", "音声", 1)]
    [InlineData("か\u3099", "が", 0)]
    public void Calculate_UsesJapaneseTextElementsAndIgnoresWhitespace(
        string reference,
        string hypothesis,
        double expected)
    {
        var actual = CharacterErrorRate.Calculate(reference, hypothesis);

        Assert.Equal(expected, actual, 3);
    }

    [Fact]
    public void Calculate_ReturnsZeroWhenBothTextsAreEmpty()
    {
        Assert.Equal(0, CharacterErrorRate.Calculate("", "   "));
    }
}
