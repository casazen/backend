using System.Net;
using System.Text.RegularExpressions;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Checks the answer of the AI provider before it is stored as an SEO revision (SE-01, A8-06). Only an answer of a
/// configured provider that is an HTML fragment of the FD-15 allowlist, with the sections of <see cref="SeoContentPrompt"/>
/// and at least <see cref="MinimumTextLength"/> characters of text, is <see cref="SeoContentStatus.Generated"/>; anything
/// else is stored as an explicit "content not generated" state that can never be approved.
/// </summary>
public static partial class SeoGeneratedContent
{
    /// <summary>
    /// Minimum characters of visible text of a publishable page. The prompt asks for 300-600 words (about 2,000-4,000
    /// characters): this floor only rejects one-line or truncated answers ("thin content").
    /// </summary>
    public const int MinimumTextLength = 600;

    /// <summary>Status and body to store: the body is empty for an answer that is not text at all.</summary>
    public static (SeoContentStatus Status, string BodyHtml) Evaluate(AiGenerationResult result)
    {
        if (!result.ProviderConfigured)
            return (SeoContentStatus.AiProviderNotConfigured, string.Empty);

        var raw = StripOuterCodeFence(result.Content ?? string.Empty);
        var body = SeoHtmlSanitizer.Sanitize(raw);
        var text = VisibleText(body);
        if (text.Length == 0)
            return (SeoContentStatus.EmptyOutput, string.Empty);

        // Kept (sanitized) so the admin can see what the provider answered; it can never be approved.
        if (LooksLikeMarkdown(raw) || !HasRequiredStructure(body) || text.Length < MinimumTextLength)
            return (SeoContentStatus.InvalidOutput, body);

        return (SeoContentStatus.Generated, body);
    }

    /// <summary>Text of a sanitized HTML fragment, entities decoded and whitespace collapsed.</summary>
    public static string VisibleText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var text = WebUtility.HtmlDecode(TagPattern().Replace(html, " "));
        return WhitespacePattern().Replace(text, " ").Trim();
    }

    /// <summary>An answer wrapped in a single <c>```html … ```</c> block is unwrapped; any other fence stays (and fails).</summary>
    private static string StripOuterCodeFence(string content)
    {
        var match = OuterFencePattern().Match(content.Trim());
        return match.Success ? match.Groups["body"].Value : content;
    }

    private static bool LooksLikeMarkdown(string raw) =>
        raw.Contains("```", StringComparison.Ordinal) || MarkdownHeadingPattern().IsMatch(raw) || MarkdownBoldPattern().IsMatch(raw);

    private static bool HasRequiredStructure(string body) =>
        body.Contains("<h2>", StringComparison.OrdinalIgnoreCase) && body.Contains("<p>", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"^```[a-zA-Z]*\s*\n(?<body>.*?)\n?```$", RegexOptions.Singleline)]
    private static partial Regex OuterFencePattern();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s", RegexOptions.Multiline)]
    private static partial Regex MarkdownHeadingPattern();

    [GeneratedRegex(@"\*\*[^*\s][^*]*\*\*")]
    private static partial Regex MarkdownBoldPattern();
}
