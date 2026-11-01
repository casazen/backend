using System.Globalization;
using System.Text.Json;

namespace Casazen.Core.Pricing;

/// <summary>
/// Reads the answer of the AI to the nightly price prompt (WIZ-AI-PRICE): one JSON object, maybe wrapped in a code fence or in a
/// sentence, with numbers that may come as text («140», «140 €»). Anything that is not that is <c>null</c>: the caller does not guess.
/// </summary>
public static class PriceSuggestionAnswerParser
{
    /// <summary>
    /// The numbers and the explanation of the answer, or <c>null</c> when there is no JSON object, it is malformed, or it has no
    /// <c>nightlyRate</c>. Names are read without regard to case; unknown ones are ignored.
    /// </summary>
    public static RawPriceAnswer? Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        try
        {
            using var document = JsonDocument.Parse(
                content[start..(end + 1)],
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
