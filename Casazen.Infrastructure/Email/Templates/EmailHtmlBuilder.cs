using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>
/// Small email template engine: a fixed HTML layout plus blocks whose texts come from <see cref="EmailTexts"/> in the
/// requested culture. Texts are trusted markup; every dynamic value (names, notes, property, links) is HTML-encoded
/// before it is placed in the text, so user input can never add markup or links to an email (A4-08, A5-33).
/// </summary>
/// <remarks>
/// <b>The guest variant (E50/F01/US03/T01, NF-155, D3).</b> <c>new EmailHtmlBuilder(culture, branding)</c> is the layout of an e-mail to a guest with the brand of its host: a header bar
/// in the primary color of the host's showcase with its logo (<c>https</c> only, the name as alt text, bounded in size) and its name, buttons in the same color with the text that
/// reads better on it, a small «powered by CasaZen» footer instead of the CasaZen one, and, on the <see cref="EmailContent"/> it builds, the name of the host as the sender's display name
/// and the host as reply-to. The brand is cleaned first (<see cref="GuestEmailBranding.Cleaned"/>: a hostile logo, color or name is dropped) and every value is HTML-encoded. With no
/// brand (<c>null</c>, or nothing usable in it) the e-mail is the CasaZen one, <b>byte for byte</b> what it always was: the e-mails of the host, of the staff and of the suppliers never
/// get one.
/// </remarks>
public sealed class EmailHtmlBuilder(CultureInfo culture)
{
    private const string MutedStyle = "font-size:13px;color:#71717a;";
    private const string CodeStyle = "font-size:22px;letter-spacing:2px;";

    // Keeps accented letters readable; still encodes <, >, &, quotes and control characters.
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

    private readonly StringBuilder _body = new();

    /// <summary>The cleaned brand of the host of a guest e-mail; <c>null</c> for every other e-mail, which keeps the CasaZen layout.</summary>
    private readonly GuestEmailBranding? _branding;

    /// <summary>The layout of an e-mail to a guest with the brand of its host (see the remarks of the type); <c>null</c> or an unusable brand is the CasaZen layout.</summary>
    public EmailHtmlBuilder(CultureInfo culture, GuestEmailBranding? branding)
        : this(culture)
    {
        _branding = branding?.Cleaned() is { IsEmpty: false } cleaned ? cleaned : null;
    }

    public CultureInfo Culture { get; } = culture;

    /// <summary>HTML-encodes a dynamic value.</summary>
    public static string Encode(string? value) => string.IsNullOrEmpty(value) ? string.Empty : Encoder.Encode(value);

    public EmailHtmlBuilder Heading(string key, params object?[] args) =>
        Append($"<h1 style=\"font-size:20px;\">{Format(key, args)}</h1>");

    public EmailHtmlBuilder Paragraph(string key, params object?[] args) => Append($"<p>{Format(key, args)}</p>");

    public EmailHtmlBuilder Muted(string key, params object?[] args) =>
        Append($"<p style=\"{MutedStyle}\">{Format(key, args)}</p>");

    /// <summary>
    /// A short value the reader has to type or copy (a lock code), large and spaced, in a paragraph of its own. It has no words of its own, so
    /// it is not a text of <see cref="EmailTexts"/> (it would be the same in Italian and English). The value is HTML-encoded.
    /// </summary>
    public EmailHtmlBuilder Code(string? value) => Append($"<p><strong style=\"{CodeStyle}\">{Encode(value)}</strong></p>");

    /// <summary>A highlighted free-text value (host notes, invite message) under a label; skipped when empty.</summary>
    public EmailHtmlBuilder Quote(string labelKey, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return this;

        var lines = value.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(Encode);
        return Append(
            "<p style=\"margin:16px 0;padding:12px;background:#f4f4f5;border-radius:8px;\">"
            + $"<strong>{Text(labelKey)}</strong><br />{string.Join("<br />", lines)}</p>");
    }

    /// <summary>A bulleted list with one localized line per entry (e.g. the amounts of a booking); skipped when empty.</summary>
    public EmailHtmlBuilder List(IEnumerable<(string Key, object?[] Args)> lines)
    {
        var items = lines.Select(line => $"<li>{Format(line.Key, line.Args)}</li>").ToList();
        return items.Count == 0
            ? this
            : Append($"<ul style=\"padding-left:20px;\">{string.Concat(items)}</ul>");
    }

