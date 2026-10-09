using System.Globalization;
using System.Text;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// The CSV of the activity log (AM-02b): a fixed header, one line per line of the log, ids and codes only (no name, no email,
/// no amount: the log holds none). RFC 4180: <c>CRLF</c> line ends, a cell with a comma, a quote or a line break is quoted
/// with the quotes doubled; and a cell that a spreadsheet would read as a formula (it starts with <c>= + - @</c> or a tab) gets a
/// leading apostrophe. The instant is UTC, ISO 8601, to the microsecond. An empty log is the header alone.
/// </summary>
public static class OrgActivityCsv
{
    /// <summary>The columns, in order. The header never changes with the data.</summary>
    public static IReadOnlyList<string> Columns { get; } =
        ["id", "when", "actor", "area", "type", "subjectType", "subjectId", "details"];

    public static string Header => string.Join(',', Columns);

    private const string InstantFormat = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";

    /// <summary>The line of one entry, without the line end. <c>details</c> is <c>key=value;key=value</c> in key order.</summary>
    public static string Line(OrgActivityItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var details = string.Join(
            ';',
            item.Details.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => $"{d.Key}={d.Value}"));

        return string.Join(
            ',',
            Cell(item.Id.ToString()),
            Cell(item.When.ToUniversalTime().ToString(InstantFormat, CultureInfo.InvariantCulture)),
            Cell(item.ActorUserId),
            Cell(OrgActivityCatalog.AreaCode(item.Area)),
            Cell(item.Type.ToString()),
            Cell(item.SubjectType.ToString()),
            Cell(item.SubjectId),
            Cell(details));
    }

    /// <summary>One cell, escaped (see the class remarks). <c>null</c> is an empty cell.</summary>
    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        // CSV injection: spreadsheets evaluate a cell that starts with one of these.
        var cell = value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;

        return cell.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : cell;
    }
}

/// <summary>
/// Writes the CSV of the activity log to the response, one line at a time as the database yields them (the log is not held in
/// memory). The download is never cached and never sniffed as anything but CSV.
/// </summary>
public sealed class OrgActivityCsvResult(IAsyncEnumerable<OrgActivityItem> items, string fileName) : IActionResult
{
    private const string NewLine = "\r\n";

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var response = context.HttpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/csv; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers.ContentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
        {
            FileName = fileName,
        }.ToString();

        var cancellationToken = context.HttpContext.RequestAborted;
        await using var writer = new StreamWriter(response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        await writer.WriteAsync((OrgActivityCsv.Header + NewLine).AsMemory(), cancellationToken);

        await foreach (var item in items.WithCancellation(cancellationToken))
            await writer.WriteAsync((OrgActivityCsv.Line(item) + NewLine).AsMemory(), cancellationToken);

        await writer.FlushAsync(cancellationToken);
    }
}
