using System.Text.RegularExpressions;

namespace Casazen.Core.Utilities;

public static partial class PropertySlugHelper
{
    private static readonly HashSet<string> ReservedSlugs = new(StringComparer.OrdinalIgnoreCase)
    {
        "property", "checkout", "my-bookings", "api", "book",
    };

    public static string Sanitize(string value)
    {
        var sanitized = SlugSanitizer().Replace(value.ToLowerInvariant(), "-");
        sanitized = sanitized.Trim('-');
        if (sanitized.Length > 100)
            sanitized = sanitized[..100].TrimEnd('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "property" : sanitized;
    }

    public static void Validate(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            throw new ArgumentException("Slug cannot be empty.");

        if (slug.Length > 100)
            throw new ArgumentException("Slug cannot exceed 100 characters.");

        if (!SlugPattern().IsMatch(slug))
            throw new ArgumentException("Slug must contain only lowercase letters, numbers, and hyphens.");

        if (ReservedSlugs.Contains(slug))
            throw new ArgumentException($"Slug '{slug}' is reserved.");
    }

    public static string NormalizeOptional(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return string.Empty;

        var normalized = Sanitize(slug);
        Validate(normalized);
        return normalized;
    }

    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex SlugSanitizer();
}
