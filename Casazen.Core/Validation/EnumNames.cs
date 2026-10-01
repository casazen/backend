namespace Casazen.Core.Validation;

/// <summary>
/// Strict enum parsing for values received at an API boundary (PL-07, A1-35, A7-31): <c>Enum.TryParse</c> alone
/// also accepts any integer-looking string, even one no member declares (e.g. <c>"99"</c> on a 3-value enum), and
/// silently maps it to that undefined numeric value. Nothing downstream was written to handle it — an exhaustive
/// <c>switch</c> throws, a lookup finds nothing — so the request fails with a generic 500 instead of a clean 400.
/// </summary>
/// <remarks>
/// Single source of truth for this check: call <see cref="TryParseDefined{TEnum}"/> instead of
/// <c>Enum.TryParse</c> wherever a request body, route, query or form value is parsed into an enum. Only the
/// (case-insensitive) name of a single declared member is accepted — never its numeric encoding, and never a
/// comma-separated combination, which <c>Enum.TryParse</c> would otherwise happily bitwise-OR into some other
/// (possibly also declared, but unintended) value even on a non-<c>[Flags]</c> enum.
/// </remarks>
public static class EnumNames
{
    /// <summary>
    /// True and <paramref name="result"/> set only when <paramref name="value"/> is, once trimmed, the
    /// case-insensitive name of a single member <typeparamref name="TEnum"/> declares. Null, blank, a numeric
    /// string (defined ordinal or not) and a comma-separated list all return false.
    /// </summary>
    public static bool TryParseDefined<TEnum>(string? value, out TEnum result) where TEnum : struct, Enum
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (trimmed.Contains(',') || IsNumeric(trimmed))
            return false;

        return Enum.TryParse(trimmed, ignoreCase: true, out result) && Enum.IsDefined(result);
    }

    private static bool IsNumeric(string value)
    {
        var start = value.Length > 0 && (value[0] == '-' || value[0] == '+') ? 1 : 0;
        return value.Length > start && value.AsSpan(start).ToArray().All(char.IsAsciiDigit);
    }
}
