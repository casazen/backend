using System.Text.RegularExpressions;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Removes known PII patterns from text before it is sent to an external AI provider.
/// <para>
/// FD-21 (PO 2026-10-08): DeepSeek must only be called after preventive anonymisation of personal data.
/// Any prompt that may contain personal data (name, email, phone, fiscal code, address, booking reference
/// with guest data) must be scrubbed before the call.
/// </para>
/// <para>
/// Patterns replaced:
/// <list type="bullet">
///   <item>Email addresses → <c>[email]</c></item>
///   <item>Italian fiscal codes (codice fiscale, 16 chars) → <c>[cf]</c></item>
///   <item>Phone numbers (Italian mobile, landlines, international) → <c>[tel]</c></item>
/// </list>
/// The scrubber errs on the side of replacement: a false positive (replacing a non-PII sequence) is always
/// preferable to a false negative (leaking PII to an external provider).
/// </para>
/// </summary>
public static partial class PiiScrubber
{
    /// <summary>
    /// Returns a copy of <paramref name="text"/> with email addresses, Italian fiscal codes, and phone numbers
    /// replaced by their respective placeholders (<c>[email]</c>, <c>[cf]</c>, <c>[tel]</c>).
    /// Returns <see cref="string.Empty"/> when <paramref name="text"/> is null or empty.
    /// </summary>
    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        // Fiscal codes first: a CF-like sequence could be partially consumed by other patterns.
        text = FiscalCodeRegex().Replace(text, "[cf]");
        text = EmailRegex().Replace(text, "[email]");
        text = PhoneRegex().Replace(text, "[tel]");
        return text;
    }

    /// <summary>
    /// Italian codice fiscale: 6 letters, 2 digits, 1 letter, 2 digits, 1 letter, 3 digits, 1 letter (16 chars).
    /// Word boundary anchors prevent partial matches inside longer alphanumeric tokens.
    /// </summary>
    [GeneratedRegex(@"\b[A-Za-z]{6}\d{2}[A-Za-z]\d{2}[A-Za-z]\d{3}[A-Za-z]\b")]
    private static partial Regex FiscalCodeRegex();

    /// <summary>
    /// RFC-5321-ish email address. Matches user@domain.tld patterns including sub-domains, dots, plus-tags, etc.
    /// </summary>
    [GeneratedRegex(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRegex();

    /// <summary>
    /// Italian and international phone numbers.
    /// <para>Matches:</para>
    /// <list type="bullet">
    ///   <item>Italian mobiles: 3XX then 7 digits, split or not (e.g. 338 123 4567, 3381234567)</item>
    ///   <item>Italian landlines: 0X–0XXXX then 5-8 local digits (e.g. 06 1234 5678, 0121 12345)</item>
    ///   <item>International prefix: +CC or 00CC before the above (e.g. +39 06 12345678)</item>
    /// </list>
    /// <para>
    /// Local digits may be a single block (5–8) or two groups (3–4 + sep + 3–6), so 6-digit ISTAT codes
    /// (e.g. 013075) and short numeric tokens (prices, years) are not matched.
    /// </para>
    /// </summary>
    [GeneratedRegex(@"(?<!\d)(?:(?:\+|00)\d{1,3}[\s\-]?)?(?:3\d{2}|0\d{1,4})[\s\-\.\(\)]?(?:\d{3,4}[\s\-\.]?\d{3,6}|\d{5,8})(?!\d)")]
    private static partial Regex PhoneRegex();
}
