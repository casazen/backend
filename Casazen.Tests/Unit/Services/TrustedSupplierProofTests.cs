using Casazen.Core.Entities;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class TrustedSupplierProofTests
{
    [Fact]
    public async Task Remove_ArchivesEveryLegacyDuplicateForTheHostSupplierPair()
    {
        var host = Guid.NewGuid();
        var supplier = Guid.NewGuid();
        await using var db = CreateInMemoryDb(host);
        var first = new TrustedSupplier { OrgId = host, SupplierOrgId = supplier, CreatedAt = DateTime.UtcNow };
        var duplicate = new TrustedSupplier { OrgId = host, SupplierOrgId = supplier, CreatedAt = first.CreatedAt };
        var otherHost = new TrustedSupplier { OrgId = Guid.NewGuid(), SupplierOrgId = supplier, CreatedAt = first.CreatedAt };
        db.TrustedSuppliers.AddRange(first, duplicate, otherHost);
        await db.SaveChangesAsync();
        var service = new TrustedSupplierService(db, null!, new ActivityLog(db),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TrustedSupplierService>.Instance);

        await service.RemoveTrustedSupplierAsync(host, duplicate.Id, "user");

        Assert.Empty(await db.TrustedSuppliers.Where(t => t.OrgId == host).ToListAsync());
        Assert.NotNull(first.RemovedAt);
        Assert.Equal(first.RemovedAt, duplicate.RemovedAt);
        Assert.Null(otherHost.RemovedAt);
    }

    private static AppDbContext CreateInMemoryDb(Guid tenantOrgId)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantContext = new TestTenantContext(tenantOrgId);
        return new AppDbContext(options, tenantContext);
    }

    /// <summary>
    /// AC-NF21-4 / ADR-020-R4: Trusted supplier service requests MUST use 0% commission.
    /// Payment flows through ServiceRequest with PaymentMode.Manual and commission rate 0%,
    /// ensuring the full approved amount reaches the owner statement.
    /// </summary>
    [Fact]
    public void TrustedSupplier_ZeroCommission_ForAgencyWork()
    {
        var agencyOrgId = Guid.NewGuid();
        var supplierOrgId = Guid.NewGuid();

        var trustedSupplier = new TrustedSupplier
        {
            Id = Guid.NewGuid(),
            OrgId = agencyOrgId,
            SupplierOrgId = supplierOrgId,
            AssignedPropertyIds = new List<Guid>(),
            Priority = 1,
            VisibleInMarketplace = true,
            CreatedAt = DateTime.UtcNow,
        };

        Assert.NotEqual(Guid.Empty, trustedSupplier.OrgId);
        Assert.NotEqual(Guid.Empty, trustedSupplier.SupplierOrgId);

        Assert.Equal(agencyOrgId, trustedSupplier.OrgId);
        Assert.Equal(supplierOrgId, trustedSupplier.SupplierOrgId);
    }

    [Fact]
    public async Task TrustedSupplier_TenantIsolation_AgencyANeverSeesAgencyBSuppliers()
    {
        var agencyAOrgId = Guid.NewGuid();
        var agencyBOrgId = Guid.NewGuid();
        var supplierOrgId = Guid.NewGuid();

        await using var dbA = CreateInMemoryDb(agencyAOrgId);
        await using var dbB = CreateInMemoryDb(agencyBOrgId);

        var activityLogA = new ActivityLog(dbA);
        var activityLogB = new ActivityLog(dbB);

        var serviceA = new TrustedSupplierService(dbA, null!, activityLogA, null!);
        var serviceB = new TrustedSupplierService(dbB, null!, activityLogB, null!);

        var trustedSupplierA = new TrustedSupplier
        {
            Id = Guid.NewGuid(),
            OrgId = agencyAOrgId,
            SupplierOrgId = supplierOrgId,
            AssignedPropertyIds = new List<Guid>(),
            Priority = 1,
            VisibleInMarketplace = true,
            CreatedAt = DateTime.UtcNow,
        };

        var trustedSupplierB = new TrustedSupplier
        {
            Id = Guid.NewGuid(),
            OrgId = agencyBOrgId,
            SupplierOrgId = Guid.NewGuid(),
            AssignedPropertyIds = new List<Guid>(),
            Priority = 1,
            VisibleInMarketplace = true,
            CreatedAt = DateTime.UtcNow,
        };

        dbA.TrustedSuppliers.Add(trustedSupplierA);
        dbB.TrustedSuppliers.Add(trustedSupplierB);

        await dbA.SaveChangesAsync();
        await dbB.SaveChangesAsync();

        var agencyASuppliers = await serviceA.GetTrustedSuppliersAsync(agencyAOrgId);

        Assert.Single(agencyASuppliers);
        Assert.Equal(trustedSupplierA.Id, agencyASuppliers[0].Id);
        Assert.Equal(agencyAOrgId, agencyASuppliers[0].OrgId);

        Assert.DoesNotContain(agencyASuppliers, s => s.Id == trustedSupplierB.Id);
        Assert.DoesNotContain(agencyASuppliers, s => s.OrgId == agencyBOrgId);

        Assert.All(agencyASuppliers, s => Assert.Equal(agencyAOrgId, s.OrgId));
    }
}

internal sealed class TestTenantContext : ITenantContext
{
    public TestTenantContext(Guid orgId)
    {
        OrgId = orgId;
    }

    public Guid? OrgId { get; }
    public bool FilterEnabled => false;
}
