using System.Data;
using System.Text.RegularExpressions;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Services.ApprovalActions;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// AM-03: every list of the host that takes a scope, run on the PostgreSQL provider with all three kinds of scope (the
/// collaborator limited to some properties, the account in no team, the whole org), without a server (see
/// <see cref="NpgsqlTranslationProbe"/>). InMemory evaluates a query the way .NET does and accepts what Npgsql cannot turn
/// into SQL; this fails here, before CI, when the <c>EXISTS</c> on the grants is combined with a shape that has no translation.
/// </summary>
public class HostScopeNpgsqlTranslationTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly TimeProvider Clock = new FixedTimeProvider(HostScopeScenario.Now);

    public static TheoryData<string> Scopes => ["collaborator", "account-in-no-team", "whole-org"];

    private static HostScope ScopeOf(string kind) => kind switch
    {
        "collaborator" => new HostScope(OrgId, GrantedToUserId: "auth0|collaboratore"),
        "account-in-no-team" => new HostScope(OrgId, OwnerId: "auth0|titolare"),
        _ => new HostScope(OrgId),
    };

    private static FiscalService Fiscal(AppDbContext db) =>
        new(db, new MigraDocPdfDocumentRenderer(), Options.Create(new ShortStayFiscalOptions()), Clock);

    private static HostDashboardService Dashboard(AppDbContext db) =>
        new(db, new ConfigurationBuilder().Build(), Clock);

    // The service as the API builds it (SP-04 kit, over the database of the test): the listings below write no file.
    private static ServiceRequestService ServiceRequests(AppDbContext db) => new ServiceRequestTestKit(db).Service;

    // The approval queue as the API builds it, with no action and no alert: the reads of the list and of one item are what is probed (the alert is not reached).
    private static ApprovalQueueService ApprovalQueue(AppDbContext db) =>
        new(
            db,
            Clock,
            new ApprovalOriginActionRegistry(() => []),
            new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, Clock),
            null!,
            NullLogger<ApprovalQueueService>.Instance);

    // The CRM profiles (E07/F01/US03): the read model, the list, the detail, and the decisions on the merges.
    private static CrmProfileReadService CrmRead(AppDbContext db) => CrmProfileApiWorld.ReadService(db, Clock);

    private static CrmProfileMergeService CrmMerge(AppDbContext db) => CrmProfileApiWorld.MergeService(db, Clock);

    private static Task<CrmProfilePage> CrmList(AppDbContext db, HostScope scope, CrmProfileFilter filter) => CrmRead(db).ListAsync(scope, filter, 1, 20);

    /// <summary>Every filter of the list at once, and the order by name, which sorts by a sub-query: the filters must translate together too.</summary>
    private static readonly CrmProfileFilter EveryFilter = new("anna ross", "Italia", "it", "Direct", "vip", 2, true, CrmProfileSort.Name);

    private static readonly Regex ProfileSelect = new(@"^SELECT\s+\w+\.""Id"",\s*\w+\.""Country"",\s*\w+\.""Language"",\s*\w+\.""TotalStays""", RegexOptions.Compiled);

    /// <summary>
    /// The row of a CRM profile, for the statement that selects it (id, country, language, stays, spend, first and last stay, tags, primary
    /// guest) in the detail and in the items of a page: with that row the probe goes on to the queries that need the profile.
    /// </summary>
    private static DataTable? ProfileRowOf(string sql)
    {
        if (!ProfileSelect.IsMatch(sql.TrimStart()))
            return null;

        var table = new DataTable();
        table.Columns.Add("Id", typeof(Guid));
        table.Columns.Add("Country", typeof(string));
        table.Columns.Add("Language", typeof(string));
        table.Columns.Add("TotalStays", typeof(int));
        table.Columns.Add("TotalSpend", typeof(decimal));
        table.Columns.Add("FirstStayAt", typeof(DateTime));
        table.Columns.Add("LastStayAt", typeof(DateTime));
        table.Columns.Add("TagsJson", typeof(string));
        table.Columns.Add("PrimaryGuestId", typeof(Guid));
        table.Rows.Add(Guid.NewGuid(), "Italia", "it", 1, 200m, HostScopeScenario.Day(2026, 9, 1), HostScopeScenario.Day(2026, 9, 3), "[]", DBNull.Value);
        return table;
    }

    /// <summary>What the probe answers for a call that cannot go on with an empty answer (see <see cref="AnswerFor"/>).</summary>
    private sealed record ProbeAnswer(int Count = 0, Func<string, DataTable?>? Rows = null);

    /// <summary>
    /// The CRM list and the merge proposals ask for a count and send the page behind it only when the count is not zero, so the probe answers
    /// the count with one (the order of the page is the part that needs the provider); the detail and the items of a page need the row of the
    /// profile to send the queries that follow it. Every other call goes on with the default answer.
    /// </summary>
    private static ProbeAnswer AnswerFor(string call) => call switch
    {
        "crm-detail" or "crm-items-of-a-page" => new ProbeAnswer(Rows: ProfileRowOf),
        "crm-merge-candidates" => new ProbeAnswer(Count: 1),
        _ when call.StartsWith("crm-list-", StringComparison.Ordinal) => new ProbeAnswer(Count: 1),
        _ => new ProbeAnswer(),
    };

    /// <summary>Every call of the code that takes the scope of the caller, by the name the failure reports.</summary>
    private static readonly Dictionary<string, Func<AppDbContext, HostScope, Task>> Calls = new()
    {
        ["bookings"] = (db, scope) => new BookingRepository(db).GetByScopeAsync(scope),
        ["leases"] = (db, scope) => new LeaseContractRepository(db).GetSummariesAsync(scope),
        ["payments"] = (db, scope) => new PaymentRepository(db).GetByScopeAsync(scope),
        ["properties"] = (db, scope) => new PropertyRepository(db).GetByScopeAsync(scope),
        ["cin-summary"] = (db, scope) => new PropertyRepository(db).GetByScopeForComplianceAsync(scope),
        ["fiscal-annual"] = (db, scope) => Fiscal(db).GetAnnualReportAsync(scope, 2026),
        ["fiscal-tourist-tax"] = (db, scope) =>
            Fiscal(db).GetTouristTaxReportAsync(scope, new FiscalReportPeriod(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31))),
        ["fiscal-withholding"] = (db, scope) => Fiscal(db).GetWithholdingReportAsync(scope, 2026),
        ["dashboard-kpis-month"] = (db, scope) => Dashboard(db).GetKpisAsync(scope, HostDashboardPeriodKind.Month, null),
        ["dashboard-kpis-30-days"] = (db, scope) => Dashboard(db).GetKpisAsync(scope, HostDashboardPeriodKind.Last30Days, null),
        ["dashboard-ical-feeds"] = (db, scope) => Dashboard(db).GetIcalFeedsAsync(scope),
        ["on-site-requests"] = (db, scope) =>
            new OnSiteBookingRequestService(db, null!, null!, null!, new ConfigurationBuilder().Build(), NullLogger<OnSiteBookingRequestService>.Instance, Clock)
                .GetAwaitingHostApprovalAsync(scope),
        ["interventions"] = (db, scope) =>
            ServiceRequests(db).ListForHostAsync(scope, ServiceRequestRentalContext.ShortRent, null, null, null, 1, 20),
        ["intervention-by-id"] = (db, scope) =>
            ServiceRequests(db).GetByIdForHostAsync(Guid.NewGuid(), scope, ServiceRequestRentalContext.ShortRent),
        ["cockpit"] = (db, scope) => ComplianceWizardServiceTests.CreateService(db, Clock).GetSummaryAsync(scope),
        ["alloggiati-list"] = (db, scope) =>
            new AlloggiatiWebService(db, NullLogger<AlloggiatiWebService>.Instance, Clock).GetSummaryAsync(scope, null),

        // The approval queue (E05/F04/US01/T02): the list, and the read of one item that the edit, the approval and the rejection all start with (the item of a property
        // the scope reaches is one IN (SELECT ...) on the properties of the scope, in the same statement).
        ["approval-queue-list"] = (db, scope) => ApprovalQueue(db).GetPendingAsync(scope),
        ["approval-queue-item"] = (db, scope) => ApprovalQueue(db).EditAsync(scope, Guid.NewGuid(), "Un testo", "auth0|utente"),

        // The CRM profiles (E07/F01/US03): every order of the list, every filter and the three searches, the detail, the items of a page and
        // the merge proposals, for every kind of scope.
        ["crm-list-by-last-stay"] = (db, scope) => CrmList(db, scope, new CrmProfileFilter(Sort: CrmProfileSort.LastStay)),
        ["crm-list-by-spend"] = (db, scope) => CrmList(db, scope, new CrmProfileFilter(Sort: CrmProfileSort.Spend)),
        ["crm-list-by-stays"] = (db, scope) => CrmList(db, scope, new CrmProfileFilter(Sort: CrmProfileSort.Stays)),
        ["crm-list-by-name"] = (db, scope) => CrmList(db, scope, new CrmProfileFilter(Sort: CrmProfileSort.Name)),
        ["crm-list-country-language-tag"] = (db, scope) => CrmList(db, scope, new CrmProfileFilter(Country: "Italia", Language: "it", Tag: "vip")),
        ["crm-list-channel-min-stays"] = (db, scope) => CrmList(db, scope, new CrmProfileFilter(Channel: "Direct", MinStays: 2)),
        ["crm-list-reachable-by-email"] = (db, scope) => CrmList(db, scope, new CrmProfileFilter(ReachableByEmail: true)),
        ["crm-list-not-reachable-by-email"] = (db, scope) => CrmList(db, scope, new CrmProfileFilter(ReachableByEmail: false)),
        ["crm-list-search-name"] = (db, scope) => CrmList(db, scope, new CrmProfileFilter(Search: "anna ross")),
        ["crm-list-search-email"] = (db, scope) => CrmList(db, scope, new CrmProfileFilter(Search: "anna.rossi@example.com")),
        ["crm-list-search-phone"] = (db, scope) => CrmList(db, scope, new CrmProfileFilter(Search: "+39 333 1234567")),
        ["crm-list-everything-at-once"] = (db, scope) => CrmList(db, scope, EveryFilter),
        ["crm-detail"] = (db, scope) => CrmRead(db).GetAsync(scope, Guid.NewGuid()),
        ["crm-items-of-a-page"] = (db, scope) => CrmRead(db).GetItemsAsync(scope, [Guid.NewGuid()]),
        ["crm-merge-candidates"] = (db, scope) => CrmMerge(db).ListProposedAsync(scope, 1, 20),
    };

    /// <summary>How many statements each call sends at least (all of them are reached when nothing was swallowed early).</summary>
    private static readonly Dictionary<string, int> MinimumStatements = new()
    {
        ["bookings"] = 1,
        ["leases"] = 1,
        ["payments"] = 1,
        ["properties"] = 1,
        ["cin-summary"] = 1,
        ["fiscal-annual"] = 5,
        ["fiscal-tourist-tax"] = 2,
        ["fiscal-withholding"] = 2,
        ["dashboard-kpis-month"] = 5,
        ["dashboard-kpis-30-days"] = 5,
        ["dashboard-ical-feeds"] = 1,
        ["on-site-requests"] = 1,
        ["interventions"] = 2,
        ["intervention-by-id"] = 1,
        ["cockpit"] = 6,
        ["alloggiati-list"] = 3,
        ["approval-queue-list"] = 1,
        ["approval-queue-item"] = 1,

        // The CRM: the count and the page behind it (the probe answers the count with one); a search by an exact e-mail address asks for the
        // contacts first, one by a phone number for the contacts and the profiles that carry the number.
        ["crm-list-by-last-stay"] = 2,
        ["crm-list-by-spend"] = 2,
        ["crm-list-by-stays"] = 2,
        ["crm-list-by-name"] = 2,
        ["crm-list-country-language-tag"] = 2,
        ["crm-list-channel-min-stays"] = 2,
        ["crm-list-reachable-by-email"] = 2,
        ["crm-list-not-reachable-by-email"] = 2,
        ["crm-list-search-name"] = 2,
        ["crm-list-search-email"] = 3,
        ["crm-list-search-phone"] = 4,
        ["crm-list-everything-at-once"] = 2,
        // The row of the profile, then the guests that give the name, the contacts, the stays and the proposals (the reviews are asked for
        // only when there is a stay).
        ["crm-detail"] = 5,
        // The row of the profile, then the guests that give the name, the bookings, the contacts and the proposals.
        ["crm-items-of-a-page"] = 5,
        // The count and the page of the proposals; the properties and the contacts of the page come after.
        ["crm-merge-candidates"] = 2,
    };

    public static TheoryData<string, string> EveryCallWithEveryScope()
    {
        var data = new TheoryData<string, string>();
        foreach (var call in Calls.Keys)
        {
            foreach (var scope in new[] { "collaborator", "account-in-no-team", "whole-org" })
                data.Add(call, scope);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryCallWithEveryScope))]
    public async Task Call_IsTranslatedToSqlByNpgsql(string call, string scope)
    {
        var statements = new List<string>();
        var answer = AnswerFor(call);
        await using var db = NpgsqlTranslationProbe.NewContext(statements, answer.Count, answer.Rows);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => Calls[call](db, ScopeOf(scope)));

        // The probe must have got as far as the last query of the call, or it proved nothing about the ones after the first.
        Assert.True(
            statements.Count >= MinimumStatements[call],
            $"{call}: only {statements.Count} of the expected {MinimumStatements[call]} statements were reached.");
    }

    [Fact]
    public void EveryCall_HasItsMinimum()
    {
        Assert.Equal(Calls.Keys.Order(), MinimumStatements.Keys.Order());
    }

    [Fact]
    public async Task Probe_CatchesAQueryThatNpgsqlCannotTranslate()
    {
        // The probe must be able to fail: a method call EF has no SQL for is a translation failure, not an empty result.
        await using var db = NpgsqlTranslationProbe.NewContext();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => NpgsqlTranslationProbe.AssertTranslatesAsync(
            () => Task.FromResult(db.Properties.Where(p => Untranslatable(p.Name)).ToList())));
    }

    private static bool Untranslatable(string name) => name.GetHashCode() % 2 == 0;
}
