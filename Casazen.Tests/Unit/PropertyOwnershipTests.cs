using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit;

/// <summary>
/// ADR-012-R4 proof tests for PropertyOwnership shares validation.
/// </summary>
public class PropertyOwnershipTests
{
    /// <summary>
    /// ADR-012-R4 proof: PropertyOwnershipService.ValidateSharesAsync returns 422 shares_not_hundred
    /// when the sum of active ownership shares for a property does not equal 100.00.
    /// </summary>
    [Fact]
    public async Task PropertyOwnership_SharesNotHundred_Returns422()
    {
        // Arrange: in-memory database with property ownerships that sum to 75.00 (not 100.00)
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"PropertyOwnershipSharesTest_{Guid.NewGuid()}")
            .Options;

        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var owner1Id = Guid.NewGuid();
        var owner2Id = Guid.NewGuid();

        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), null))
        {
            // Create two active ownerships that sum to 75.00 (50.00 + 25.00)
            var ownership1 = new PropertyOwnership
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                PropertyId = propertyId,
                PropertyOwnerId = owner1Id,
                SharePercent = 50.00m,
                OwnershipStart = DateTime.UtcNow,
                OwnershipEnd = null // Active
            };

            var ownership2 = new PropertyOwnership
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                PropertyId = propertyId,
                PropertyOwnerId = owner2Id,
                SharePercent = 25.00m,
                OwnershipStart = DateTime.UtcNow,
                OwnershipEnd = null // Active
            };

            db.Set<PropertyOwnership>().AddRange(ownership1, ownership2);
            await db.SaveChangesAsync();
        }

        // Act & Assert: validate shares throws DomainRuleException with code "shares_not_hundred"
        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), null))
        {
            var service = new PropertyOwnershipService(db);

            var exception = await Assert.ThrowsAsync<DomainRuleException>(
                () => service.ValidateSharesAsync(propertyId));

            Assert.Equal("shares_not_hundred", exception.Code);
            Assert.Equal("shares_not_hundred", exception.MessageKey);
        }
    }

    /// <summary>
    /// ADR-012-R4 proof: ValidateSharesAsync succeeds when shares sum to exactly 100.00.
    /// </summary>
    [Fact]
    public async Task PropertyOwnership_SharesHundred_Succeeds()
    {
        // Arrange: in-memory database with property ownerships that sum to 100.00
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"PropertyOwnershipSharesOkTest_{Guid.NewGuid()}")
            .Options;

        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var owner1Id = Guid.NewGuid();
        var owner2Id = Guid.NewGuid();

        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), null))
        {
            // Create two active ownerships that sum to 100.00 (60.00 + 40.00)
            var ownership1 = new PropertyOwnership
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                PropertyId = propertyId,
                PropertyOwnerId = owner1Id,
                SharePercent = 60.00m,
                OwnershipStart = DateTime.UtcNow,
                OwnershipEnd = null // Active
            };

            var ownership2 = new PropertyOwnership
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                PropertyId = propertyId,
                PropertyOwnerId = owner2Id,
                SharePercent = 40.00m,
                OwnershipStart = DateTime.UtcNow,
                OwnershipEnd = null // Active
            };

            db.Set<PropertyOwnership>().AddRange(ownership1, ownership2);
            await db.SaveChangesAsync();
        }

        // Act & Assert: validation succeeds without exception
        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), null))
        {
            var service = new PropertyOwnershipService(db);

            // Should not throw
            await service.ValidateSharesAsync(propertyId);
        }
    }

    /// <summary>
    /// ADR-012-R4: Ended ownerships (OwnershipEnd != null) are excluded from validation.
    /// </summary>
    [Fact]
    public async Task PropertyOwnership_OnlyActiveSharesCounted()
    {
        // Arrange: one active ownership (100.00) + one ended ownership (50.00)
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
                OwnershipEnd = null // Active
            };

            var endedOwnership = new PropertyOwnership
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                PropertyId = propertyId,
                PropertyOwnerId = owner2Id,
                SharePercent = 50.00m,
                OwnershipStart = DateTime.UtcNow.AddYears(-2),
                OwnershipEnd = DateTime.UtcNow.AddDays(-30) // Ended last month
            };

            db.Set<PropertyOwnership>().AddRange(activeOwnership, endedOwnership);
            await db.SaveChangesAsync();
        }

        // Act & Assert: validation counts only active ownership (100.00), so it succeeds
        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), null))
        {
            var service = new PropertyOwnershipService(db);

            // Should not throw (only the active 100.00% is counted)
            await service.ValidateSharesAsync(propertyId);
        }
    }
}

/// <summary>Fixed tenant context for unit tests.</summary>
file sealed class FixedTenantContext(Guid orgId) : ITenantContext
{
    public Guid? OrgId => orgId;
    public bool FilterEnabled => true;
}
