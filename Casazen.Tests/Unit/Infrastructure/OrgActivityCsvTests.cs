using System.Text;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// AM-02b: the CSV of the activity log. A header that never changes, one line per entry, RFC 4180 escaping, nothing a
/// spreadsheet would run as a formula, an empty log that is still a valid file with its header, and the headers of the download.
/// The cells are ids and codes: there is no name, email or amount in any column.
/// </summary>
public class OrgActivityCsvTests
{
    private static readonly DateTime When = new(2026, 10, 1, 8, 5, 3, DateTimeKind.Utc);

    private static OrgActivityItem Item(
        string? actor = "auth0|owner",
        string subject = "auth0|anna",
        IReadOnlyDictionary<string, string>? details = null,
        OrgActivityType type = OrgActivityType.MemberRoleChanged,
        DateTime? when = null) => new(
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        when ?? When,
        actor,
        OrgActivityArea.Account,
        type,
        OrgActivitySubjectType.Member,
        subject,
        details ?? new Dictionary<string, string>());

    [Fact]
    public void Header_IsFixed()
    {
        Assert.Equal("id,when,actor,area,type,subjectType,subjectId,details", OrgActivityCsv.Header);
        Assert.Equal(8, OrgActivityCsv.Columns.Count);
    }

    [Fact]
    public void Line_HasEveryColumn_InTheOrderOfTheHeader()
    {
        var line = OrgActivityCsv.Line(Item(
            details: new Dictionary<string, string> { ["toRole"] = "Admin", ["fromRole"] = "Collaborator" }));

        Assert.Equal(
            "11111111-2222-3333-4444-555555555555,2026-10-01T08:05:03.000000Z,auth0|owner,account,MemberRoleChanged,Member,auth0|anna,fromRole=Collaborator;toRole=Admin",
            line);
        Assert.Equal(OrgActivityCsv.Columns.Count, line.Split(',').Length);
    }

    [Fact]
    public void Line_TheInstantIsUtcIso8601ToTheMicrosecond()
    {
        var local = new DateTime(2026, 10, 1, 8, 5, 3, DateTimeKind.Utc).AddTicks(1_234_560);

        Assert.Contains(",2026-10-01T08:05:03.123456Z,", OrgActivityCsv.Line(Item(when: local)), StringComparison.Ordinal);
    }

    [Fact]
    public void Line_ALineWithNoPersonAndNoDetails_HasEmptyCells()
    {
        var line = OrgActivityCsv.Line(Item(actor: null, type: OrgActivityType.OrgNameChanged));

        Assert.Equal("11111111-2222-3333-4444-555555555555,2026-10-01T08:05:03.000000Z,,account,OrgNameChanged,Member,auth0|anna,", line);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    [InlineData("auth0|abc.def", "auth0|abc.def")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    [InlineData("line\r\nbreak", "\"line\r\nbreak\"")]
    [InlineData("a;b=c", "a;b=c")]
    public void Cell_FollowsRfc4180(string? value, string expected)
    {
        Assert.Equal(expected, OrgActivityCsv.Cell(value));
    }

    [Theory]
    [InlineData("=SUM(A1:A9)", "'=SUM(A1:A9)")]
    [InlineData("+39 333", "'+39 333")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("\tindented", "'\tindented")]
    [InlineData("=1,2", "\"'=1,2\"")]
    public void Cell_ANumberLookingOrFormulaLookingCell_CannotRunAsAFormula(string value, string expected)
    {
        Assert.Equal(expected, OrgActivityCsv.Cell(value));
    }

    [Fact]
    public void Line_AnIdThatWouldBeAFormula_IsDefused_AndTheLineKeepsItsColumns()
    {
        var line = OrgActivityCsv.Line(Item(subject: "=HYPERLINK(\"http://evil\")"));

        Assert.Contains("\"'=HYPERLINK(\"\"http://evil\"\")\"", line, StringComparison.Ordinal);
    }

    // ─── The download ──────────────────────────────────────────────────────────────────────────────────

    private static async Task<(DefaultHttpContext Context, string Body)> ExecuteAsync(IAsyncEnumerable<OrgActivityItem> items)
    {
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        await new OrgActivityCsvResult(items, "activity-20261009.csv")
            .ExecuteResultAsync(new ActionContext(context, new RouteData(), new ActionDescriptor()));
        context.Response.Body.Position = 0;
        return (context, new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEnd());
    }

    private static async IAsyncEnumerable<OrgActivityItem> Items(params OrgActivityItem[] items)
    {
        foreach (var item in items)
            yield return item;

        await Task.CompletedTask;
    }

    [Fact]
    public async Task Result_WritesTheHeaderThenEachLine_WithCrLf()
    {
        var (_, body) = await ExecuteAsync(Items(Item(subject: "auth0|a"), Item(subject: "auth0|b")));

        var lines = body.Split("\r\n");
        Assert.Equal(OrgActivityCsv.Header, lines[0]);
        Assert.Contains("auth0|a", lines[1], StringComparison.Ordinal);
        Assert.Contains("auth0|b", lines[2], StringComparison.Ordinal);
        Assert.Equal(string.Empty, lines[3]);
        Assert.Equal(4, lines.Length);
        Assert.DoesNotContain("\n", body.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Result_AnEmptyLog_IsTheHeaderAlone()
    {
        var (_, body) = await ExecuteAsync(Items());

        Assert.Equal(OrgActivityCsv.Header + "\r\n", body);
    }

    [Fact]
    public async Task Result_IsAnUncachedUtf8CsvAttachment_WithoutABom()
    {
        var (context, body) = await ExecuteAsync(Items(Item()));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("text/csv; charset=utf-8", context.Response.ContentType);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Contains("attachment", context.Response.Headers.ContentDisposition.ToString(), StringComparison.Ordinal);
        Assert.Contains("activity-20261009.csv", context.Response.Headers.ContentDisposition.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("id,when", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Result_NoColumnHoldsAPersonalDatum_ForAJourneyOfEverySort()
    {
        var items = Enum.GetValues<OrgActivityType>()
            .Select(type => Item(
                subject: Guid.NewGuid().ToString(),
                type: type,
                details: new Dictionary<string, string> { ["role"] = "Admin" }))
            .ToArray();

        var (_, body) = await ExecuteAsync(Items(items));

        Assert.DoesNotContain("@", body, StringComparison.Ordinal);
        Assert.Equal(items.Length + 2, body.Split("\r\n").Length);
    }
}
