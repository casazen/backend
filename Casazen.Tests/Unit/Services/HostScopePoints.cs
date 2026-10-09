using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The assertions of AM-03 about the lists of the host, written once and run on both providers: by <c>HostScopeListsTests</c>
/// on EF InMemory and by <c>HostScopePostgresTests</c> on a real PostgreSQL database. Each point is a list that used to filter
/// by <c>scope.OwnerId</c> (the thirteen of TN-3: the repositories of bookings, leases, payments and properties twice; the three
/// fiscal reports; the dashboard three times; the requests waiting for the host; the interventions) or a list added with the
/// scope (the cockpit and the Alloggiati stays). Every point checks three things: the collaborator limited to some properties
/// sees only the one it was given (and sees something: an empty list would prove nothing), the whole org sees both, and nobody
/// sees the properties of the other org.
/// </summary>
internal static class HostScopePoints
{
    private static readonly TimeProvider Clock = new FixedTimeProvider(HostScopeScenario.Now);

    public static void OnlyTheGranted(HostScopeWorld world, IEnumerable<Guid> propertyIds, string point)
    {
        var shown = propertyIds.ToHashSet();
        Assert.True(shown.Count > 0, $"{point}: the restricted scope sees nothing, the test proves nothing");
        Assert.True(shown.SetEquals([world.Granted.Id]), $"{point}: the restricted scope sees {shown.Count} properties instead of the one it was given");
    }

    public static void TheWholeOrgButNotAnotherOne(HostScopeWorld world, IEnumerable<Guid> propertyIds, string point)
    {
        var shown = propertyIds.ToHashSet();
        Assert.True(shown.Contains(world.Granted.Id), $"{point}: the whole org does not see the first property");
        Assert.True(shown.Contains(world.Hidden.Id), $"{point}: the whole org does not see the second property");
        Assert.False(shown.Contains(world.OtherOrgProperty.Id), $"{point}: the property of another org is shown");
    }

    // --- 1-5 · the repositories ------------------------------------------------------------------------

    public static async Task BookingsAsync(AppDbContext db, HostScopeWorld world)
    {
        var repository = new BookingRepository(db);

        OnlyTheGranted(world, (await repository.GetByScopeAsync(world.Restricted)).Select(b => b.PropertyId), "bookings");
        TheWholeOrgButNotAnotherOne(world, (await repository.GetByScopeAsync(world.OrgWide)).Select(b => b.PropertyId), "bookings");
    }

    public static async Task LeasesAsync(AppDbContext db, HostScopeWorld world)
    {
        var repository = new LeaseContractRepository(db);

        OnlyTheGranted(world, (await repository.GetSummariesAsync(world.Restricted)).Select(l => l.PropertyId), "leases");
        TheWholeOrgButNotAnotherOne(world, (await repository.GetSummariesAsync(world.OrgWide)).Select(l => l.PropertyId), "leases");
    }

    public static async Task PaymentsAsync(AppDbContext db, HostScopeWorld world)
    {
        var repository = new PaymentRepository(db);

        OnlyTheGranted(world, (await repository.GetByScopeAsync(world.Restricted)).Select(p => p.Booking.PropertyId), "payments");
        TheWholeOrgButNotAnotherOne(world, (await repository.GetByScopeAsync(world.OrgWide)).Select(p => p.Booking.PropertyId), "payments");
    }

    public static async Task PropertiesAsync(AppDbContext db, HostScopeWorld world)
    {
        var repository = new PropertyRepository(db);

        OnlyTheGranted(world, (await repository.GetByScopeAsync(world.Restricted)).Select(p => p.Id), "properties");
        TheWholeOrgButNotAnotherOne(world, (await repository.GetByScopeAsync(world.OrgWide)).Select(p => p.Id), "properties");
    }

    public static async Task CinSummaryAsync(AppDbContext db, HostScopeWorld world)
    {
        var repository = new PropertyRepository(db);

        OnlyTheGranted(world, (await repository.GetByScopeForComplianceAsync(world.Restricted)).Select(p => p.Id), "CIN summary");
        TheWholeOrgButNotAnotherOne(world, (await repository.GetByScopeForComplianceAsync(world.OrgWide)).Select(p => p.Id), "CIN summary");
    }

    /// <summary>An account in no team keeps the old rule: the properties it created (here both of the org, not the other org).</summary>
    public static async Task AccountInNoTeamAsync(AppDbContext db, HostScopeWorld world)
    {
        var scope = world.OwnedByOwner;

        var properties = (await new PropertyRepository(db).GetByScopeAsync(scope)).Select(p => p.Id).ToList();
        Assert.Equal(2, properties.Count);
        TheWholeOrgButNotAnotherOne(world, (await new BookingRepository(db).GetByScopeAsync(scope)).Select(b => b.PropertyId), "bookings of the account in no team");
    }

    // --- 6-8 · the fiscal reports ----------------------------------------------------------------------

