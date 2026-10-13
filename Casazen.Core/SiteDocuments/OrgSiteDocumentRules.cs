using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Exceptions;

namespace Casazen.Core.SiteDocuments;

/// <summary>
/// Validation, normalization and rendering of the operator documents of the public site (BK-14, A3-21). One place for
/// the rules; the console mirrors the limits in <c>src/features/settings/site-documents/site-documents-rules.ts</c>.
/// </summary>
/// <remarks>
/// The text is never trusted as HTML: raw HTML in the input is refused, every character is HTML-encoded before the few
/// Markdown marks become tags, and links are limited to http, https and mailto. The service then passes the result
/// through the allow-list sanitizer as a second line of defense, and the frontend sanitizes it again before rendering.
/// CasaZen writes no legal text for the operator: this only stores and shows what the operator provides.
/// </remarks>
public static partial class OrgSiteDocumentRules
{
    public const string ContentRequiredCode = "org_document_content_required";
    public const string ContentTooLongCode = "org_document_content_too_long";
    public const string HtmlNotAllowedCode = "org_document_html_not_allowed";
    public const string LinkInvalidCode = "org_document_link_invalid";
    public const string UrlRequiredCode = "org_document_url_required";
    public const string UrlInvalidCode = "org_document_url_invalid";

    /// <summary>Longest text, in characters after normalization: a long privacy notice is about 15,000.</summary>
    public const int ContentMaxLength = 50_000;

    /// <summary>Longest external address (the usual browser limit).</summary>
    public const int ExternalUrlMaxLength = 2048;

