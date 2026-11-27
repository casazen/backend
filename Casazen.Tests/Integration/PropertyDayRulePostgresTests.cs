using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// NF-36 (E08/F05/US01/T01) on a real PostgreSQL database, where it counts: a night of a property has one row, and the values of a rule stay inside
/// their bounds whoever writes them. It is the database itself that refuses a price of nothing, a maximum stay below the minimum and a second row
/// for the same date, so a price a guest is quoted can never be a price the rules of the service would have refused.
/// </summary>
public class PropertyDayRulePostgresTests
{
    private static readonly DateOnly Night = new(2026, 12, 24);

    /// <summary>
    /// PROOF TEST: data rule. The check constraint refuses a price of 0 and a price above 100000, a maximum stay below the minimum, a stay outside 1
    /// to 365 nights, an advance outside 0 to 730 days and a maximum advance below the minimum; the unique index refuses a second row for the same
    /// property and date. A whole row is accepted, so the refusals are those of the rules and not of the setup.
    /// </summary>
    [PostgresFact]
    public async Task PropertyDayRule_InvalidValues_AreRejectedByTheDatabase_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("dayrules");
        var (orgId, propertyId) = await SeedPropertyAsync(database);

        // A whole row first: every value inside its bounds, at the edges.
        await SaveAsync(database, Rule(orgId, propertyId, Night, r =>
        {
            r.Price = 0.01m;
            r.MinNights = 1;
            r.MaxNights = 365;
            r.MinAdvanceDays = 0;
            r.MaxAdvanceDays = 730;
            r.ClosedToArrival = true;
        }));
        await SaveAsync(database, Rule(orgId, propertyId, Night.AddDays(1), r =>
        {
            r.Price = 100000m;
            r.MinNights = 3;
            r.MaxNights = 3;
        }));

        var flaws = new (string Name, Action<PropertyDayRule> Apply)[]
        {
            ("price-zero", r => r.Price = 0m),
            ("price-above-the-range", r => r.Price = 100000.01m),
            ("price-negative", r => r.Price = -5m),
            ("max-below-min", r => { r.MinNights = 4; r.MaxNights = 3; }),
            ("min-nights-zero", r => r.MinNights = 0),
            ("max-nights-above-the-range", r => r.MaxNights = 366),
            ("min-advance-negative", r => r.MinAdvanceDays = -1),
            ("max-advance-above-the-range", r => r.MaxAdvanceDays = 731),
            ("max-advance-below-min", r => { r.MinAdvanceDays = 10; r.MaxAdvanceDays = 9; }),
        };

        var day = Night.AddDays(10);
        foreach (var (name, apply) in flaws)
        {
            var refused = await SaveFailingAsync(database, Rule(orgId, propertyId, day, apply));
            var postgres = Assert.IsType<PostgresException>(refused.InnerException);
            Assert.True(
                postgres.SqlState == PostgresErrorCodes.CheckViolation && postgres.ConstraintName == PropertyDayRule.ValuesCheckName,
                $"{name}: expected the check constraint, got {postgres.SqlState} {postgres.ConstraintName}");
            day = day.AddDays(1);
        }

        // A second row for a night that has one: the unique index says no, the first row is untouched.
        var duplicate = await SaveFailingAsync(database, Rule(orgId, propertyId, Night, r => r.Price = 99m));
        var unique = Assert.IsType<PostgresException>(duplicate.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, unique.SqlState);
        Assert.Equal(PropertyDayRule.PropertyDateIndexName, unique.ConstraintName);

        await using var read = database.CreateContext();
        var rows = await read.PropertyDayRules.IgnoreQueryFilters().AsNoTracking().Where(r => r.PropertyId == propertyId).OrderBy(r => r.StayDate).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(0.01m, rows[0].Price);
        Assert.True(rows[0].ClosedToArrival);
        Assert.Equal(100000m, rows[1].Price);
    }

    private static async Task<(Guid OrgId, Guid PropertyId)> SeedPropertyAsync(PostgresTestDatabase database)
    {
        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await using var seed = database.CreateContext();
        seed.Orgs.Add(new OrgEntity
        {
            Id = orgId,
            Name = "Casa Regole",
            Slug = $"day-rules-{orgId:N}"[..28],
            DisplayName = "Casa Regole",
            ContactEmail = "day-rules@example.com",
            OrgType = OrgType.Host,
            PlanTier = PlanTier.Starter,
        });
        seed.Properties.Add(new Property
        {
            Id = propertyId,
            OrgId = orgId,
            OwnerId = "auth0|owner",
            Name = "Casa del Lago",
            Address = "Via del Lago 1",
            City = "Como",
        });
        await seed.SaveChangesAsync();
        return (orgId, propertyId);
    }

    private static PropertyDayRule Rule(Guid orgId, Guid propertyId, DateOnly date, Action<PropertyDayRule> apply)
    {
        var rule = new PropertyDayRule { OrgId = orgId, PropertyId = propertyId, StayDate = date };
        apply(rule);
        return rule;
    }

    private static async Task SaveAsync(PostgresTestDatabase database, PropertyDayRule rule)
    {
        await using var db = database.CreateContext();
        db.PropertyDayRules.Add(rule);
        await db.SaveChangesAsync();
    }

    private static async Task<DbUpdateException> SaveFailingAsync(PostgresTestDatabase database, PropertyDayRule rule)
    {
        await using var db = database.CreateContext();
        db.PropertyDayRules.Add(rule);
        return await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
