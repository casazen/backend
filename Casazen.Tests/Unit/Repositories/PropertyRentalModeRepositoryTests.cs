using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Repositories;

/// <summary>
/// PM-01 on the stored properties: the list filter of <c>GET /api/properties?mode=</c> keeps the scope of the caller, the
/// generic save never writes the rental mode (only the creation and the mode change of PM-02 do), and the plan counter
/// (<c>MaxProperties</c>) counts the properties of every mode.
/// </summary>
public class PropertyRentalModeRepositoryTests
{
    private readonly string _databaseName = $"rental-mode-{Guid.NewGuid():N}";

    // ─── List filter ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetByScopeAsync_WithMode_ReturnsOnlyThatModeOfTheActivePropertiesOfTheOrg()
    {
        await using var db = NewDb();
        var org = AddOrg(db);
        var otherOrg = AddOrg(db);
        AddProperty(db, org, "A breve", RentalMode.Short, "auth0|x");
        AddProperty(db, org, "B lungo", RentalMode.Long, "auth0|x");
        AddProperty(db, org, "C lungo", RentalMode.Long, "auth0|y");
        AddProperty(db, org, "D lungo disattivo", RentalMode.Long, "auth0|x", isActive: false);
        AddProperty(db, otherOrg, "E lungo di altri", RentalMode.Long, "auth0|x");
        await db.SaveChangesAsync();
        var repository = new PropertyRepository(db);

        var longOfOrg = await repository.GetByScopeAsync(new HostScope(org.Id, null), RentalMode.Long);
        var longOfOwner = await repository.GetByScopeAsync(new HostScope(org.Id, "auth0|x"), RentalMode.Long);
        var shortOfOrg = await repository.GetByScopeAsync(new HostScope(org.Id, null), RentalMode.Short);

        Assert.Equal(["B lungo", "C lungo"], longOfOrg.Select(p => p.Name));
        Assert.Equal(["B lungo"], longOfOwner.Select(p => p.Name));
        Assert.Equal(["A breve"], shortOfOrg.Select(p => p.Name));
    }

    [Fact]
    public async Task GetByScopeAsync_WithoutMode_ReturnsEveryActivePropertyAsBefore()
    {
        await using var db = NewDb();
        var org = AddOrg(db);
        AddProperty(db, org, "A breve", RentalMode.Short, "auth0|x");
        AddProperty(db, org, "B lungo", RentalMode.Long, "auth0|x");
        await db.SaveChangesAsync();

        var all = await new PropertyRepository(db).GetByScopeAsync(new HostScope(org.Id, null));

        Assert.Equal(["A breve", "B lungo"], all.Select(p => p.Name));
    }

    [Fact]
    public async Task GetByScopeForComplianceAsync_ReturnsTheShortRentPropertiesOnly()
    {
        await using var db = NewDb();
        var org = AddOrg(db);
        AddProperty(db, org, "A breve", RentalMode.Short, "auth0|x");
        AddProperty(db, org, "B lungo", RentalMode.Long, "auth0|x");
        await db.SaveChangesAsync();

        var properties = await new PropertyRepository(db).GetByScopeForComplianceAsync(new HostScope(org.Id, null));

        Assert.Equal(["A breve"], properties.Select(p => p.Name));
    }

    // ─── The generic save never writes the mode ─────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_ChangedRentalModeOnTheEntity_IsNotWritten()
    {
        Guid propertyId;
        await using (var db = NewDb())
        {
            var org = AddOrg(db);
            propertyId = AddProperty(db, org, "Casa", RentalMode.Short, "auth0|x").Id;
            await db.SaveChangesAsync();
        }

        await using (var db = NewDb())
        {
            var property = await db.Properties.SingleAsync(p => p.Id == propertyId);
            property.Name = "Casa rinnovata";
            property.RentalMode = RentalMode.Long;

            await new PropertyRepository(db).UpdateAsync(property);
        }

        await using var check = NewDb();
        var stored = await check.Properties.AsNoTracking().SingleAsync(p => p.Id == propertyId);
        Assert.Equal("Casa rinnovata", stored.Name);
        Assert.Equal(RentalMode.Short, stored.RentalMode);
    }

