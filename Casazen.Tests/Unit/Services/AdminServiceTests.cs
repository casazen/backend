using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class AdminServiceTests
{
    private static AppDbContext CreateInMemoryContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>
    /// Builds an <see cref="AppDbContext"/> wired to a REAL authenticated <see cref="ITenantContext"/>
    /// (filter ENABLED, scoped to <paramref name="callerOrgId"/>). This deliberately differs from
    /// <see cref="CreateInMemoryContext"/> / <c>new AppDbContext(options)</c>, which fall back to
    /// <c>NullTenantContext</c> and disable the global tenant filter — the very condition that
    /// masked the #202 F-H1 admin cross-org regression.
    /// </summary>
    private static AppDbContext CreateTenantScopedContext(string dbName, Guid callerOrgId)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AppDbContext(options, new AuthenticatedTenantContext(callerOrgId));
    }

    private static AdminService CreateService(AppDbContext ctx) =>
        new AdminService(ctx, new Mock<ILogger<AdminService>>().Object);

    /// <summary>Authenticated tenant context: the global query filter is ON and scoped to one org.</summary>
    private sealed class AuthenticatedTenantContext(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;
        public bool FilterEnabled => true;
    }

    // ─── GetStatsAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatsAsync_EmptyDatabase_ReturnsZeroStats()
    {
        // Arrange
        using var ctx = CreateInMemoryContext(nameof(GetStatsAsync_EmptyDatabase_ReturnsZeroStats));
        var service = CreateService(ctx);

        // Act
        var stats = await service.GetStatsAsync();

        // Assert
        Assert.Equal(0, stats.TotalProperties);
        Assert.Equal(0, stats.TotalBookings);
        Assert.Equal(0m, stats.TotalRevenue);
        Assert.Equal(0, stats.CinTotal);
        Assert.Equal(0, stats.OtaNeverSynced);
    }

    [Fact]
    public async Task GetStatsAsync_WithProperties_ReturnsCinBreakdown()
    {
        // Arrange
        using var ctx = CreateInMemoryContext(nameof(GetStatsAsync_WithProperties_ReturnsCinBreakdown));

        ctx.Properties.AddRange(
            new Property { Name = "P1", OwnerId = "o1", Address = "a", City = "Roma", CinCode = "IT-12345-0123456789" },  // valid
            new Property { Name = "P2", OwnerId = "o1", Address = "a", City = "Roma", CinCode = null },                  // missing
            new Property { Name = "P3", OwnerId = "o1", Address = "a", City = "Roma", CinCode = "INVALID" }              // invalid
        );
        await ctx.SaveChangesAsync();

        var service = CreateService(ctx);

        // Act
        var stats = await service.GetStatsAsync();

        // Assert
        Assert.Equal(3, stats.CinTotal);
        Assert.Equal(1, stats.CinValid);
        Assert.Equal(1, stats.CinMissing);
        Assert.Equal(1, stats.CinInvalid);
    }

    // ─── F-H1 regression: admin reads must bypass the tenant filter (cross-org) ──

    [Fact]
    public async Task GetStatsAsync_AuthenticatedAdminContext_ReturnsPlatformWideTotalsAcrossAllOrgs()
    {
        // Regression for #202 F-H1. The global tenant filter must NOT scope platform-wide admin
        // reads. We exercise the REAL filter: AppDbContext is built with an authenticated
        // ITenantContext (scoped to orgA), NOT new AppDbContext(options) (NullTenantContext →
        // filter disabled), which is exactly what masked this bug. Two distinct orgs are seeded;
        // GetStatsAsync must aggregate BOTH orgs (IgnoreQueryFilters). Without the fix the caller's
        // org alone is counted (1 / 1 / 1 / €100) and every assertion below fails.
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        using var ctx = CreateTenantScopedContext(
            nameof(GetStatsAsync_AuthenticatedAdminContext_ReturnsPlatformWideTotalsAcrossAllOrgs),
            callerOrgId: orgA);

        ctx.Orgs.AddRange(
            new Org { Id = orgA, Name = "Org A", Slug = "org-a", DisplayName = "Org A", ContactEmail = "a@x.io", PlanTier = PlanTier.Starter },
            new Org { Id = orgB, Name = "Org B", Slug = "org-b", DisplayName = "Org B", ContactEmail = "b@x.io", PlanTier = PlanTier.Pro });

        var propA = new Property { Id = Guid.NewGuid(), OrgId = orgA, OwnerId = "ownerA", Name = "A1", Address = "a", City = "Roma", CinCode = "IT-12345-0123456789", IsActive = true };
        var propB = new Property { Id = Guid.NewGuid(), OrgId = orgB, OwnerId = "ownerB", Name = "B1", Address = "b", City = "Milano", CinCode = "IT-54321-9876543210", IsActive = true };
        ctx.Properties.AddRange(propA, propB);

        var bookingA = new Booking { Id = Guid.NewGuid(), OrgId = orgA, PropertyId = propA.Id, GuestId = Guid.NewGuid(), Status = BookingStatus.Confirmed, CheckInDate = DateTime.UtcNow.AddDays(5), CheckOutDate = DateTime.UtcNow.AddDays(7) };
        var bookingB = new Booking { Id = Guid.NewGuid(), OrgId = orgB, PropertyId = propB.Id, GuestId = Guid.NewGuid(), Status = BookingStatus.Confirmed, CheckInDate = DateTime.UtcNow.AddDays(5), CheckOutDate = DateTime.UtcNow.AddDays(7) };
        ctx.Bookings.AddRange(bookingA, bookingB);

        ctx.Payments.AddRange(
            new Payment { Id = Guid.NewGuid(), OrgId = orgA, BookingId = bookingA.Id, Amount = 100m, Status = PaymentStatus.Completed },
            new Payment { Id = Guid.NewGuid(), OrgId = orgB, BookingId = bookingB.Id, Amount = 150m, Status = PaymentStatus.Completed });

        await ctx.SaveChangesAsync();

        // Guard: confirm the tenant filter really is engaged on this context (a filtered read sees
        // only the caller org's single property). This is what distinguishes the real filter from
        // the NullTenantContext path and proves the assertions below depend on IgnoreQueryFilters.
        Assert.Equal(1, await ctx.Properties.CountAsync());

        var service = CreateService(ctx);

        // Act
        var stats = await service.GetStatsAsync();

        // Assert — platform-wide totals span BOTH orgs.
        Assert.Equal(2, stats.TotalProperties);
        Assert.Equal(2, stats.TotalBookings);
        Assert.Equal(2, stats.UpcomingCheckIns);
        Assert.Equal(250m, stats.TotalRevenue);
        Assert.Equal(2, stats.CinTotal);
        Assert.Equal(2, stats.CinValid);
    }

    [Fact]
    public async Task GetCinComplianceAsync_AuthenticatedAdminContext_ReturnsRowsAcrossAllOrgs()
    {
        // Companion to F-H1 for the CIN-compliance bypass site: the regulatory report must remain
        // platform-wide under an authenticated admin caller, not scoped to the caller's org.
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        using var ctx = CreateTenantScopedContext(
            nameof(GetCinComplianceAsync_AuthenticatedAdminContext_ReturnsRowsAcrossAllOrgs),
            callerOrgId: orgA);

        ctx.Properties.AddRange(
            new Property { Id = Guid.NewGuid(), OrgId = orgA, OwnerId = "ownerA", Name = "A1", Address = "a", City = "Roma", CinCode = "IT-12345-0123456789" },
            new Property { Id = Guid.NewGuid(), OrgId = orgB, OwnerId = "ownerB", Name = "B1", Address = "b", City = "Milano", CinCode = null });
        await ctx.SaveChangesAsync();

        var service = CreateService(ctx);

        var (items, total) = await service.GetCinComplianceAsync(null, 1, 20);

        Assert.Equal(2, total);
        Assert.Equal(2, items.Count());
    }

    // ─── GetCinComplianceAsync ──────────────────────────────────────────────

    [Fact]
    public async Task GetCinComplianceAsync_FilterValid_ReturnsOnlyValidProperties()
    {
        // Arrange
        using var ctx = CreateInMemoryContext(nameof(GetCinComplianceAsync_FilterValid_ReturnsOnlyValidProperties));

        ctx.Properties.AddRange(
            new Property { Name = "Valid", OwnerId = "o1", Address = "a", City = "Roma", CinCode = "IT-12345-0123456789" },
            new Property { Name = "Missing", OwnerId = "o1", Address = "a", City = "Roma", CinCode = null },
            new Property { Name = "Invalid", OwnerId = "o1", Address = "a", City = "Roma", CinCode = "INVALID" }
        );
        await ctx.SaveChangesAsync();

        var service = CreateService(ctx);

        // Act
        var (items, total) = await service.GetCinComplianceAsync("valid", 1, 20);

        // Assert
        Assert.Equal(1, total);
        Assert.Single(items);
        Assert.Equal("valid", items.First().CinStatus);
    }

    [Fact]
    public async Task GetCinComplianceAsync_FilterMissing_ReturnsOnlyMissingProperties()
    {
        // Arrange
        using var ctx = CreateInMemoryContext(nameof(GetCinComplianceAsync_FilterMissing_ReturnsOnlyMissingProperties));

        ctx.Properties.AddRange(
            new Property { Name = "Valid", OwnerId = "o1", Address = "a", City = "Roma", CinCode = "IT-12345-0123456789" },
            new Property { Name = "Missing", OwnerId = "o1", Address = "a", City = "Roma", CinCode = null }
        );
        await ctx.SaveChangesAsync();

        var service = CreateService(ctx);

        // Act
        var (items, total) = await service.GetCinComplianceAsync("missing", 1, 20);

        // Assert
        Assert.Equal(1, total);
        Assert.Equal("missing", items.First().CinStatus);
    }

    [Fact]
    public async Task GetCinComplianceAsync_FilterInvalid_ReturnsOnlyInvalidProperties()
    {
        // Arrange
        using var ctx = CreateInMemoryContext(nameof(GetCinComplianceAsync_FilterInvalid_ReturnsOnlyInvalidProperties));

        ctx.Properties.AddRange(
            new Property { Name = "Valid", OwnerId = "o1", Address = "a", City = "Roma", CinCode = "IT-12345-0123456789" },
            new Property { Name = "Invalid", OwnerId = "o1", Address = "a", City = "Roma", CinCode = "BAD" }
        );
        await ctx.SaveChangesAsync();

        var service = CreateService(ctx);

        // Act
        var (items, total) = await service.GetCinComplianceAsync("invalid", 1, 20);

        // Assert
        Assert.Equal(1, total);
        Assert.Equal("invalid", items.First().CinStatus);
    }

    [Fact]
    public async Task GetCinComplianceAsync_UnknownStatus_ThrowsArgumentException()
    {
        // Arrange
        using var ctx = CreateInMemoryContext(nameof(GetCinComplianceAsync_UnknownStatus_ThrowsArgumentException));
        var service = CreateService(ctx);

        // Act + Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.GetCinComplianceAsync("unknown-status", 1, 20));
    }

    [Fact]
    public async Task GetCinComplianceAsync_NullFilter_ReturnsAllProperties()
    {
        // Arrange
        using var ctx = CreateInMemoryContext(nameof(GetCinComplianceAsync_NullFilter_ReturnsAllProperties));

        ctx.Properties.AddRange(
            new Property { Name = "P1", OwnerId = "o1", Address = "a", City = "Roma", CinCode = "IT-12345-0123456789" },
            new Property { Name = "P2", OwnerId = "o1", Address = "a", City = "Roma", CinCode = null }
        );
        await ctx.SaveChangesAsync();

        var service = CreateService(ctx);

        // Act
        var (items, total) = await service.GetCinComplianceAsync(null, 1, 20);

        // Assert
        Assert.Equal(2, total);
    }
}
