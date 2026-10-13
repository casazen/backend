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
        await using var db = NpgsqlTranslationProbe.NewContext(statements);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => Calls[call](db, ScopeOf(scope)));

        // The probe must have got as far as the last query of the call, or it proved nothing about the ones after the first.
        Assert.True(
            statements.Count >= MinimumStatements[call],
            $"{call}: only {statements.Count} of the expected {MinimumStatements[call]} statements were reached.");
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