    [Fact]
    public async Task UpdateAsync_FromACopyReadBeforeTheModeChanged_DoesNotPutTheOldModeBack()
    {
        Guid propertyId;
        await using (var db = NewDb())
        {
            var org = AddOrg(db);
            propertyId = AddProperty(db, org, "Casa", RentalMode.Short, "auth0|x").Id;
            await db.SaveChangesAsync();
        }

        await using var staleCopy = NewDb();
        var property = await staleCopy.Properties.SingleAsync(p => p.Id == propertyId);

        // Meanwhile the scheduled mode change (PM-02) moves the property to long-term.
        await using (var changer = NewDb())
        {
            var current = await changer.Properties.SingleAsync(p => p.Id == propertyId);
            current.RentalMode = RentalMode.Long;
            await changer.SaveChangesAsync();
        }

        property.Description = "Nuova descrizione";
        await new PropertyRepository(staleCopy).UpdateAsync(property);

        await using var check = NewDb();
        var stored = await check.Properties.AsNoTracking().SingleAsync(p => p.Id == propertyId);
        Assert.Equal("Nuova descrizione", stored.Description);
        Assert.Equal(RentalMode.Long, stored.RentalMode);
    }

    [Fact]
    public async Task AddAsync_StoresTheModeOfTheNewProperty()
    {
        await using var db = NewDb();
        var org = AddOrg(db);
        await db.SaveChangesAsync();
        var property = new Property
        {
            OwnerId = "auth0|x",
            OrgId = org.Id,
            Name = "Bilocale",
            Address = "Via Roma 1",
            City = "Monza",
            RentalMode = RentalMode.Long,
        };

        await new PropertyRepository(db).AddAsync(property);

        await using var check = NewDb();
        Assert.Equal(RentalMode.Long, (await check.Properties.AsNoTracking().SingleAsync(p => p.Id == property.Id)).RentalMode);
    }

    // ─── Plan counter ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetEntitlementAsync_CountsThePropertiesOfEveryMode()
    {
        await using var db = NewDb();
        var org = AddOrg(db);
        AddProperty(db, org, "A breve", RentalMode.Short, "auth0|x");
        AddProperty(db, org, "B lungo", RentalMode.Long, "auth0|x");
        AddProperty(db, org, "C lungo", RentalMode.Long, "auth0|x");
        await db.SaveChangesAsync();
        var entitlements = new EntitlementService(db, new ConfigurationBuilder().Build());

        var entitlement = await entitlements.GetEntitlementAsync(org.Id);

        // Starter allows 3 properties: the two long-term ones use their slots like any other.
        Assert.Equal(3, entitlement.PropertyCount);
        Assert.Equal(3, entitlement.MaxProperties);
        Assert.False(entitlement.CanAddProperty);
    }

    [Fact]
    public async Task CreatePropertyWithinLimitAsync_LongTermPropertiesTakeTheLastSlots()
    {
        await using var db = NewDb();
        var org = AddOrg(db);
        AddProperty(db, org, "A breve", RentalMode.Short, "auth0|x");
        AddProperty(db, org, "B lungo", RentalMode.Long, "auth0|x");
        AddProperty(db, org, "C lungo", RentalMode.Long, "auth0|x");
        await db.SaveChangesAsync();
        var created = false;

        var result = await new EntitlementService(db, new ConfigurationBuilder().Build()).CreatePropertyWithinLimitAsync(
            org.Id,
            () =>
            {
                created = true;
                return Task.FromResult(new Property());
            });

        Assert.Null(result);
        Assert.False(created);
    }

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static OrgEntity AddOrg(AppDbContext db)
    {
        var org = new OrgEntity
        {
            Name = "Org",
            Slug = $"org-{Guid.NewGuid():N}",
            DisplayName = "Org",
            ContactEmail = "host@org.test",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        };
        db.Orgs.Add(org);
        return org;
    }

    private static Property AddProperty(
        AppDbContext db,
        OrgEntity org,
        string name,
        RentalMode mode,
        string ownerId,
        bool isActive = true)
    {
        var property = new Property
        {
            OwnerId = ownerId,
            OrgId = org.Id,
            Name = name,
            Address = $"Via {name} 1",
            City = "Monza",
            PostalCode = "20900",
            RentalMode = mode,
            IsActive = isActive,
        };
        db.Properties.Add(property);
        return property;
    }
}
