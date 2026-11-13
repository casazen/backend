using System.Data;
using System.Text.RegularExpressions;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Hangfire;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// The owner portal operations (E07/F07/US01) on the PostgreSQL provider with no server (see <see cref="NpgsqlTranslationProbe"/>). InMemory evaluates a query the way
/// .NET does and accepts what Npgsql cannot turn into SQL, and these services read across orgs (the tenant filter ignored), through sub-queries built outside the
/// predicates, in pages, and with a correlated «not exists»: a query that has no translation fails here, before the CI, and not as a 500 of the portal or a red
/// background job.
/// </summary>
public class OwnerPortalNpgsqlTranslationTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly Guid PropertyId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid OwnerId = Guid.Parse("22222222-3333-4444-5555-666666666666");
    private static readonly Guid OwnerUserId = Guid.Parse("33333333-4444-5555-6666-777777777777");
    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 10, 9, 10, 0, 0, TimeSpan.Zero));

    private static readonly OwnerContactIndex ContactIndex = new(
        Options.Create(new OwnerPortalOptions()),
        Mock.Of<IHostEnvironment>(environment => environment.EnvironmentName == "Development"),
        NullLogger<OwnerContactIndex>.Instance);

    private const RegexOptions Compiled = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // The statements that need a row to let the code go on (the first of a call, whose answer the next statements are built from).
    private static readonly Regex DelegationSelect =
        new(@"^SELECT\s+\w+\.""Id"",\s*\w+\.""OrgId"",\s*\w+\.""PropertyOwnerId"",\s*\w+\.""Permissions""\s+FROM ""OwnerPortalContacts""", Compiled);

    private static readonly Regex OwnedPropertySelect = new(@"^SELECT\s+\w+\.""PropertyId""\s+FROM ""PropertyOwnerships""", Compiled);

    private static readonly Regex OwnerProfileSelect = new(@"^SELECT DISTINCT\s+\w+\.""OrgId"",\s*\w+\.""PropertyOwnerId""\s+FROM ""PropertyOwnerships""", Compiled);

    private static readonly Regex LapsingVersionSelect = new(@"^SELECT\s+\w+\.""Id"",\s*\w+\.""OwnerDocumentId"",\s*\w+\.""ExpiresOn""\s+FROM ""OwnerDocumentVersions""", Compiled);

    /// <summary>What the caller of the portal reaches: its own property, every permission.</summary>
    private static OwnerPortalAccess Access() =>
        new(OwnerUserId, [new OwnerPortalGrant(OrgId, OwnerId, PropertyId, Clock.GetUtcNow().UtcDateTime.AddYears(-1), null, OwnerPortalPermissions.Everything, null)]);

    private static OwnerStayService Stays(AppDbContext db) =>
        new(
            db,
            Mock.Of<ICalendarBlockService>(),
            Mock.Of<ICalendarChangeRunner>(),
            Mock.Of<IInternalActivityService>(),
            new ActivityLog(db, Clock),
            Clock,
            NullLogger<OwnerStayService>.Instance,
            Mock.Of<IBackgroundJobClient>());

    private static OwnerCollaborationService Collaboration(AppDbContext db) =>
        new(
            db,
            Mock.Of<IFileStorage>(),
            new OwnerFileContentScanner(),
            new OwnerPortalAccessResolver(db, Clock),
            new ActivityLog(db, Clock),
            Mock.Of<IInternalActivityService>(),
            Mock.Of<IEmailQueue>(),
            new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://casazen.example.test/" })),
            Mock.Of<IAuth0ManagementService>(),
            ContactIndex,
            Clock,
            NullLogger<OwnerCollaborationService>.Instance);

    private static OwnerPortalService Portal(AppDbContext db) => new(db, Clock);

    private static OwnerAccountingService Accounting(AppDbContext db) =>
        new(db, Mock.Of<IOwnerFiscalProfileService>(), Clock, NullLogger<OwnerAccountingService>.Instance);

    /// <summary>A table of one row: the columns are read by their position, in the order of the SELECT of the statement.</summary>
    private static DataTable Table(params (string Name, Type Type, object Value)[] columns)
    {
        var table = new DataTable();
        foreach (var (name, type, _) in columns)
            table.Columns.Add(name, type);

        table.Rows.Add(columns.Select(column => column.Value).ToArray());
        return table;
    }

    /// <summary>The delegation the account holds: the read of the ownerships of the owner that gave it is sent too.</summary>
    private static DataTable? DelegationOf(string sql) =>
        DelegationSelect.IsMatch(sql.TrimStart())
            ? Table(
                ("Id", typeof(Guid), Guid.NewGuid()),
                ("OrgId", typeof(Guid), OrgId),
                ("PropertyOwnerId", typeof(Guid), OwnerId),
                ("Permissions", typeof(int), (int)OwnerPortalPermission.DocumentsRead))
            : null;

    /// <summary>The property the owner holds: the read of its properties with their bookings is sent too.</summary>
    private static DataTable? OwnedPropertyOf(string sql) =>
        OwnedPropertySelect.IsMatch(sql.TrimStart()) ? Table(("PropertyId", typeof(Guid), PropertyId)) : null;

    /// <summary>The owner profile of the account: the read of the references of the published statements is sent with it.</summary>
    private static DataTable? OwnerProfileOf(string sql) =>
        OwnerProfileSelect.IsMatch(sql.TrimStart())
            ? Table(("OrgId", typeof(Guid), OrgId), ("PropertyOwnerId", typeof(Guid), OwnerId))
            : null;

    /// <summary>One lapsing version on the first page, none on the next: the reads that follow a page (documents, properties, items raised) are sent too.</summary>
    private static Func<string, DataTable?> FirstPageOfTheLapsingVersions()
    {
        var served = false;
        return sql =>
        {
            if (served || !LapsingVersionSelect.IsMatch(sql.TrimStart()))
                return null;

            served = true;
            return Table(
                ("Id", typeof(Guid), Guid.NewGuid()),
                ("OwnerDocumentId", typeof(Guid), Guid.NewGuid()),
                ("ExpiresOn", typeof(DateOnly), new DateOnly(2026, 10, 20)));
        };
    }

    /// <summary>What a call runs, how many statements it sends at least (all of them are reached when nothing was swallowed early) and the rows that let it go on.</summary>
    private sealed record Call(Func<AppDbContext, Task> Run, int MinimumStatements, Func<string, DataTable?>? Rows = null);

    private static readonly string[] CallNames =
    [
        // The calendar, the stays and the rules of the owner stays, and the background work on them (E07/F07/US01/T01).
        "owner-calendar",
        "owner-stays",
        "owner-stay-rule",
        "agency-stays",
        "agency-stay-rules",
        "stay-reconciliation",

        // The access resolved at every request, the documents, the contacts and the inbox (E07/F07/US01/T02).
        "access-resolver",
        "owner-documents",
        "owner-document-file",
        "agency-documents",
        "document-downloads",
        "owner-contacts",
        "agency-contacts",
        "owner-conversations",
        "owner-messages",
        "agency-conversations",
        "agency-messages",
        "inbox-preferences",
        "expiry-alerts",

        // The reads that existed before and were corrected to name the owner and the org: no tenant of the caller.
        "owner-properties",
        "owner-property-access",
        "owner-statement-documents",
    ];

    private static Call CallOf(string name) => name switch
    {
        // The bookings, the blocks and the stays of the property.
        "owner-calendar" => new(db => Stays(db).GetCalendarAsync(Access(), PropertyId, null, null), 3),
        "owner-stays" => new(db => Stays(db).ListForOwnerAsync(Access()), 1),

        // The rules of the property, then the nights held.
        "owner-stay-rule" => new(db => Stays(db).GetRuleForOwnerAsync(Access(), PropertyId), 2),
        "agency-stays" => new(db => Stays(db).ListForAgencyAsync(OrgId, OwnerStayStatus.Requested, PropertyId), 1),
        "agency-stay-rules" => new(db => Stays(db).ListRulesAsync(OrgId, PropertyId), 1),

        // The five groups of stays of a run: the requests, the approved, the withdrawn, the failed with no «To do» since, and the «To do» still open.
        "stay-reconciliation" => new(db => Stays(db).ReconcileAsync(), 5),

        // The ownerships of the account, its delegations, and the ownerships of the owners that gave them.
        "access-resolver" => new(db => new OwnerPortalAccessResolver(db, Clock).ResolveAsync(OwnerUserId), 3, DelegationOf),

        // The documents and the statements; one document that does not exist; the lists of the agency; the downloads of a document that does not exist.
        "owner-documents" => new(db => Collaboration(db).ListDocumentsForOwnerAsync(Access()), 2),
        "owner-document-file" => new(db => Collaboration(db).OpenDocumentForOwnerAsync(Access(), Guid.NewGuid(), null), 1),
        "agency-documents" => new(db => Collaboration(db).ListDocumentsForAgencyAsync(OrgId, OwnerId), 1),
        "document-downloads" => new(db => Collaboration(db).ListDocumentDownloadsAsync(OrgId, Guid.NewGuid()), 1),
        "owner-contacts" => new(db => Collaboration(db).ListContactsForOwnerAsync(Access()), 1),
        "agency-contacts" => new(db => Collaboration(db).ListContactsForAgencyAsync(OrgId, OwnerId), 1),
        "owner-conversations" => new(db => Collaboration(db).ListConversationsForOwnerAsync(Access()), 1),
        "owner-messages" => new(db => Collaboration(db).ListMessagesForOwnerAsync(Access(), Guid.NewGuid()), 1),
        "agency-conversations" => new(db => Collaboration(db).ListConversationsForAgencyAsync(OrgId, OwnerId), 1),
        "agency-messages" => new(db => Collaboration(db).ListMessagesForAgencyAsync(OrgId, Guid.NewGuid()), 1),
        "inbox-preferences" => new(db => Collaboration(db).GetInboxPreferencesAsync(OwnerUserId), 1),

        // The To do still open; the versions that lapse, a page at a time, with a «not exists» on the later versions in force; then, for the page, the documents, the
        // properties and the items already raised (the property is read only for a document that has one); then the next page, which ends the loop.
        "expiry-alerts" => new(db => Collaboration(db).RaiseExpiryAlertsAsync(), 5, FirstPageOfTheLapsingVersions()),

        // The ownerships of the owner, then its properties with the count, the sum and the ten latest bookings of each (the sub-queries built outside the projection).
        "owner-properties" => new(db => Portal(db).GetOwnerPropertiesAsync(OwnerUserId), 2, OwnedPropertyOf),
        "owner-property-access" => new(db => Portal(db).OwnerHasAccessToPropertyAsync(OwnerUserId, PropertyId), 1),

        // The owner profiles of the account, then the references of the published statements of those profiles (a «contains» on a sub-query).
        "owner-statement-documents" => new(db => Accounting(db).ListPublishedExternalDocumentsForOwnerUserAsync(OwnerUserId), 2, OwnerProfileOf),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "A call of the list that has no definition."),
    };

    public static TheoryData<string> EveryCall()
    {
        var data = new TheoryData<string>();
        foreach (var name in CallNames)
            data.Add(name);

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryCall))]
    public async Task Call_IsTranslatedToSqlByNpgsql(string name)
    {
        var call = CallOf(name);
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements, rows: call.Rows);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => call.Run(db));

        // The probe must have got as far as the last query of the call, or it proved nothing about the ones after the first.
        Assert.True(
            statements.Count >= call.MinimumStatements,
            $"{name}: only {statements.Count} of the expected {call.MinimumStatements} statements were reached:{Environment.NewLine}{string.Join(Environment.NewLine + "--" + Environment.NewLine, statements)}");
    }
}