    /// <summary>A bulleted list of dynamic values (e.g. property names), each HTML-encoded; skipped when empty.</summary>
    public EmailHtmlBuilder ValueList(IEnumerable<string?> values)
    {
        var items = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => $"<li>{Encode(value)}</li>")
            .ToList();
        return items.Count == 0
            ? this
            : Append($"<ul style=\"padding-left:20px;\">{string.Concat(items)}</ul>");
    }

    /// <summary>
    /// A bulleted list of links, each with a short detail after it (e.g. the suppliers offered to a customer): the label and the detail are
    /// HTML-encoded and every link has to be an absolute http(s) URL; skipped when empty.
    /// </summary>
    public EmailHtmlBuilder LinkList(IEnumerable<(string Label, string Url, string? Detail)> items)
    {
        var rows = items
            .Select(item => $"<li><a href=\"{EncodeUrl(item.Url)}\">{Encode(item.Label)}</a>"
                            + (string.IsNullOrWhiteSpace(item.Detail) ? string.Empty : $" — {Encode(item.Detail)}")
                            + "</li>")
            .ToList();
        return rows.Count == 0
            ? this
            : Append($"<ul style=\"padding-left:20px;\">{string.Concat(rows)}</ul>");
    }

    public EmailHtmlBuilder Button(string key, string url) =>
        Append(
            $"<p><a href=\"{EncodeUrl(url)}\" style=\"display:inline-block;padding:12px 20px;background:{Accent};"
            + $"color:{AccentText};text-decoration:none;border-radius:6px;font-weight:bold;\">{Text(key)}</a></p>");

    /// <summary>The raw link under a short explanation, for clients that do not render buttons.</summary>
    public EmailHtmlBuilder LinkFallback(string key, string url)
    {
        var encoded = EncodeUrl(url);
        return Append($"<p style=\"{MutedStyle}\">{Text(key)}<br /><a href=\"{encoded}\">{encoded}</a></p>");
    }

    /// <summary>Date of a stay (date-only value stored as midnight UTC: no time zone conversion).</summary>
    public string FormatDate(DateTime date) => date.ToString(Text("Format_Date"), Culture);

    /// <summary>A UTC instant shown in Italian time (Europe/Rome).</summary>
    public string FormatInstant(DateTime utc)
    {
        var asUtc = utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(asUtc, RomeCalendar.TimeZone).ToString(Text("Format_DateTime"), Culture);
    }

    /// <summary>
    /// Wraps the blocks in the layout. The subject is plain text (values are not HTML-encoded there). With the brand of a host (guest e-mails) the layout has its header and
    /// the «powered by CasaZen» footer, and the content carries the host's name as the sender's display name and the host as reply-to (<see cref="EmailContent.FromName"/>,
    /// <see cref="EmailContent.ReplyTo"/>); without one it is the CasaZen layout, as ever.
    /// </summary>
    public EmailContent Build(string subjectKey, params object?[] subjectArgs)
    {
        var subject = SingleLine(string.Format(Culture, Text(subjectKey), subjectArgs.Select(ToText).ToArray<object?>()));
        var html =
            "<!DOCTYPE html>\n"
            + $"<html lang=\"{Culture.TwoLetterISOLanguageName}\">\n"
            + "<head><meta charset=\"utf-8\" /><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />"
            + $"<title>{Encode(subject)}</title></head>\n"
            + "<body style=\"font-family:Arial,sans-serif;color:#18181b;line-height:1.5;\">\n"
            + BrandHeader()
            + _body
            + $"<p style=\"font-size:12px;color:#a1a1aa;\">{Text(_branding is null ? "Layout_Footer" : "Layout_PoweredBy")}</p>\n"
            + "</body>\n</html>\n";

        return new EmailContent(subject, html, _branding?.ReplyTo) { FromName = _branding?.Name };
    }

    /// <summary>The color of the buttons (and of the header) of this e-mail: the one of the host's brand, otherwise the CasaZen blue they have always had.</summary>
    private string Accent => _branding?.Accent ?? GuestEmailBranding.DefaultAccent;

    /// <summary>The color of the text of a button: the one that reads better on <see cref="Accent"/>.</summary>
    private string AccentText => _branding?.AccentText ?? GuestEmailBranding.DefaultAccentText;

    /// <summary>
    /// The bar at the top of an e-mail with the brand of a host: its logo, on a white chip so that any logo reads on any color (an <c>https</c> address only, the name as alt text, at most
    /// 40 pixels high and 160 wide), and its name, on the primary color. Nothing for an e-mail without a brand, or with a brand that has neither a logo nor a name.
    /// </summary>
    private string BrandHeader()
    {
        if (_branding is not { } brand || (brand.Name is null && brand.LogoUrl is null))
            return string.Empty;

        var logo = brand.LogoUrl is null
            ? string.Empty
            : "<span style=\"display:inline-block;background:#ffffff;padding:6px;border-radius:6px;margin-right:12px;vertical-align:middle;\">"
              + $"<img src=\"{Encode(brand.LogoUrl)}\" alt=\"{Encode(brand.Name)}\" height=\"40\" "
              + "style=\"display:block;max-height:40px;max-width:160px;width:auto;height:auto;border:0;\" /></span>";
        var name = brand.Name is null
            ? string.Empty
            : $"<span style=\"font-size:18px;font-weight:bold;vertical-align:middle;\">{Encode(brand.Name)}</span>";

        return $"<div style=\"background:{brand.Accent};color:{brand.AccentText};padding:14px 20px;border-radius:8px;margin-bottom:20px;\">{logo}{name}</div>\n";
    }

    private string Text(string key) => EmailTexts.Get(key, Culture);

    private string Format(string key, object?[] args) =>
        string.Format(Culture, Text(key), args.Select(arg => (object?)Encode(ToText(arg))).ToArray());

    private string ToText(object? value) => value switch
    {
        null => string.Empty,
        DateTime date => FormatDate(date),
        IFormattable formattable => formattable.ToString(null, Culture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string EncodeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("Email links must be absolute http(s) URLs.", nameof(url));

        return Encode(url);
    }

    private static string SingleLine(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private EmailHtmlBuilder Append(string html)
    {
        _body.Append(html).Append('\n');
        return this;
    }
}
