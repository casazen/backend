using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace Casazen.Web.Seo;

public static class SeoHtml
{
    public const int DescriptionMaxLength = 155;

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
