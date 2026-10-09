using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-03: the lists of the host show a collaborator "Solo alcuni" only the properties it was given, at each of the points that
/// used to filter by <c>scope.OwnerId</c> and in the lists added with the scope (see <see cref="HostScopePoints"/>), here on
/// EF InMemory. The same assertions run on PostgreSQL in <c>HostScopePostgresTests</c>. Every point also checks that the
/// org-wide scope of the owner sees both properties and that nobody sees the other org.
/// </summary>
public class HostScopeListsTests : IAsyncLifetime
{
    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private HostScopeWorld _world = null!;

    public async Task InitializeAsync() => _world = await HostScopeScenario.SeedAsync(_db);

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public Task Point01_Bookings_TheCollaboratorSeesOnlyTheBookingsOfItsProperties() => HostScopePoints.BookingsAsync(_db, _world);

    [Fact]
    public Task Point02_Leases_TheCollaboratorSeesOnlyTheLeasesOfItsProperties() => HostScopePoints.LeasesAsync(_db, _world);

    [Fact]
    public Task Point03_Payments_TheScopeSeesOnlyThePaymentsOfItsProperties() => HostScopePoints.PaymentsAsync(_db, _world);

    [Fact]
    public Task Point04_Properties_TheCollaboratorSeesOnlyItsProperties() => HostScopePoints.PropertiesAsync(_db, _world);

    [Fact]
    public Task Point05_PropertiesOfTheCinSummary_TheCollaboratorSeesOnlyItsProperties() => HostScopePoints.CinSummaryAsync(_db, _world);

    [Fact]
    public Task Point01to05_AnAccountInNoTeamKeepsTheOldRule_ThePropertiesItCreated() => HostScopePoints.AccountInNoTeamAsync(_db, _world);

    [Fact]
    public Task Point06_AnnualReport_TheLinesAndTheIncomeAreOnlyThoseOfTheGrantedProperties() => HostScopePoints.AnnualReportAsync(_db, _world);

    [Fact]
    public Task Point07_TouristTaxReport_TheStaysAreOnlyThoseOfTheGrantedProperties() => HostScopePoints.TouristTaxReportAsync(_db, _world);

    [Fact]
    public Task Point08_WithholdingReport_ThePaymentsAreOnlyThoseOfTheGrantedProperties() => HostScopePoints.WithholdingReportAsync(_db, _world);

    [Fact]
    public Task Point09_DashboardProperties_TheOccupancyCountsOnlyTheGrantedProperties() => HostScopePoints.DashboardPropertiesAsync(_db, _world);

    [Fact]
    public Task Point10_DashboardBookings_ArrivalsRevenueAndActivityAreOnlyTheGrantedPropertys() => HostScopePoints.DashboardBookingsAsync(_db, _world);

    [Fact]
    public Task Point11_DashboardIcalFeeds_OnlyTheFeedsOfTheGrantedProperties() => HostScopePoints.DashboardIcalFeedsAsync(_db, _world);

    [Fact]
    public Task Point12_RequestsWaitingForTheHost_OnlyThoseOfTheGrantedProperties() => HostScopePoints.AwaitingHostApprovalAsync(_db, _world);

    [Fact]
    public Task Point13_Interventions_TheListAndTheSingleReadAreOnlyThoseOfTheGrantedProperties() => HostScopePoints.InterventionsAsync(_db, _world);

    [Fact]
    public Task Point13_Interventions_AFilterOnAHiddenPropertyFindsNothing() => HostScopePoints.InterventionsFilteredOnAHiddenPropertyAsync(_db, _world);

    [Fact]
    public Task AlloggiatiList_TheCollaboratorSeesOnlyTheStaysOfItsProperties() => HostScopePoints.AlloggiatiListAsync(_db, _world);

    [Fact]
    public async Task EveryPoint_IsRunByTheCommonList()
    {
        // The list the PostgreSQL run walks must not lose a point: one fact above for each of its entries.
        Assert.Equal(16, HostScopePoints.All.Count);
        foreach (var (name, run) in HostScopePoints.All)
        {
            await using var db = HostScopeScenario.NewInMemoryDb();
            var world = await HostScopeScenario.SeedAsync(db);
            await run(db, world);
            Assert.False(string.IsNullOrWhiteSpace(name));
        }
    }

    [Fact]
    public async Task Scenario_EveryForeignKeyOfTheSeededData_PointsToARowOfTheStore()
    {
        // The same data is seeded on PostgreSQL by HostScopePostgresTests, which enforces the foreign keys this provider does
        // not: a dangling reference here would only show up in CI.
        await HostScopeScenario.AssertReferentialIntegrityAsync(_db);
    }

    // --- the entity-level extension, for the types the points above do not all reach -------------------

    [Fact]
    public void InScope_TheOrgWideScopeAddsNothing()
    {
        var plain = _db.Bookings.Where(b => b.OrgId == _world.OrgId);

        Assert.Same(plain, plain.InScope(_world.OrgWide));
    }

    [Fact]
    public void InScope_AServiceRequestWithNoPropertyIsNeverInARestrictedScope()
    {
        _db.ServiceRequests.Add(new ServiceRequest
        {
            OrgId = _world.OrgId,
            PropertyId = Guid.Empty,
            SupplierOrgId = _world.SupplierOrgId,
            Category = "cleaning",
        });
        _db.SaveChanges();

        var ids = _db.ServiceRequests.Where(r => r.OrgId == _world.OrgId).InScope(_world.Restricted).Select(r => r.PropertyId).ToList();

        Assert.All(ids, id => Assert.Equal(_world.Granted.Id, id));
    }
}
