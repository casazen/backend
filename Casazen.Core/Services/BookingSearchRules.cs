namespace Casazen.Core.Services;

/// <summary>
/// The rule of the booking search (SR-03) that is not a query: what part of the text typed can be a booking code.
/// </summary>
public static class BookingSearchRules
{
    /// <summary>Characters from which a text is looked for among the booking codes (a shorter one would match almost every code).</summary>
    public const int MinCodeFragmentLength = 3;

    /// <summary>
    /// The text, as a piece of a stored booking code: spaces and dashes taken out (a code is shown as <c>XXXXX-XXXXX</c>), upper
    /// case, and the characters a person may read for others as <see cref="BookingCodes.TryNormalize"/> reads them (O as 0, I
    /// and L as 1). Null when it cannot be one: shorter than <see cref="MinCodeFragmentLength"/>, longer than a code, or with a
    /// character no code has.
    /// </summary>
    public static string? CodeFragment(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        Span<char> chars = stackalloc char[BookingCodes.Length];
        var count = 0;
        foreach (var raw in text)
        {
            if (char.IsWhiteSpace(raw) || raw is '-' or '‐' or '‑' or '‒' or '–' or '—' or '−')
                continue;

            var c = char.ToUpperInvariant(raw) switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                var other => other,
            };
            if (count == BookingCodes.Length || BookingCodes.Alphabet.IndexOf(c) < 0)
                return null;
            chars[count++] = c;
        }

        return count < MinCodeFragmentLength ? null : new string(chars[..count]);
    }
}
