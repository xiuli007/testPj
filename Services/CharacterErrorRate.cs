using System.Globalization;
using System.Text;

namespace JapaneseTranscriptionPrototype.Services;

public static class CharacterErrorRate
{
    public static double Calculate(string reference, string hypothesis)
    {
        var referenceElements = GetTextElements(reference);
        var hypothesisElements = GetTextElements(hypothesis);

        if (referenceElements.Length == 0)
        {
            return hypothesisElements.Length == 0 ? 0 : 1;
        }

        var previousRow = Enumerable.Range(0, hypothesisElements.Length + 1).ToArray();
        var currentRow = new int[hypothesisElements.Length + 1];

        for (var referenceIndex = 1; referenceIndex <= referenceElements.Length; referenceIndex++)
        {
            currentRow[0] = referenceIndex;
            for (var hypothesisIndex = 1; hypothesisIndex <= hypothesisElements.Length; hypothesisIndex++)
            {
                var substitutionCost = referenceElements[referenceIndex - 1] == hypothesisElements[hypothesisIndex - 1]
                    ? 0
                    : 1;
                currentRow[hypothesisIndex] = Math.Min(
                    Math.Min(currentRow[hypothesisIndex - 1] + 1, previousRow[hypothesisIndex] + 1),
                    previousRow[hypothesisIndex - 1] + substitutionCost);
            }

            (previousRow, currentRow) = (currentRow, previousRow);
        }

        return (double)previousRow[hypothesisElements.Length] / referenceElements.Length;
    }

    private static string[] GetTextElements(string text)
    {
        var elements = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(text.Normalize(NormalizationForm.FormC));
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            if (!string.IsNullOrWhiteSpace(element))
            {
                elements.Add(element);
            }
        }

        return [.. elements];
    }
}