    internal static FiscalService Fiscal(AppDbContext db) =>
        new(db, new MigraDocPdfDocumentRenderer(), Options.Create(new ShortStayFiscalOptions()), Clock);

    public static async Task AnnualReportAsync(AppDbContext db, HostScopeWorld world)
    {
        var restricted = await Fiscal(db).GetAnnualReportAsync(world.Restricted, 2026);
        var orgWide = await Fiscal(db).GetAnnualReportAsync(world.OrgWide, 2026);

        OnlyTheGranted(world, restricted.Properties.Select(p => p.PropertyId), "annual report");
        TheWholeOrgButNotAnotherOne(world, orgWide.Properties.Select(p => p.PropertyId), "annual report");
        // What it earned is the granted property alone: half of the org (the two properties are alike).
        Assert.True(restricted.Totals.GrossIncome > 0, "annual report: the granted property earned nothing");
        Assert.Equal(orgWide.Totals.GrossIncome, restricted.Totals.GrossIncome * 2);
    }

    public static async Task TouristTaxReportAsync(AppDbContext db, HostScopeWorld world)
    {
        var period = new FiscalReportPeriod(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));

        var restricted = await Fiscal(db).GetTouristTaxReportAsync(world.Restricted, period);
        var orgWide = await Fiscal(db).GetTouristTaxReportAsync(world.OrgWide, period);

