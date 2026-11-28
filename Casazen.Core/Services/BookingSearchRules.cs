namespace Casazen.Core.Services;

public static class BookingSearchRules
{
    public const int MinCodeFragmentLength = 3;

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
