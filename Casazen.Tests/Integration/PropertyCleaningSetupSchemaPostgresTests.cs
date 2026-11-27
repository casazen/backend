using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// E24/F01/US01/T01 and T02 (NF-120, the review of slice S015) on a real PostgreSQL database: the checks and the unique indexes of the migration <c>AddPropertyCleaningSetup</c>, which the
/// InMemory provider neither enforces nor can prove. A row written by hand cannot be one the automation would refuse: a supplier has its org and a price from 1 cent to the bound of a
/// service request and no staff member, a member of the staff has no supplier and, when it has a price, the same bounds; the duration is the one of the catalog (5 to 1,440 minutes); a
/// property has one setup and a stay has one automatic cleaning.
/// </summary>
public class PropertyCleaningSetupSchemaPostgresTests : IAsyncLifetime
{
    private const string CleanerCheck = "CK_PropertyCleaningSetups_Cleaner";
    private const string DurationCheck = "CK_PropertyCleaningSetups_Duration";

    private PostgresTestDatabase? _database;
    private HostScopeWorld _world = null!;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateMigratedAsync();
        await using var db = _database.CreateContext();
        _world = await HostScopeScenario.SeedAsync(db);
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    private PropertyCleaningSetup SupplierSetup(Property property, Action<PropertyCleaningSetup>? change = null)
    {
        var setup = new PropertyCleaningSetup
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            CleanerKind = PropertyCleanerKind.Supplier,
            SupplierOrgId = _world.SupplierOrgId,
            PriceCents = 7500,
        };
        change?.Invoke(setup);
        return setup;
    }

    private PropertyCleaningSetup StaffSetup(Property property, Action<PropertyCleaningSetup>? change = null)
    {
        var setup = new PropertyCleaningSetup
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            CleanerKind = PropertyCleanerKind.Staff,
            StaffUserId = _world.OwnerUserId,
        };
        change?.Invoke(setup);
        return setup;
    }

    /// <summary>The error PostgreSQL answers to a save of <paramref name="rows"/>: they are refused, and nothing is saved.</summary>
    private async Task<PostgresException> RefusedAsync(params object[] rows)
    {
        await using var db = _database!.CreateContext();
        db.AddRange(rows);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        return Assert.IsType<PostgresException>(error.InnerException);
    }

    private async Task SavedAsync(params object[] rows)
    {
        await using var db = _database!.CreateContext();
        db.AddRange(rows);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Data rule: the kind of cleaner ties the columns it uses. Every row the API would refuse (a supplier with no org, with a staff member too, with no price or a price out of the bounds;
    /// a member of the staff with no user, with a supplier, with a price out of the bounds) is refused by the check of the database too. All the cases are run and reported together.
    /// </summary>
    [PostgresFact]
    public async Task Setup_TheKindOfCleaner_TiesTheColumnsItUses_Test()
    {
        var property = _world.Granted;
        var cases = new (string Name, PropertyCleaningSetup Setup)[]
        {
            ("supplier with no org", SupplierSetup(property, s => s.SupplierOrgId = null)),
            ("supplier with a staff member too", SupplierSetup(property, s => s.StaffUserId = _world.OwnerUserId)),
            ("supplier with no price", SupplierSetup(property, s => s.PriceCents = null)),
            ("supplier with a price of 0", SupplierSetup(property, s => s.PriceCents = 0)),
            ("supplier with a price over the bound", SupplierSetup(property, s => s.PriceCents = ServiceRequestLimits.MaxAmountCents + 1)),
            ("staff with no user", StaffSetup(property, s => s.StaffUserId = null)),
            ("staff with a supplier too", StaffSetup(property, s => s.SupplierOrgId = _world.SupplierOrgId)),
            ("staff with a price of 0", StaffSetup(property, s => s.PriceCents = 0)),
            ("staff with a price over the bound", StaffSetup(property, s => s.PriceCents = ServiceRequestLimits.MaxAmountCents + 1)),
        };

        var failures = new List<string>();
        foreach (var (name, setup) in cases)
        {
            var error = await RefusedAsync(setup);
            if (error.SqlState != PostgresErrorCodes.CheckViolation || error.ConstraintName != CleanerCheck)
                failures.Add($"{name}: {error.SqlState} {error.ConstraintName}");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Data rule: the bounds are the ones of a service request and of the catalog, both ends in: a price of 1 cent and of 10,000,000, a duration of 5 and of 1,440 minutes are saved; 4 and 1,441
    /// minutes are refused by the check of the duration. A member of the staff with no price is saved (the price is optional for staff).
    /// </summary>
    [PostgresFact]
    public async Task Setup_ThePriceAndTheDuration_AreInsideTheBoundsOfARequest_Test()
    {
        await SavedAsync(
            SupplierSetup(_world.Granted, s =>
            {
                s.PriceCents = 1;
                s.DurationMinutes = SupplierServiceCatalogLimits.MinDurationMinutes;
            }),
            StaffSetup(_world.Hidden, s =>
            {
                s.PriceCents = ServiceRequestLimits.MaxAmountCents;
                s.DurationMinutes = SupplierServiceCatalogLimits.MaxDurationMinutes;
            }));
        await using (var db = _database!.CreateContext())
        {
            var stored = await db.PropertyCleaningSetups.AsNoTracking().ToListAsync();
            Assert.Equal(2, stored.Count);
        }

        foreach (var minutes in new[] { SupplierServiceCatalogLimits.MinDurationMinutes - 1, SupplierServiceCatalogLimits.MaxDurationMinutes + 1 })
        {
            var error = await RefusedAsync(StaffSetup(_world.OtherOrgProperty, s =>
            {
                s.StaffUserId = "auth0|someone-of-the-other-org";
                s.DurationMinutes = minutes;
            }));

            Assert.Equal((PostgresErrorCodes.CheckViolation, DurationCheck), (error.SqlState, error.ConstraintName));
        }
    }

    /// <summary>Data rule: a property has one setup. A second row for the same property is refused by the unique index, whoever writes it.</summary>
    [PostgresFact]
    public async Task Setup_OnePerProperty_Test()
    {
        await SavedAsync(SupplierSetup(_world.Granted));

        var error = await RefusedAsync(StaffSetup(_world.Granted));

        Assert.Equal((PostgresErrorCodes.UniqueViolation, PropertyCleaningSetup.PropertyIndexName), (error.SqlState, error.ConstraintName));
    }

    /// <summary>Data rule: a stay has one automatic cleaning, the key of the idempotency of the automation: a second row of the same org and booking is refused by the unique index.</summary>
    [PostgresFact]
    public async Task StayCleaning_OnePerStay_Test()
    {
        Guid bookingId;
        await using (var db = _database!.CreateContext())
            bookingId = (await db.Bookings.AsNoTracking().FirstAsync(b => b.PropertyId == _world.Granted.Id)).Id;

        StayCleaning Key() => new()
        {
            OrgId = _world.OrgId,
            BookingId = bookingId,
            PropertyId = _world.Granted.Id,
            CleanerKind = PropertyCleanerKind.Supplier,
            ScheduledStartUtc = new DateTime(2026, 10, 13, 8, 0, 0, DateTimeKind.Utc),
        };

        await SavedAsync(Key());
        var error = await RefusedAsync(Key());

        Assert.Equal((PostgresErrorCodes.UniqueViolation, StayCleaning.BookingIndexName), (error.SqlState, error.ConstraintName));
    }
}