    /// <summary>Public route names of the kinds (<c>/api/public/orgs/{slug}/documents/{kind}</c>).</summary>
    public static string RouteName(OrgSiteDocumentKind kind) => kind switch
    {
        OrgSiteDocumentKind.Privacy => "privacy",
        OrgSiteDocumentKind.Terms => "terms",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>The kind of a route name (<c>privacy</c>, <c>terms</c>, case-insensitive); false for anything else.</summary>
    public static bool TryParseKind(string? routeName, out OrgSiteDocumentKind kind)
    {
        switch (routeName?.Trim().ToLowerInvariant())
        {
            case "privacy":
                kind = OrgSiteDocumentKind.Privacy;
                return true;
            case "terms":
                kind = OrgSiteDocumentKind.Terms;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    /// <summary>
    /// The text as stored: line breaks unified, control characters removed, outer blank space trimmed and runs of
    /// blank lines reduced to one. Throws <see cref="DomainRuleException"/> when it is empty, longer than
    /// <see cref="ContentMaxLength"/>, contains raw HTML or has a link that is not http, https or mailto.
    /// </summary>
    public static string NormalizeContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new DomainRuleException(ContentRequiredCode, "OrgDocumentContentRequired");

        var text = LineBreaks().Replace(content, "\n");
        text = ControlCharacters().Replace(text, string.Empty);
        text = BlankLines().Replace(text.Trim(), "\n\n");
        if (text.Length == 0)
            throw new DomainRuleException(ContentRequiredCode, "OrgDocumentContentRequired");

        if (text.Length > ContentMaxLength)
            throw new DomainRuleException(ContentTooLongCode, "OrgDocumentContentTooLong", ContentMaxLength);

        if (HtmlTag().IsMatch(text))
            throw new DomainRuleException(HtmlNotAllowedCode, "OrgDocumentHtmlNotAllowed");

        foreach (Match link in MarkdownLink().Matches(text))
        {
            if (!IsAllowedLink(link.Groups["url"].Value))
                throw new DomainRuleException(LinkInvalidCode, "OrgDocumentLinkInvalid");
        }

        return text;
    }

    /// <summary>
    /// The external address as stored (absolute, normalized). Only https, no credentials in the address and a host
    /// with a dot; throws <see cref="DomainRuleException"/> otherwise.
    /// </summary>
    public static string NormalizeExternalUrl(string? url)
    {
        var trimmed = url?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new DomainRuleException(UrlRequiredCode, "OrgDocumentUrlRequired");

        if (trimmed.Length > ExternalUrlMaxLength
            || trimmed.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length > 0
            || !uri.Host.Contains('.'))
        {
            throw new DomainRuleException(UrlInvalidCode, "OrgDocumentUrlInvalid");
        }

        var normalized = uri.AbsoluteUri;
        if (normalized.Length > ExternalUrlMaxLength)
            throw new DomainRuleException(UrlInvalidCode, "OrgDocumentUrlInvalid");
        return normalized;
    }

    /// <summary>
    /// HTML of a normalized text, built from encoded text only: paragraphs (a line break inside a paragraph becomes
    /// <c>&lt;br&gt;</c>), headings (<c>#</c>, <c>##</c>, <c>###</c> → h2, h3, h4: the page title is the h1), bullet lists
    /// (<c>- </c> or <c>* </c>), <c>**bold**</c>, <c>*italic*</c> and <c>[text](https://…)</c> links. Numbered lines
    /// stay paragraphs, so clause numbers are kept exactly as written.
    /// </summary>
    public static string ToHtml(string normalizedContent)
    {
        var html = new StringBuilder();
        var open = OpenBlock.None;

        void Close()
        {
            html.Append(open switch
            {
                OpenBlock.Paragraph => "</p>",
                OpenBlock.List => "</ul>",
                _ => string.Empty,
            });
            open = OpenBlock.None;
        }

        foreach (var rawLine in normalizedContent.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                Close();
                continue;
            }

            var heading = Heading().Match(line);
            if (heading.Success)
            {
                Close();
                var level = heading.Groups["marks"].Length + 1;
                html.Append($"<h{level}>{Inline(heading.Groups["text"].Value)}</h{level}>");
                continue;
            }

            var bullet = Bullet().Match(line);
            if (bullet.Success)
            {
                if (open != OpenBlock.List)
                {
                    Close();
                    html.Append("<ul>");
                    open = OpenBlock.List;
                }

                html.Append($"<li>{Inline(bullet.Groups["text"].Value)}</li>");
                continue;
            }

            if (open == OpenBlock.Paragraph)
            {
                html.Append("<br>");
            }
            else
            {
                Close();
                html.Append("<p>");
                open = OpenBlock.Paragraph;
            }

            html.Append(Inline(line));
        }

        Close();
        return html.ToString();
    }

    private enum OpenBlock
    {
        None,
        Paragraph,
        List,
    }

    private static bool IsAllowedLink(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto);

    /// <summary>Encodes the text, then turns links and emphasis into tags; emphasis is never applied inside an address.</summary>
    private static string Inline(string text)
    {
        var encoded = Encode(text);
        var result = new StringBuilder();
        var position = 0;
        foreach (Match link in MarkdownLink().Matches(encoded))
        {
            result.Append(Emphasis(encoded[position..link.Index]));
            position = link.Index + link.Length;

            var url = WebUtility.HtmlDecode(link.Groups["url"].Value).Trim();
            if (IsAllowedLink(url))
                result.Append("<a href=\"").Append(Encode(url)).Append("\">").Append(Emphasis(link.Groups["label"].Value)).Append("</a>");
            else
                result.Append(link.Value);
        }

        result.Append(Emphasis(encoded[position..]));
        return result.ToString();
    }

    /// <summary>
    /// The five characters that can open markup or end an attribute become entities; letters and symbols stay as they are
    /// (<c>WebUtility.HtmlEncode</c> would write "à" as <c>&amp;#224;</c>, which is valid but unreadable in the stored HTML).
    /// </summary>
    private static string Encode(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&#39;", StringComparison.Ordinal);

    private static string Emphasis(string encoded)
    {
        var bold = Bold().Replace(encoded, "<strong>$1</strong>");
        return Italic().Replace(bold, "<em>$1</em>");
    }

    [GeneratedRegex(@"\r\n|\r|\u2028|\u2029")]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]")]
    private static partial Regex ControlCharacters();

    [GeneratedRegex(@"\n[ \t]*\n(?:[ \t]*\n)+")]
    private static partial Regex BlankLines();

    /// <summary>An HTML start or end tag (<c>&lt;p&gt;</c>, <c>&lt;a href="x"&gt;</c>, <c>&lt;/div&gt;</c>), a comment or a processing instruction. <c>&lt;https://x&gt;</c> is not one.</summary>
    [GeneratedRegex(@"</?[A-Za-z][A-Za-z0-9-]*(?:\s[^<>]*)?/?>|<[!?]")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\[(?<label>[^\]\n]*)\]\(\s*(?<url>[^)\s]*)[^)\n]*\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"^(?<marks>#{1,3})\s+(?<text>.+)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^[-*•]\s+(?<text>.+)$")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"\*\*(?=\S)(.+?)(?<=\S)\*\*")]
    private static partial Regex Bold();

    [GeneratedRegex(@"(?<![*\w])\*(?=\S)([^*\n]+?)(?<=\S)\*(?![*\w])")]
    private static partial Regex Italic();
}
