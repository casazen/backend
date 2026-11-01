using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using Ganss.Xss;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Allowlist sanitizer for the editorial HTML of public SEO pages (A8-08, A9-29).
/// Applied when a revision is stored and again when the public endpoint serves it.
/// Mirrors the frontend helper <c>src/lib/sanitize-html.ts</c> (DOMPurify): keep both lists aligned.
/// </summary>
public static class SeoHtmlSanitizer
{
    /// <summary>Editorial tags that survive sanitization, without attributes except <c>a[href]</c>.</summary>
    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "br", "h2", "h3", "h4", "ul", "ol", "li", "strong", "em", "a",
        "table", "thead", "tbody", "tr", "th", "td",
    };

    /// <summary>URL schemes accepted in <c>a[href]</c>. Relative URLs are rejected as well.</summary>
    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "mailto",
    };

    private const string LinkRel = "noopener noreferrer";

    /// <summary>
    /// Disallowed elements are unwrapped (their text stays readable) except these, which are dropped
    /// together with their content: code, styles, embedded documents and foreign (SVG/MathML) content.
    /// </summary>
    private static readonly HashSet<string> DropWithContentTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "iframe", "frame", "frameset", "object", "embed", "applet", "svg", "math",
        "template", "noscript", "noembed", "noframes", "xmp", "plaintext", "title", "head", "textarea",
        "select", "audio", "video", "canvas",
    };

    // HtmlSanitizer.Sanitize is thread-safe once the instance is configured (see package README).
    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

    public static string Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        return Sanitizer.Sanitize(html).Trim();
    }

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer(new HtmlSanitizerOptions
        {
            AllowedTags = new HashSet<string>(AllowedTags, StringComparer.OrdinalIgnoreCase),
            AllowedAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "href" },
            AllowedSchemes = new HashSet<string>(AllowedSchemes, StringComparer.OrdinalIgnoreCase),
            UriAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "href" },
            AllowedCssProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AllowedAtRules = new HashSet<CssRuleType>(),
            AllowedCssClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AllowDataAttributes = false,
        })
        {
            KeepChildNodes = true,
        };

        sanitizer.RemovingTag += (_, e) =>
        {
            if (!DropWithContentTags.Contains(e.Tag.LocalName))
                return;

            e.Tag.Remove();
            e.Cancel = true;
        };

        // Only absolute http/https/mailto links: relative or scheme-less hrefs are dropped too.
        sanitizer.FilterUrl += (_, e) =>
        {
            if (e.SanitizedUrl is null || !IsAllowedAbsoluteUrl(e.SanitizedUrl))
                e.SanitizedUrl = null;
        };

        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is IElement { LocalName: "a" } link)
                link.SetAttribute("rel", LinkRel);
        };

        sanitizer.PostProcessDom += (_, e) => RemoveEmptyElements(e.Document);

        return sanitizer;
    }

    /// <summary>Elements that carry text: when sanitizing leaves one with none, nothing of it is worth showing.</summary>
    private const string TextElements = "li, p, h2, h3, h4, a, strong, em, ul, ol";

    /// <summary>
    /// Drops what the sanitizer left empty: an item whose link lost its address or its text, a paragraph made of a removed
    /// tag. Without it a list of sources ends with a bullet and nothing after it (seen on a published guide). Repeated until
    /// nothing is left to drop, because removing the last item empties its list. Table cells are not touched: an empty cell
    /// is still a cell.
    /// </summary>
    private static void RemoveEmptyElements(IDocument document)
    {
        if (document.Body is null)
            return;

        bool removed;
        do
        {
            removed = false;
            foreach (var element in document.Body.QuerySelectorAll(TextElements).ToList())
            {
                if (!string.IsNullOrWhiteSpace(element.TextContent))
                    continue;

                element.Remove();
                removed = true;
            }
        }
        while (removed);
    }

    private static bool IsAllowedAbsoluteUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && AllowedSchemes.Contains(uri.Scheme);
}
