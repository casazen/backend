using Casazen.Core.DTOs;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// US-001 (#212) service-layer projection tests for public read-models (AC1, AC3, AC5, AC7).
/// </summary>
public class PropertyServicePublicReadModelTests
{
    private static PropertyService CreateService(AppDbContext context) =>
        new(new PropertyRepository(context), new Mock<ILogger<PropertyService>>().Object);

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task SearchAsync_ProjectsWhitelistFields_WithoutOwnerId()
    {
        await using var context = CreateContext();
        var org = await SeedOrgAsync(context);
        context.Properties.Add(new Property
        {
            OwnerId = "auth0|owner-secret",
            OrgId = org.Id,
            Name = "Whitelist Test",
            Description = "Desc",
            Address = "Secret Address",
            City = "Milan",
            PostalCode = "20100",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 90m,
            CleaningFee = 30m,
            CinCode = "IT-12345-0123456789",
            IsActive = true,
        });
        await context.SaveChangesAsync();

        var result = (await CreateService(context).SearchAsync("Milan", null, null)).ToList();

        Assert.Single(result);
        var dto = result[0];
        Assert.Equal("Whitelist Test", dto.Name);
        Assert.Equal(CinStatus.Valid, dto.CinStatus);
        Assert.Equal("Milan", dto.City);
        Assert.DoesNotContain(dto.GetType().GetProperties().Select(p => p.Name), n => n.Equals("OwnerId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchAsync_ExcludesInactiveProperties()
    {
        await using var context = CreateContext();
        var org = await SeedOrgAsync(context);
        context.Properties.AddRange(
            new Property { OwnerId = "auth0|a", OrgId = org.Id, Name = "Active", Address = "A", City = "Rome", IsActive = true, NightlyRate = 50m, Bedrooms = 1, Bathrooms = 1, MaxGuests = 2 },
            new Property { OwnerId = "auth0|b", OrgId = org.Id, Name = "Inactive", Address = "B", City = "Rome", IsActive = false, NightlyRate = 50m, Bedrooms = 1, Bathrooms = 1, MaxGuests = 2 });
        await context.SaveChangesAsync();

        var result = await CreateService(context).SearchAsync(null, null, null);

        Assert.Single(result);
        Assert.Equal("Active", result.First().Name);
    }

    [Fact]
    public async Task SearchAsync_CapsAt50Results()
    {
        await using var context = CreateContext();
        var org = await SeedOrgAsync(context);
        for (var i = 0; i < 60; i++)
        {
            context.Properties.Add(new Property
            {
                OwnerId = $"auth0|{i}",
                OrgId = org.Id,
                Name = $"Property {i}",
                Address = $"Addr {i}",
                City = "CapCity",
                IsActive = true,
                NightlyRate = i,
                Bedrooms = 1,
                Bathrooms = 1,
                MaxGuests = 2,
            });
        }
        await context.SaveChangesAsync();

        var result = await CreateService(context).SearchAsync("CapCity", null, null);

        Assert.Equal(50, result.Count());
    }

    [Fact]
    public async Task GetPublicPropertyAsync_ReturnsDetailFields_ForActiveProperty()
    {
        await using var context = CreateContext();
        var org = await SeedOrgAsync(context);
        var policy = new CancellationPolicy { Name = "Std", Description = "48h full refund" };
        context.CancellationPolicies.Add(policy);
        var property = new Property
        {
            OwnerId = "auth0|owner",
            OrgId = org.Id,
            Name = "Detail Villa",
            Address = "Via Detail 1",
            City = "Florence",
            HouseRules = "Quiet hours after 22:00",
            CancellationPolicyId = policy.Id,
            IsActive = true,
            NightlyRate = 100m,
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
        };
        context.Properties.Add(property);
        await context.SaveChangesAsync();

        var dto = await CreateService(context).GetPublicPropertyAsync(property.Id);

        Assert.NotNull(dto);
        Assert.Equal("Quiet hours after 22:00", dto!.HouseRules);
        Assert.Equal("48h full refund", dto.CancellationPolicySummary);
        Assert.Equal("EUR", dto.Currency);
        Assert.Null(dto.MinNights);
    }

    [Fact]
    public async Task GetPublicPropertyAsync_ReturnsNull_ForInactiveProperty()
    {
        await using var context = CreateContext();
        var org = await SeedOrgAsync(context);
        var property = new Property
        {
            OwnerId = "auth0|owner",
            OrgId = org.Id,
            Name = "Draft",
            Address = "Draft addr",
            City = "Rome",
            IsActive = false,
            NightlyRate = 80m,
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
        };
        context.Properties.Add(property);
        await context.SaveChangesAsync();

        var dto = await CreateService(context).GetPublicPropertyAsync(property.Id);

        Assert.Null(dto);
    }

    [Theory]
    [InlineData(null, CinStatus.Missing)]
    [InlineData("IT-12345-0123456789", CinStatus.Valid)]
    [InlineData("BAD", CinStatus.Invalid)]
    public async Task SearchAsync_DerivesCinStatus_FromCinCode(string? cinCode, CinStatus expected)
    {
        await using var context = CreateContext();
        var org = await SeedOrgAsync(context);
        context.Properties.Add(new Property
        {
            OwnerId = "auth0|owner",
            OrgId = org.Id,
            Name = "CIN Test",
            Address = "Addr",
            City = "Turin",
            CinCode = cinCode,
            IsActive = true,
            NightlyRate = 70m,
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
        });
        await context.SaveChangesAsync();

        var dto = (await CreateService(context).SearchAsync("Turin", null, null)).Single();

        Assert.Equal(expected, dto.CinStatus);
    }

    // ── SearchByOrgAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchByOrgAsync_ReturnsOnlyPropertiesForGivenOrg()
    {
        await using var context = CreateContext();
        var orgA = await SeedOrgAsync(context);
        var orgB = await SeedOrgAsync(context);

        context.Properties.AddRange(
            new Property { OwnerId = "auth0|a", OrgId = orgA.Id, Name = "Org A Villa", Address = "A", City = "Milan", IsActive = true, NightlyRate = 100m, Bedrooms = 2, Bathrooms = 1, MaxGuests = 4 },
            new Property { OwnerId = "auth0|b", OrgId = orgB.Id, Name = "Org B Villa", Address = "B", City = "Rome", IsActive = true, NightlyRate = 80m, Bedrooms = 1, Bathrooms = 1, MaxGuests = 2 });
        await context.SaveChangesAsync();

        var result = (await CreateService(context).SearchByOrgAsync(orgA.Id)).ToList();

        Assert.Single(result);
        Assert.Equal("Org A Villa", result[0].Name);
    }

    [Fact]
    public async Task SearchByOrgAsync_ExcludesInactiveProperties()
    {
        await using var context = CreateContext();
        var org = await SeedOrgAsync(context);

        context.Properties.AddRange(
            new Property { OwnerId = "auth0|a1", OrgId = org.Id, Name = "Active", Address = "A", City = "Venice", IsActive = true, NightlyRate = 90m, Bedrooms = 1, Bathrooms = 1, MaxGuests = 2 },
            new Property { OwnerId = "auth0|a2", OrgId = org.Id, Name = "Draft", Address = "B", City = "Venice", IsActive = false, NightlyRate = 90m, Bedrooms = 1, Bathrooms = 1, MaxGuests = 2 });
        await context.SaveChangesAsync();

        var result = (await CreateService(context).SearchByOrgAsync(org.Id)).ToList();

        Assert.Single(result);
        Assert.Equal("Active", result[0].Name);
    }

    [Fact]
    public async Task SearchByOrgAsync_CapsAt50Results()
    {
        await using var context = CreateContext();
        var org = await SeedOrgAsync(context);

        for (var i = 0; i < 60; i++)
        {
            context.Properties.Add(new Property
            {
                OwnerId = $"auth0|{i}",
                OrgId = org.Id,
                Name = $"Prop {i}",
                Address = $"Addr {i}",
                City = "CapOrg",
                IsActive = true,
                NightlyRate = i,
                Bedrooms = 1,
                Bathrooms = 1,
                MaxGuests = 2,
            });
        }
        await context.SaveChangesAsync();

        var result = await CreateService(context).SearchByOrgAsync(org.Id);

        Assert.Equal(50, result.Count());
    }

    [Fact]
    public async Task SearchByOrgAsync_ReturnsEmpty_ForOrgWithNoListings()
    {
        await using var context = CreateContext();
        var org = await SeedOrgAsync(context);

        var result = await CreateService(context).SearchByOrgAsync(org.Id);

        Assert.Empty(result);
    }

    // ── GetPublicPropertyForOrgAsync ─────────────────────────────────────────────

    [Fact]
    public async Task GetPublicPropertyForOrgAsync_ReturnsDetail_WhenPropertyBelongsToOrg()
    {
        await using var context = CreateContext();
        var org = await SeedOrgAsync(context);
        var policy = new CancellationPolicy { Name = "Flex", Description = "Full refund 24h" };
        context.CancellationPolicies.Add(policy);
        var property = new Property
        {
            OwnerId = "auth0|owner",
            OrgId = org.Id,
            Name = "Org Property",
            Address = "Via Org 1",
            City = "Naples",
            HouseRules = "No smoking",
            CancellationPolicyId = policy.Id,
            IsActive = true,
            NightlyRate = 110m,
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
        };
        context.Properties.Add(property);
        await context.SaveChangesAsync();

        var dto = await CreateService(context).GetPublicPropertyForOrgAsync(property.Id, org.Id);

        Assert.NotNull(dto);
        Assert.Equal("No smoking", dto!.HouseRules);
        Assert.Equal("Full refund 24h", dto.CancellationPolicySummary);
    }

    [Fact]
    public async Task GetPublicPropertyForOrgAsync_ReturnsNull_WhenPropertyBelongsToOtherOrg()
    {
        // AC3: cross-org property lookup must return null so controller returns 404.
        await using var context = CreateContext();
        var orgA = await SeedOrgAsync(context);
        var orgB = await SeedOrgAsync(context);
        var property = new Property
        {
            OwnerId = "auth0|owner-b",
            OrgId = orgB.Id,
            Name = "Org B Property",
            Address = "Via B 1",
            City = "Turin",
            IsActive = true,
            NightlyRate = 75m,
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
        };
        context.Properties.Add(property);
        await context.SaveChangesAsync();

        var dto = await CreateService(context).GetPublicPropertyForOrgAsync(property.Id, orgA.Id);

        Assert.Null(dto);
    }

    [Fact]
    public async Task GetPublicPropertyForOrgAsync_ReturnsNull_WhenPropertyIsInactive()
    {
        await using var context = CreateContext();
        var org = await SeedOrgAsync(context);
        var property = new Property
        {
            OwnerId = "auth0|owner",
            OrgId = org.Id,
            Name = "Inactive Prop",
            Address = "Via X",
            City = "Bari",
            IsActive = false,
            NightlyRate = 60m,
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
        };
        context.Properties.Add(property);
        await context.SaveChangesAsync();

        var dto = await CreateService(context).GetPublicPropertyForOrgAsync(property.Id, org.Id);

        Assert.Null(dto);
    }

    private static async Task<OrgEntity> SeedOrgAsync(AppDbContext context)
    {
        var org = new OrgEntity
        {
            Name = "Test Org",
            Slug = $"test-{Guid.NewGuid():N}",
            DisplayName = "Test Org",
            ContactEmail = "test@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        };
        context.Orgs.Add(org);
        await context.SaveChangesAsync();
        return org;
    }
}
