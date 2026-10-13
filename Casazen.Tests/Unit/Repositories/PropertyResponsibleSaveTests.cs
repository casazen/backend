using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Repositories;

/// <summary>
/// AM-03b: the generic save of a property never writes the person in charge. <c>ResponsibleUserId</c> is written only by the access
/// service, under the org's people lock: when someone is put in charge, and when the access to the property is taken away and the
/// name goes back to nobody. A save of the other fields from a copy of the row read earlier (the host edits the record while the
/// owner narrows a collaborator) must not put the old name back, the way it must not put back the photos or the rental mode.
/// </summary>
public class PropertyResponsibleSaveTests
{
    private readonly string _databaseName = $"property-responsible-{Guid.NewGuid():N}";

    private AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_databaseName).Options);

    private async Task<Guid> SeedAsync(string? responsible)
    {
        await using var db = NewDb();
        var org = new OrgEntity { Name = "Org", DisplayName = "Org", Slug = $"org-{Guid.NewGuid():N}"[..20] };
        db.Orgs.Add(org);
        var property = new Property
        {
            OrgId = org.Id,
            OwnerId = "auth0|owner",
            ResponsibleUserId = responsible,
            Name = "Casa",
            Address = "Via Roma 1",
            City = "Roma",
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property.Id;
    }

    private async Task<Property> ReadAsync(Guid id)
    {
        await using var db = NewDb();
        return await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == id);
    }

    [Fact]
    public async Task UpdateAsync_ChangedResponsibleOnTheEntity_IsNotWritten()
    {
        var id = await SeedAsync(responsible: null);

        await using (var db = NewDb())
        {
            var property = await db.Properties.SingleAsync(p => p.Id == id);
            property.Name = "Casa rinnovata";
            property.ResponsibleUserId = "auth0|someone";

            await new PropertyRepository(db).UpdateAsync(property);
        }

        var stored = await ReadAsync(id);
        Assert.Equal("Casa rinnovata", stored.Name);
        Assert.Null(stored.ResponsibleUserId);
    }

    [Fact]
    public async Task UpdateAsync_FromACopyReadBeforeTheAccessWasTakenAway_DoesNotPutTheOldPersonBack()
    {
        var id = await SeedAsync(responsible: "auth0|anna");

        // The host opens the record of the property: this copy still names Anna.
        await using var staleCopy = NewDb();
        var property = await staleCopy.Properties.SingleAsync(p => p.Id == id);

        // Meanwhile the owner takes the property away from Anna and her name goes back to nobody.
        await using (var release = NewDb())
        {
            var current = await release.Properties.SingleAsync(p => p.Id == id);
            current.ResponsibleUserId = null;
            await release.SaveChangesAsync();
        }

        property.Description = "Nuova descrizione";
        await new PropertyRepository(staleCopy).UpdateAsync(property);

        var stored = await ReadAsync(id);
        Assert.Equal("Nuova descrizione", stored.Description);
        Assert.Null(stored.ResponsibleUserId);
    }

    [Fact]
    public async Task UpdateAsync_FromACopyReadBeforeSomeoneWasPutInCharge_DoesNotTakeThemOut()
    {
        var id = await SeedAsync(responsible: null);
        await using var staleCopy = NewDb();
        var property = await staleCopy.Properties.SingleAsync(p => p.Id == id);

        await using (var appoint = NewDb())
        {
            var current = await appoint.Properties.SingleAsync(p => p.Id == id);
            current.ResponsibleUserId = "auth0|anna";
            await appoint.SaveChangesAsync();
        }

        property.Description = "Nuova descrizione";
        await new PropertyRepository(staleCopy).UpdateAsync(property);

        Assert.Equal("auth0|anna", (await ReadAsync(id)).ResponsibleUserId);
    }
}
