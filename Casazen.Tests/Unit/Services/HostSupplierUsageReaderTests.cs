using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.DTOs.ServiceRequests;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// MKT (SR-09c): «Le mie app» lists the suppliers the host already asked for a job, derived from its short-rent requests. The rule that
/// matters is the perimeter: only the requests of the caller's org and, for a collaborator «Solo alcuni», only those of the properties it
/// was given; nothing of the other rental contexts; and nothing about a supplier but its name and the counts (no contact).
/// On EF InMemory, on the data every scope test runs on (<see cref="HostScopeScenario"/>).
/// </summary>
public class HostSupplierUsageReaderTests : IAsyncLifetime
{
    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private HostScopeWorld _world = null!;

    public async Task InitializeAsync() => _world = await HostScopeScenario.SeedAsync(_db);

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private HostSupplierUsageReader Reader() => new(_db);

    [Fact]
    public async Task ListAsync_TheWholeOrg_CountsItsOwnRequestsAndOnlyThose()
    {
        // Each of the three properties (two of the org, one of the other org) has one open request to the same supplier.
        var supplier = Assert.Single(await Reader().ListAsync(_world.OrgWide));

        Assert.Equal(_world.SupplierOrgId, supplier.SupplierOrgId);
        Assert.Equal(2, supplier.RequestsCount);
        Assert.Equal(2, supplier.OpenCount);
        Assert.Equal("cleaning", supplier.LastCategory);
        Assert.Equal("Fornitore", supplier.SupplierName);

        // The other org sees its own request alone.
        var other = Assert.Single(await Reader().ListAsync(new HostScope(_world.OtherOrgId)));
        Assert.Equal(1, other.RequestsCount);
    }

    [Fact]
    public async Task ListAsync_ACollaboratorSoloAlcuni_CountsOnlyTheRequestsOfItsProperties()
    {
        var supplier = Assert.Single(await Reader().ListAsync(_world.Restricted));
        Assert.Equal(1, supplier.RequestsCount);

        // A supplier asked only through a property the collaborator does not reach is not there at all.
        var other = HostScopeScenario.NewOrg("Altro fornitore", $"altro-forn-{Guid.NewGuid():N}"[..20], OrgType.Supplier);
        _db.Orgs.Add(other);
        _db.ServiceRequests.Add(Request(_world.Hidden.Id, other.Id));
        await _db.SaveChangesAsync();

        var restricted = await Reader().ListAsync(_world.Restricted);
        Assert.DoesNotContain(restricted, s => s.SupplierOrgId == other.Id);
        Assert.Contains(await Reader().ListAsync(_world.OrgWide), s => s.SupplierOrgId == other.Id);
    }

    [Fact]
    public async Task ListAsync_OnlyShortRentRequests_AndTheOpenOnesAreThoseNotOver()
    {
        // A request of the long-rent context is not one of Affitti brevi, and a paid one counts but is not open.
        var longRentOnly = HostScopeScenario.NewOrg("Solo lungo", $"solo-lungo-{Guid.NewGuid():N}"[..20], OrgType.Supplier);
        _db.Orgs.Add(longRentOnly);
        _db.ServiceRequests.Add(Request(_world.Granted.Id, longRentOnly.Id, rentalContext: ServiceRequestRentalContext.LongRent));
        _db.ServiceRequests.Add(Request(_world.Granted.Id, _world.SupplierOrgId, status: ServiceRequestStatus.Pagato, category: "plumbing", createdAt: DateTime.UtcNow.AddDays(1)));
        await _db.SaveChangesAsync();

        var suppliers = await Reader().ListAsync(_world.OrgWide);

        Assert.DoesNotContain(suppliers, s => s.SupplierOrgId == longRentOnly.Id);
        var used = Assert.Single(suppliers);
        Assert.Equal(3, used.RequestsCount);
        Assert.Equal(2, used.OpenCount);
        // The latest request is the paid one: its category is the one told.
        Assert.Equal("plumbing", used.LastCategory);
    }

    [Fact]
    public void UsedSupplierDto_HasTheNameAndTheCountsAndNoContact()
    {
        var fields = typeof(UsedSupplierDto).GetProperties().Select(p => p.Name).Order().ToArray();

        Assert.Equal(
            new[] { "LastCategory", "LastRequestAt", "OpenCount", "RequestsCount", "SupplierName", "SupplierOrgId" },
            fields);
    }

    private ServiceRequest Request(
        Guid propertyId,
        Guid supplierOrgId,
        ServiceRequestRentalContext rentalContext = ServiceRequestRentalContext.ShortRent,
        ServiceRequestStatus status = ServiceRequestStatus.Richiesto,
        string category = "cleaning",
        DateTime? createdAt = null) => new()
        {
            OrgId = _world.OrgId,
            PropertyId = propertyId,
            SupplierOrgId = supplierOrgId,
            RentalContext = rentalContext,
            Category = category,
            Status = status,
            CreatedAt = createdAt ?? DateTime.UtcNow,
        };
}
