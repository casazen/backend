using System.Text.RegularExpressions;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Strips script/event-handler vectors from AI-generated HTML before persist and serve.
/// </summary>
public static partial class SeoHtmlSanitizer
{
    public static string Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var sanitized = ScriptTagRegex().Replace(html, string.Empty);
        sanitized = EventHandlerRegex().Replace(sanitized, string.Empty);
        sanitized = JavascriptUrlRegex().Replace(sanitized, string.Empty);
        return sanitized;
    }

    [GeneratedRegex(@"<script\b[^<]*(?:(?!<\/script>)<[^<]*)*<\/script>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptTagRegex();

    [GeneratedRegex(@"\s+on\w+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlerRegex();

    [GeneratedRegex(@"javascript\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex JavascriptUrlRegex();
}
