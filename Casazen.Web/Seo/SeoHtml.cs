using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace Casazen.Web.Seo;

/// <summary>
/// Escaping and the few text helpers of the crawler pages (BK-15). Every text that comes from a host (name, tagline,
/// description, city, house rules) goes through <see cref="Encode"/> before it reaches the markup, and every URL through
/// <see cref="TryHttpsUrl"/>: a host cannot inject markup or a <c>javascript:</c> link into a page crawlers index.
/// </summary>
public static class SeoHtml
{
    /// <summary>Longest description of a search result snippet (the first 155 characters, spec AC13).</summary>
    public const int DescriptionMaxLength = 155;

    /// <summary>
    /// Encodes what HTML gives a meaning to (<c>&lt; &gt; &amp; " '</c> and a few more) and leaves every letter as it is, so
    /// the page stays readable UTF-8 text (the default encoders turn every accented letter into an entity).
    /// </summary>
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

    public static string Encode(string? value) => string.IsNullOrEmpty(value) ? string.Empty : Encoder.Encode(value);

    /// <summary>
    /// The URL when it is an absolute https URL without user info, otherwise <c>null</c>. Preview images and links come
    /// from the database (public storage URLs): anything else is dropped, not rewritten.
    /// </summary>
    public static string? TryHttpsUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo))
            return null;

        return uri.AbsoluteUri;
    }

    /// <summary>White space collapsed to single spaces and trimmed (a description with line breaks is one snippet).</summary>
    public static string Collapse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
                builder.Append(' ');
            pendingSpace = false;
            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// <paramref name="value"/> cut to at most <paramref name="maxLength"/> characters at a word boundary, with an
    /// ellipsis when something was cut; white space is collapsed first.
    /// </summary>
    public static string Truncate(string? value, int maxLength = DescriptionMaxLength)
    {
        var text = Collapse(value);
        if (text.Length <= maxLength)
            return text;

        var cut = text[..(maxLength - 1)];
        var lastSpace = cut.LastIndexOf(' ');
        if (lastSpace >= maxLength / 2)
            cut = cut[..lastSpace];

        return cut.TrimEnd(' ', ',', ';', ':', '.', '-') + "…";
    }

    /// <summary>
    /// Paragraphs of a plain text (blank lines separate them), each escaped, as <c>&lt;p&gt;</c> elements.
    /// </summary>
    public static string Paragraphs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var collapsed = Collapse(paragraph);
            if (collapsed.Length > 0)
                builder.Append("<p>").Append(Encode(collapsed)).Append("</p>\n");
        }

        return builder.ToString();
    }
}
