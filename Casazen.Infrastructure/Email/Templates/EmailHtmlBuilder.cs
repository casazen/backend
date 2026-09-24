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
public sealed class EmailHtmlBuilder(CultureInfo culture)
{
    private const string MutedStyle = "font-size:13px;color:#71717a;";

    // Keeps accented letters readable; still encodes <, >, &, quotes and control characters.
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

    private readonly StringBuilder _body = new();

    public CultureInfo Culture { get; } = culture;

    /// <summary>HTML-encodes a dynamic value.</summary>
    public static string Encode(string? value) => string.IsNullOrEmpty(value) ? string.Empty : Encoder.Encode(value);

    public EmailHtmlBuilder Heading(string key, params object?[] args) =>
        Append($"<h1 style=\"font-size:20px;\">{Format(key, args)}</h1>");

    public EmailHtmlBuilder Paragraph(string key, params object?[] args) => Append($"<p>{Format(key, args)}</p>");

    public EmailHtmlBuilder Muted(string key, params object?[] args) =>
        Append($"<p style=\"{MutedStyle}\">{Format(key, args)}</p>");

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

    public EmailHtmlBuilder Button(string key, string url) =>
        Append(
            $"<p><a href=\"{EncodeUrl(url)}\" style=\"display:inline-block;padding:12px 20px;background:#0d8abc;"
            + $"color:#ffffff;text-decoration:none;border-radius:6px;font-weight:bold;\">{Text(key)}</a></p>");

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

    /// <summary>Wraps the blocks in the layout. The subject is plain text (values are not HTML-encoded there).</summary>
    public EmailContent Build(string subjectKey, params object?[] subjectArgs)
    {
        var subject = SingleLine(string.Format(Culture, Text(subjectKey), subjectArgs.Select(ToText).ToArray<object?>()));
        var html =
            "<!DOCTYPE html>\n"
            + $"<html lang=\"{Culture.TwoLetterISOLanguageName}\">\n"
            + "<head><meta charset=\"utf-8\" /><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />"
            + $"<title>{Encode(subject)}</title></head>\n"
            + "<body style=\"font-family:Arial,sans-serif;color:#18181b;line-height:1.5;\">\n"
            + _body
            + $"<p style=\"font-size:12px;color:#a1a1aa;\">{Text("Layout_Footer")}</p>\n"
            + "</body>\n</html>\n";

        return new EmailContent(subject, html);
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
