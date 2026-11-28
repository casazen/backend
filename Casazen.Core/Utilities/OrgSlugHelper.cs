using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Utilities;

public static partial class OrgSlugHelper
{
    public const int MinLength = 3;

    /// <summary>Longest slug a host may choose: a DNS label, since the slug is also the subdomain fallback.</summary>
    public const int MaxLength = 63;

    public const string InvalidCode = "org_slug_invalid";

    public const string ReservedCode = "org_slug_reserved";

    public const string TakenCode = "org_slug_taken";

    private const string NeutralAlphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    /// <summary>
    /// Reserved at the org level: platform routes (<c>/book</c>, <c>/api</c>, ...) and terms that would be
    /// confusing or exploitable as an org's own public identity, including the default reserved subdomains
    /// (<c>PublicHostOptions.ReservedSubdomains</c>) since the slug also serves as the subdomain fallback.
    /// </summary>
    private static readonly HashSet<string> ReservedSlugs = new(StringComparer.OrdinalIgnoreCase)
    {
        "book", "app", "api", "admin", "checkout", "my-bookings", "properties", "property",
        "settings", "login", "register", "onboarding", "widget", "public", "search",
        "help", "checkin", "supplier", "www", "staging", "test", "mail", "casazen",
    };

    public static string Sanitize(string value)
    {
        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        }

        return SlugSanitizer().Replace(builder.ToString(), "-").Trim('-');
    }

    /// <summary>
    /// Sanitizes a host-chosen slug and validates it: <see cref="MinLength"/> to <see cref="MaxLength"/> characters,
    /// no reserved word. Throws <see cref="DomainRuleException"/> (422 <see cref="InvalidCode"/> or
    /// <see cref="ReservedCode"/>) rather than returning an unusable value; uniqueness is a database concern.
    /// </summary>
    public static string NormalizeRequired(string? rawSlug)
    {
        var sanitized = Sanitize(rawSlug ?? string.Empty);
        if (sanitized.Length is < MinLength or > MaxLength)
            throw new DomainRuleException(InvalidCode, "OrgSlugInvalid", MinLength, MaxLength);

        if (ReservedSlugs.Contains(sanitized))
            throw new DomainRuleException(ReservedCode, "OrgSlugReserved");

        return sanitized;
    }

    public static string GenerateNeutral()
    {
        Span<char> suffix = stackalloc char[8];
        for (var i = 0; i < suffix.Length; i++)
            suffix[i] = NeutralAlphabet[RandomNumberGenerator.GetInt32(NeutralAlphabet.Length)];
        return $"org-{suffix}";
    }

    public static bool IsNeutral(string? slug) => !string.IsNullOrEmpty(slug) && NeutralSlug().IsMatch(slug);

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex SlugSanitizer();

    [GeneratedRegex("^org-[abcdefghijkmnpqrstuvwxyz23456789]{8}$")]
    private static partial Regex NeutralSlug();
}
