using System.Data;
using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Tests.Unit.Infrastructure;
using Xunit;

namespace Casazen.Tests.Unit.Messages;

/// <summary>
/// E05/F02/US01/T01: the list of the hub, run on the PostgreSQL provider with every kind of scope and every filter, without a server (see
/// <see cref="NpgsqlTranslationProbe"/>). The in-memory provider evaluates a query the way .NET does and accepts what Npgsql cannot turn into SQL; this
/// fails here, before CI, when the concatenation of the two sources, the scope or a filter has no translation, and it checks the shape of the
/// statements: one <c>UNION ALL</c> paged in the database, and no statement of the list ever names the text of a message or the address of a file.
/// </summary>
public class MessageHubNpgsqlTranslationTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly Guid PropertyId = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly Guid BookingId = Guid.Parse("66666666-7777-4888-9999-aaaaaaaaaaaa");

    private static HostScope ScopeOf(string kind) => kind switch
    {
        "collaborator" => new HostScope(OrgId, GrantedToUserId: "auth0|collaboratore"),
        "collaborator-one-area" => new HostScope(OrgId, GrantedToUserId: "auth0|collaboratore", GrantedMode: Casazen.Core.Entities.Enums.RentalMode.Short),
        "account-in-no-team" => new HostScope(OrgId, OwnerId: "auth0|titolare"),
        _ => new HostScope(OrgId),
    };

    private static readonly Dictionary<string, MessageHubFilter> Filters = new()
    {
        ["plain"] = new MessageHubFilter(),
        ["to-answer"] = new MessageHubFilter(NeedsReply: true),
        ["the-others"] = new MessageHubFilter(NeedsReply: false),
        ["property-channel-booking"] = new MessageHubFilter(PropertyId: PropertyId, Channel: MessageHubChannel.Airbnb, BookingId: BookingId),
        ["direct-only"] = new MessageHubFilter(Channel: MessageHubChannel.Direct),
        ["unread-requests"] = new MessageHubFilter(Unread: true, Kind: MessageHubThreadKind.Request),
        ["read-reservations"] = new MessageHubFilter(Unread: false, Kind: MessageHubThreadKind.Reservation),
        ["a-name"] = new MessageHubFilter(Query: "Anna Ross"),
        ["a-booking-code"] = new MessageHubFilter(Query: "7K3M9-PQ2XV"),
        ["everything-at-once"] = new MessageHubFilter(true, PropertyId, MessageHubChannel.Booking, BookingId, true, MessageHubThreadKind.Reservation, "mar 7K3M9"),
    };

    public static TheoryData<string, string> EveryFilterWithEveryScope()
    {
        var data = new TheoryData<string, string>();
        foreach (var filter in Filters.Keys)
        {
            foreach (var scope in new[] { "collaborator", "collaborator-one-area", "account-in-no-team", "whole-org" })
                data.Add(filter, scope);
        }

        return data;
    }

    /// <summary>
    /// One row of each source, in the order of the columns of the page statement (the members of the row of the list, in the order the service
    /// projects them), so the probe goes on to the statements that read the last message of each conversation of the page.
    /// </summary>
    private static DataTable? PageRowsOf(string sql)
    {
        if (!IsThePage(sql))
            return null;

        var table = new DataTable();
        table.Columns.Add("Source", typeof(int));
        table.Columns.Add("KeyId", typeof(Guid));
        table.Columns.Add("Channel", typeof(int));
        table.Columns.Add("Kind", typeof(int));
        table.Columns.Add("PropertyId", typeof(Guid));
        table.Columns.Add("PropertyName", typeof(string));
        table.Columns.Add("BookingId", typeof(Guid));
        table.Columns.Add("BookingCode", typeof(string));
        table.Columns.Add("GuestName", typeof(string));
        table.Columns.Add("CheckInDate", typeof(DateTime));
        table.Columns.Add("CheckOutDate", typeof(DateTime));
        table.Columns.Add("BookingStatus", typeof(int));
        table.Columns.Add("IsArchived", typeof(bool));
        table.Columns.Add("LastMessageAt", typeof(DateTime));
        table.Columns.Add("UnreadCount", typeof(int));
        table.Columns.Add("NeedsReply", typeof(bool));
        var at = new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);
        table.Rows.Add(0, BookingId, 0, 1, PropertyId, "Trullo", BookingId, "7K3M9PQ2XV", "Anna Ross", at, at.AddDays(3), 2, false, at, 1, true);
        table.Rows.Add(1, Guid.NewGuid(), 1, 2, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, "Marta R.", DBNull.Value, DBNull.Value, DBNull.Value, false, at, 0, false);
        return table;
    }

    [Theory]
    [MemberData(nameof(EveryFilterWithEveryScope))]
    public async Task List_IsTranslatedToSqlByNpgsql_AsOneUnionPagedInTheDatabase(string filter, string scope)
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements, count: 2, rows: PageRowsOf);
        var service = MessageHubTestKit.Service(db);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.ListAsync(ScopeOf(scope), Filters[filter], 1, 20));

        // The count, the page, and for the two conversations of the page the id of the last message and the message of each source.
        Assert.True(statements.Count >= 6, $"{filter}/{scope}: only {statements.Count} of the 6 statements were reached.");

        var page = Assert.Single(statements, IsThePage);
        Assert.Equal(1, CountOf(page, "UNION ALL"));
        Assert.Contains("ORDER BY", page, StringComparison.Ordinal);
        Assert.Contains("OFFSET", page, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(statements.First(), "UNION ALL"));
        Assert.StartsWith("SELECT COUNT", statements.First().TrimStart(), StringComparison.OrdinalIgnoreCase);

        // What is searched and ordered is plain columns: the words of a message (encrypted at rest) and the address of a file are in no statement
        // of the list but the two that read the last message of the page, and the address is in none.
        Assert.All(statements, sql => Assert.DoesNotContain("AttachmentUrl", sql, StringComparison.Ordinal));
        Assert.DoesNotContain("TextEncrypted", statements.First(), StringComparison.Ordinal);
        Assert.DoesNotContain("TextEncrypted", page, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Text\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARestrictedScope_IsAnExistsOnTheGrantsInBothSources_AndAnOrgWideOneHasNone()
    {
        var restricted = new List<string>();
        await using (var db = NpgsqlTranslationProbe.NewContext(restricted, count: 1))
            await NpgsqlTranslationProbe.AssertTranslatesAsync(() => MessageHubTestKit.Service(db).ListAsync(ScopeOf("collaborator"), new MessageHubFilter(), 1, 20));
        var orgWide = new List<string>();
        await using (var db = NpgsqlTranslationProbe.NewContext(orgWide, count: 1))
            await NpgsqlTranslationProbe.AssertTranslatesAsync(() => MessageHubTestKit.Service(db).ListAsync(ScopeOf("whole-org"), new MessageHubFilter(), 1, 20));

        var restrictedPage = Assert.Single(restricted, IsThePage);
        var orgWidePage = Assert.Single(orgWide, IsThePage);

        // One EXISTS on the grants of the person for each source, none for the whole org; every source names the org of the scope.
        Assert.Equal(2, CountOf(restrictedPage, "PropertyMemberAccesses"));
        Assert.Equal(0, CountOf(orgWidePage, "PropertyMemberAccesses"));
        Assert.Equal(2, CountOf(restrictedPage, "\"OrgId\" = @"));
        Assert.Equal(2, CountOf(orgWidePage, "\"OrgId\" = @"));
    }

    /// <summary>The statement of the page: it selects the columns of the row of the list from the concatenation of the two sources.</summary>
    private static bool IsThePage(string sql) =>
        sql.TrimStart().StartsWith("SELECT u.\"Source\"", StringComparison.Ordinal) && sql.Contains("UNION ALL", StringComparison.Ordinal);

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;

        return count;
    }
}