        OnlyTheGranted(world, restricted.Stays.Select(s => s.PropertyId), "tourist tax report");
        TheWholeOrgButNotAnotherOne(world, orgWide.Stays.Select(s => s.PropertyId), "tourist tax report");
    }

    public static async Task WithholdingReportAsync(AppDbContext db, HostScopeWorld world)
    {
        var restricted = await Fiscal(db).GetWithholdingReportAsync(world.Restricted, 2026);
        var orgWide = await Fiscal(db).GetWithholdingReportAsync(world.OrgWide, 2026);

        OnlyTheGranted(world, restricted.Lines.Select(l => l.PropertyId), "withholding report");
        TheWholeOrgButNotAnotherOne(world, orgWide.Lines.Select(l => l.PropertyId), "withholding report");
        Assert.Equal(orgWide.Totals.Withholding, restricted.Totals.Withholding * 2);
    }

    // --- 9-11 · the dashboard --------------------------------------------------------------------------

    internal static HostDashboardService Dashboard(AppDbContext db) => new(db, new ConfigurationBuilder().Build(), Clock);

    public static async Task DashboardPropertiesAsync(AppDbContext db, HostScopeWorld world)
    {
        var restricted = await Dashboard(db).GetKpisAsync(world.Restricted, HostDashboardPeriodKind.Month, null);
        var orgWide = await Dashboard(db).GetKpisAsync(world.OrgWide, HostDashboardPeriodKind.Month, null);

        Assert.Equal(1, restricted.PropertyCount);
        Assert.Equal(2, orgWide.PropertyCount);
        // The nights available are those of the properties in scope.
        Assert.Equal(orgWide.Occupancy.AvailableNights, restricted.Occupancy.AvailableNights * 2);
    }

    public static async Task DashboardBookingsAsync(AppDbContext db, HostScopeWorld world)
    {
        var restricted = await Dashboard(db).GetKpisAsync(world.Restricted, HostDashboardPeriodKind.Month, null);
        var orgWide = await Dashboard(db).GetKpisAsync(world.OrgWide, HostDashboardPeriodKind.Month, null);

        OnlyTheGranted(world, restricted.UpcomingCheckIns.Items.Select(s => s.PropertyId), "upcoming check-ins");
        OnlyTheGranted(world, restricted.RecentBookings.Select(s => s.PropertyId), "recent bookings");
        TheWholeOrgButNotAnotherOne(world, orgWide.RecentBookings.Select(s => s.PropertyId), "recent bookings");
        Assert.True(restricted.Revenue > 0, "dashboard: the granted property earned nothing");
        Assert.Equal(orgWide.Revenue, restricted.Revenue * 2);
        Assert.Equal(1, restricted.UpcomingCheckIns.Count);
    }

    public static async Task DashboardIcalFeedsAsync(AppDbContext db, HostScopeWorld world)
    {
        var restricted = await Dashboard(db).GetIcalFeedsAsync(world.Restricted);
        var orgWide = await Dashboard(db).GetIcalFeedsAsync(world.OrgWide);

        OnlyTheGranted(world, restricted.Select(f => f.PropertyId), "iCal feeds");
        TheWholeOrgButNotAnotherOne(world, orgWide.Select(f => f.PropertyId), "iCal feeds");
    }

    // --- 12 · the requests waiting for the host --------------------------------------------------------

    public static async Task AwaitingHostApprovalAsync(AppDbContext db, HostScopeWorld world)
    {
        var service = new OnSiteBookingRequestService(
            db, null!, null!, null!, new ConfigurationBuilder().Build(), NullLogger<OnSiteBookingRequestService>.Instance, Clock);

        var restricted = await service.GetAwaitingHostApprovalAsync(world.Restricted);
        var orgWide = await service.GetAwaitingHostApprovalAsync(world.OrgWide);

        OnlyTheGranted(world, restricted.Select(b => b.PropertyId), "requests waiting for the host");
        TheWholeOrgButNotAnotherOne(world, orgWide.Select(b => b.PropertyId), "requests waiting for the host");
    }

    // --- 13 · the interventions of the host ------------------------------------------------------------

    private static ServiceRequestService ServiceRequests(AppDbContext db) => new(
        db,
        new ServiceRequestRepository(db),
        new RecordingEmailQueue(),
        EmailTestHelpers.Links(),
        Mock.Of<IPushNotificationService>(),
        ComuneTestServices.Matcher(db),
        LegalTestServices.Legal(),
        NullLogger<ServiceRequestService>.Instance);

    public static async Task InterventionsAsync(AppDbContext db, HostScopeWorld world)
    {
        var service = ServiceRequests(db);

        var (restricted, restrictedTotal) = await service.ListForHostAsync(
            world.Restricted, ServiceRequestRentalContext.ShortRent, null, null, null, 1, 50);
        var (orgWide, orgWideTotal) = await service.ListForHostAsync(
            world.OrgWide, ServiceRequestRentalContext.ShortRent, null, null, null, 1, 50);

        OnlyTheGranted(world, restricted.Select(r => r.PropertyId), "interventions");
        Assert.Equal(1, restrictedTotal);
        TheWholeOrgButNotAnotherOne(world, orgWide.Select(r => r.PropertyId), "interventions");
        Assert.Equal(2, orgWideTotal);

        // The single read follows the same rule: the request of a hidden property is not found.
        var hidden = orgWide.Single(r => r.PropertyId == world.Hidden.Id);
        var granted = orgWide.Single(r => r.PropertyId == world.Granted.Id);
        Assert.Null(await service.GetByIdForHostAsync(hidden.Id, world.Restricted, ServiceRequestRentalContext.ShortRent));
        Assert.NotNull(await service.GetByIdForHostAsync(granted.Id, world.Restricted, ServiceRequestRentalContext.ShortRent));
        Assert.NotNull(await service.GetByIdForHostAsync(hidden.Id, world.OrgWide, ServiceRequestRentalContext.ShortRent));
    }

    public static async Task InterventionsFilteredOnAHiddenPropertyAsync(AppDbContext db, HostScopeWorld world)
    {
        var (items, total) = await ServiceRequests(db).ListForHostAsync(
            world.Restricted, ServiceRequestRentalContext.ShortRent, null, world.Hidden.Id, null, 1, 50);

        Assert.Empty(items);
        Assert.Equal(0, total);
    }

    // --- the lists added with the scope: the Alloggiati stays ------------------------------------------

    public static async Task AlloggiatiListAsync(AppDbContext db, HostScopeWorld world)
    {
        var service = new AlloggiatiWebService(db, NullLogger<AlloggiatiWebService>.Instance, Clock);

        var restricted = await service.GetSummaryAsync(world.Restricted, null);
        var orgWide = await service.GetSummaryAsync(world.OrgWide, null);
        var hiddenOnly = await service.GetSummaryAsync(world.Restricted, world.Hidden.Id);

        Assert.NotEmpty(restricted);
        Assert.All(restricted, row => Assert.Equal(world.Granted.Name, row.PropertyName));
        Assert.Contains(orgWide, row => row.PropertyName == world.Hidden.Name);
        Assert.DoesNotContain(orgWide, row => row.PropertyName == world.OtherOrgProperty.Name);
        Assert.Empty(hiddenOnly);
    }

    /// <summary>Every point, by name, for the run that reports all the failures together (PostgreSQL in CI).</summary>
    public static IReadOnlyList<(string Name, Func<AppDbContext, HostScopeWorld, Task> Run)> All { get; } =
    [
        ("01 bookings", BookingsAsync),
        ("02 leases", LeasesAsync),
        ("03 payments", PaymentsAsync),
        ("04 properties", PropertiesAsync),
        ("05 CIN summary", CinSummaryAsync),
        ("01-05 account in no team", AccountInNoTeamAsync),
        ("06 annual report", AnnualReportAsync),
        ("07 tourist tax report", TouristTaxReportAsync),
        ("08 withholding report", WithholdingReportAsync),
        ("09 dashboard properties", DashboardPropertiesAsync),
        ("10 dashboard bookings", DashboardBookingsAsync),
        ("11 dashboard iCal feeds", DashboardIcalFeedsAsync),
        ("12 requests waiting for the host", AwaitingHostApprovalAsync),
        ("13 interventions", InterventionsAsync),
        ("13 interventions filtered on a hidden property", InterventionsFilteredOnAHiddenPropertyAsync),
        ("Alloggiati list", AlloggiatiListAsync),
    ];
}
