using Casazen.Core.Entities;
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

    private static AdminService CreateService(AppDbContext ctx) =>
        new AdminService(ctx, new Mock<ILogger<AdminService>>().Object);

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
