using System.Globalization;
using System.Text.Json;

namespace Casazen.Core.Pricing;

/// <summary>
/// Reads the answer of the AI to the nightly price prompt (WIZ-AI-PRICE): one JSON object, maybe wrapped in a code fence or in a
/// sentence, with numbers that may come as text («140», «140 €»). A model that reasons aloud (a <c>&lt;think&gt;</c> block, drafts of the
/// answer before the answer) is read from its last object that has a rate, after the reasoning. Anything that is not that is
/// <c>null</c>: the caller does not guess.
/// </summary>
public static class PriceSuggestionAnswerParser
{
    private const string ReasoningOpening = "<think>";
    private const string ReasoningClosing = "</think>";

    /// <summary>
    /// The numbers and the explanation of the answer, or <c>null</c> when there is no JSON object with a <c>nightlyRate</c>. The text
    /// of a reasoning (up to the last <c>&lt;/think&gt;</c>, or from an <c>&lt;think&gt;</c> that was never closed) is not an answer.
    /// Of several objects the last one that has a rate is the answer: the ones before it are drafts. Names are read without regard to
    /// case; unknown ones are ignored.
    /// </summary>
    public static RawPriceAnswer? Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var text = WithoutReasoning(content);
        for (var start = text.LastIndexOf('{'); start >= 0; start = start == 0 ? -1 : text.LastIndexOf('{', start - 1))
        {
            var end = EndOfObject(text, start);
            if (end < 0)
                continue;

            var answer = ParseObject(text[start..(end + 1)]);
            if (answer is not null)
                return answer;
        }

        return null;
    }

    /// <summary>What follows the last closing tag of a reasoning, without a reasoning that was opened and never closed.</summary>
    private static string WithoutReasoning(string content)
    {
        var closing = content.LastIndexOf(ReasoningClosing, StringComparison.OrdinalIgnoreCase);
        var text = closing >= 0 ? content[(closing + ReasoningClosing.Length)..] : content;
        var opening = text.IndexOf(ReasoningOpening, StringComparison.OrdinalIgnoreCase);
        return opening >= 0 ? text[..opening] : text;
    }

    /// <summary>The index of the brace that closes the object opened at <paramref name="start"/>, braces inside strings left out; -1 when it never closes.</summary>
    private static int EndOfObject(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped)
                    escaped = false;
                else if (c == '\\')
                    escaped = true;
                else if (c == '"')
                    inString = false;
            }
            else if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static RawPriceAnswer? ParseObject(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            decimal? rate = null;
            decimal? rangeMin = null;
            decimal? rangeMax = null;
            decimal? cleaning = null;
            decimal? weekend = null;
            decimal? minNights = null;
            string? rationale = null;

            foreach (var member in document.RootElement.EnumerateObject())
            {
                if (Is(member, "nightlyRate"))
                    rate = ReadNumber(member.Value);
                else if (Is(member, "rangeMin"))
                    rangeMin = ReadNumber(member.Value);
                else if (Is(member, "rangeMax"))
                    rangeMax = ReadNumber(member.Value);
                else if (Is(member, "cleaningFee"))
                    cleaning = ReadNumber(member.Value);
                else if (Is(member, "weekendSurchargePercent"))
                    weekend = ReadNumber(member.Value);
                else if (Is(member, "minNights"))
                    minNights = ReadNumber(member.Value);
                else if (Is(member, "rationale") && member.Value.ValueKind == JsonValueKind.String)
                    rationale = member.Value.GetString();
            }

            return rate is null ? null : new RawPriceAnswer(rate, rangeMin, rangeMax, cleaning, weekend, minNights, rationale);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Is(JsonProperty member, string name) =>
        string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase);

    private static decimal? ReadNumber(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.TryGetDecimal(out var number) ? number : null;

            case JsonValueKind.String:
                var text = value.GetString()?
                    .Replace("€", string.Empty, StringComparison.Ordinal)
                    .Replace("%", string.Empty, StringComparison.Ordinal)
                    .Trim()
                    .Replace(',', '.');
                return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

            default:
                return null;
        }
    }
}
