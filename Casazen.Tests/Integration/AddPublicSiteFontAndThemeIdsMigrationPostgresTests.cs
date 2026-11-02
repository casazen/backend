using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// DB-01 on real PostgreSQL: the <c>AddPublicSiteFontAndThemeIds</c> migration moves the stored theme ids of the public site to
/// the new ones. The id <c>mare</c> used to be terracotta on cream and now names a petrol blue look, so an org that had it
/// must come out as <c>terra</c> (the look it saw); <c>montagna</c> becomes <c>terra</c> and <c>urban</c> becomes <c>citta</c>,
/// keeping the colour those two themes gave when the org never picked one of its own. Nothing else changes: an own colour is
/// never overwritten, an org without a theme stays without one (the default), an unknown value is left alone. The rows are
/// written with SQL that names only the columns of the first <c>Orgs</c> table (<see cref="LegacyOrgRows"/>).
/// </summary>
public class AddPublicSiteFontAndThemeIdsMigrationPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Migration_StoredThemeIds_MoveToTheNewIds_AndTheVanishedThemesKeepTheirColour()
    {
        // (stored theme, stored colour) -> (theme after, colour after)
        var cases = new (string? Theme, string? Colour, string? ThemeAfter, string? ColourAfter)[]
        {
            ("mare", null, "terra", null),
            ("mare", "#112233", "terra", "#112233"),
            ("montagna", null, "terra", "#2e6a4a"),
            ("montagna", "#445566", "terra", "#445566"),
            ("urban", "", "citta", "#2f3bd1"),
            (" Urban ", null, "citta", "#2f3bd1"),
            ("urban", "#778899", "citta", "#778899"),
            (null, null, null, null),
            ("collina", null, "collina", null),
        };

        var ids = new List<Guid>();
        await using (var db = _database!.CreateContext())
        {
            await _database!.MigrateEmptyToAsync(PreviousMigration(db));
            foreach (var c in cases)
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                await LegacyOrgRows.InsertAsync(db, new OrgEntity
                {
                    Id = id,
                    Name = "Org DB-01",
                    Slug = $"db01-{id:N}",
                    DisplayName = "Org DB-01",
                    ContactEmail = "db01@example.com",
                    PlanTier = PlanTier.Starter,
                    IsActive = true,
                });
                if (c.Theme is not null)
                    await db.Database.ExecuteSqlAsync($"""UPDATE "Orgs" SET "PublicThemeId" = {c.Theme} WHERE "Id" = {id}""");
                if (c.Colour is not null)
                    await db.Database.ExecuteSqlAsync($"""UPDATE "Orgs" SET "ThemeColor" = {c.Colour} WHERE "Id" = {id}""");
            }

            await db.Database.MigrateAsync();
        }

        await using var after = _database.CreateContext();
        var orgs = await after.Orgs.IgnoreQueryFilters().AsNoTracking().Where(o => ids.Contains(o.Id)).ToDictionaryAsync(o => o.Id);
        Assert.Equal(cases.Length, orgs.Count);
        for (var i = 0; i < cases.Length; i++)
        {
            var org = orgs[ids[i]];
            Assert.True(
                cases[i].ThemeAfter == org.PublicThemeId && cases[i].ColourAfter == org.ThemeColor,
                $"{cases[i].Theme ?? "(none)"}/{cases[i].Colour ?? "(none)"} -> {org.PublicThemeId ?? "(none)"}/{org.ThemeColor ?? "(none)"}, expected {cases[i].ThemeAfter ?? "(none)"}/{cases[i].ColourAfter ?? "(none)"}");
            Assert.Null(org.PublicFontId);
            Assert.Equal("Org DB-01", org.DisplayName);
        }

        Assert.Empty(await after.Database.GetPendingMigrationsAsync());
    }

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_AddPublicSiteFontAndThemeIds", StringComparison.Ordinal));
        Assert.True(index > 0, "AddPublicSiteFontAndThemeIds migration not found.");
        return all[index - 1];
    }
}
