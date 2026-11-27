using System.Globalization;
using System.Text;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Infrastructure;

public static class OrgActivityCsv
{
    public static IReadOnlyList<string> Columns { get; } =
        ["id", "when", "actor", "area", "type", "subjectType", "subjectId", "details"];

    public static string Header => string.Join(',', Columns);

    private const string InstantFormat = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";

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

    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var cell = value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;

        return cell.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : cell;
    }
}

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
