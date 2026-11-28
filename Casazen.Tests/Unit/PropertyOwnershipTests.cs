using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit;

public class PropertyOwnershipTests
{
    [Fact]
    public async Task PropertyOwnership_SharesNotHundred_Returns422()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"PropertyOwnershipSharesTest_{Guid.NewGuid()}")
            .Options;

        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var owner1Id = Guid.NewGuid();
        var owner2Id = Guid.NewGuid();

        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), null))
        {
            var ownership1 = new PropertyOwnership
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                PropertyId = propertyId,
                PropertyOwnerId = owner1Id,
                SharePercent = 50.00m,
                OwnershipStart = DateTime.UtcNow,
                OwnershipEnd = null
            };

            var ownership2 = new PropertyOwnership
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                PropertyId = propertyId,
                PropertyOwnerId = owner2Id,
                SharePercent = 25.00m,
                OwnershipStart = DateTime.UtcNow,
                OwnershipEnd = null
            };

            db.Set<PropertyOwnership>().AddRange(ownership1, ownership2);
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), null))
        {
            var service = new PropertyOwnershipService(db);

            var exception = await Assert.ThrowsAsync<DomainRuleException>(
                () => service.ValidateSharesAsync(propertyId));

            Assert.Equal("shares_not_hundred", exception.Code);
            Assert.Equal("shares_not_hundred", exception.MessageKey);
        }
    }

    [Fact]
    public async Task PropertyOwnership_SharesHundred_Succeeds()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"PropertyOwnershipSharesOkTest_{Guid.NewGuid()}")
            .Options;

        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var owner1Id = Guid.NewGuid();
        var owner2Id = Guid.NewGuid();

        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), null))
        {
            var ownership1 = new PropertyOwnership
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                PropertyId = propertyId,
                PropertyOwnerId = owner1Id,
                SharePercent = 60.00m,
                OwnershipStart = DateTime.UtcNow,
                OwnershipEnd = null
            };

            var ownership2 = new PropertyOwnership
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                PropertyId = propertyId,
                PropertyOwnerId = owner2Id,
                SharePercent = 40.00m,
                OwnershipStart = DateTime.UtcNow,
                OwnershipEnd = null
            };

            db.Set<PropertyOwnership>().AddRange(ownership1, ownership2);
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), null))
        {
            var service = new PropertyOwnershipService(db);

            await service.ValidateSharesAsync(propertyId);
        }
    }

    [Fact]
    public async Task PropertyOwnership_OnlyActiveSharesCounted()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"PropertyOwnershipActiveOnlyTest_{Guid.NewGuid()}")
            .Options;

        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var owner1Id = Guid.NewGuid();
        var owner2Id = Guid.NewGuid();

        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), null))
        {
            var activeOwnership = new PropertyOwnership
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                PropertyId = propertyId,
                PropertyOwnerId = owner1Id,
                SharePercent = 100.00m,
                OwnershipStart = DateTime.UtcNow.AddYears(-1),
                OwnershipEnd = null
            };

            var endedOwnership = new PropertyOwnership
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                PropertyId = propertyId,
                PropertyOwnerId = owner2Id,
                SharePercent = 50.00m,
                OwnershipStart = DateTime.UtcNow.AddYears(-2),
                OwnershipEnd = DateTime.UtcNow.AddDays(-30)
            };

            db.Set<PropertyOwnership>().AddRange(activeOwnership, endedOwnership);
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), null))
        {
            var service = new PropertyOwnershipService(db);

            await service.ValidateSharesAsync(propertyId);
        }
    }
}

file sealed class FixedTenantContext(Guid orgId) : ITenantContext
{
    public Guid? OrgId => orgId;
    public bool FilterEnabled => true;
}
