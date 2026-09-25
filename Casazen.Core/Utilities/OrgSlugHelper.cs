using System.Text.RegularExpressions;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Utilities;

/// <summary>
/// Sanitizes and validates the org's own public slug (A1-23): the readable identifier shown at
/// <c>/book/{slug}</c> and, absent an explicit <c>Org.Subdomain</c>, used as its fallback subdomain label
/// (<c>IOrgService.GetBySubdomainOrSlugAsync</c>, <c>PublicHostResolver</c>). Mirrors
/// <see cref="PropertySlugHelper"/>'s sanitize-then-validate shape at the org level, with its own reserved
/// words and domain exceptions (422) so an unusable slug never reaches the database.
/// </summary>
public static partial class OrgSlugHelper
{
    public const int MaxLength = 100;

    /// <summary>422: the slug is empty once sanitized, or too short/long to be a usable identifier.</summary>
    public const string InvalidCode = "org_slug_invalid";

    /// <summary>422: the slug is a platform route or term that must never be an org's public identity.</summary>
    public const string ReservedCode = "org_slug_reserved";

    /// <summary>
    /// Reserved at the org level: platform routes (<c>/book</c>, <c>/api</c>, ...) and terms that would be
    /// confusing or exploitable as an org's own public identity, including the subdomain reserved words
    /// (<c>www</c>, <c>admin</c>, ...) since the slug also serves as the subdomain fallback.
    /// </summary>
    private static readonly HashSet<string> ReservedSlugs = new(StringComparer.OrdinalIgnoreCase)
    {
        "book", "app", "api", "admin", "checkout", "my-bookings", "properties", "property",
        "settings", "login", "register", "onboarding", "widget", "public", "search",
        "help", "checkin", "supplier", "www", "staging", "test", "mail", "casazen",
    };

    /// <summary>Lowercases, replaces runs of non <c>[a-z0-9]</c> with a single hyphen and trims to <see cref="MaxLength"/>.</summary>
    public static string Sanitize(string value)
    {
        var sanitized = SlugSanitizer().Replace(value.Trim().ToLowerInvariant(), "-");
        sanitized = sanitized.Trim('-');
        if (sanitized.Length > MaxLength)
            sanitized = sanitized[..MaxLength].TrimEnd('-');
        return sanitized;
    }

    /// <summary>
    /// Sanitizes a host-chosen slug and validates it: never empty, no reserved word. Throws
    /// <see cref="DomainRuleException"/> (422 <see cref="InvalidCode"/> or <see cref="ReservedCode"/>) rather
    /// than returning an unusable value — callers still check uniqueness themselves (a database concern).
    /// </summary>
    public static string NormalizeRequired(string? rawSlug)
    {
        var sanitized = Sanitize(rawSlug ?? string.Empty);
        if (sanitized.Length == 0)
            throw new DomainRuleException(InvalidCode, "OrgSlugInvalid");

        if (ReservedSlugs.Contains(sanitized))
            throw new DomainRuleException(ReservedCode, "OrgSlugReserved");

        return sanitized;
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex SlugSanitizer();
}
