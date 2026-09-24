using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Migrations;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SU-07 migration (<see cref="AddServiceRequestRentalContext"/>) on real PostgreSQL: applies every migration up to the
/// one before it, stores short-rent requests the way the web created them before SU-07 (no <c>BookingId</c>), then
/// migrates. Every existing request is short-rent; a request is tied to a stay only when its creation day (Europe/Rome)
/// falls within exactly one non-cancelled stay of its property; otherwise it stays on the property.
/// </summary>
public class AddServiceRequestRentalContextPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Migration_RequestsWithoutStay_AreShortRentAndTiedOnlyToTheirSingleStay()
    {
        var s = new Seed();
        await using (var db = _database!.CreateContext())
        {
            db.GetService<IMigrator>().Migrate(PreviousMigration(db));
            await SeedPropertiesAndStaysAsync(db, s);
            await InsertPreSu07RequestsAsync(db, s);
            await db.Database.MigrateAsync();
        }

        await using var after = _database.CreateContext();
        var requests = await after.ServiceRequests.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(r => r.Id);

        Assert.All(requests.Values, r => Assert.Equal(ServiceRequestRentalContext.ShortRent, r.RentalContext));
        Assert.Equal(s.Stay1, requests[s.DuringStay1].BookingId);
        // 2026-06-04T22:30Z is already 5 June in Rome (CEST): the stay starting on the 5th, not the one ending on the 4th.
        Assert.Equal(s.Stay2, requests[s.JustAfterMidnightInRome].BookingId);
        Assert.Null(requests[s.OnTurnoverDay].BookingId);
        Assert.Null(requests[s.BetweenStays].BookingId);
        Assert.Null(requests[s.DuringCancelledStayOnly].BookingId);
        Assert.Null(requests[s.OnPropertyWithoutStay].BookingId);
        Assert.Equal(s.Stay3, requests[s.AlreadyLinked].BookingId);
        Assert.All(requests.Values, r => Assert.Equal(Seed.UpdatedAt, r.UpdatedAt));
        Assert.Empty(await after.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task LinkToStaysSql_RunTwice_ChangesNothingTheSecondTime()
    {
        var s = new Seed();
        await using (var db = _database!.CreateContext())
        {
            db.GetService<IMigrator>().Migrate(PreviousMigration(db));
            await SeedPropertiesAndStaysAsync(db, s);
            await InsertPreSu07RequestsAsync(db, s);
            await db.Database.MigrateAsync();
        }

        await using var again = _database.CreateContext();
        var before = await SnapshotAsync(again);
        await again.Database.ExecuteSqlRawAsync(AddServiceRequestRentalContext.LinkToStaysSql);

        Assert.Equal(before, await SnapshotAsync(again));
    }

    private sealed class Seed
    {
        public static readonly DateTime UpdatedAt = new(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc);

        public Guid Org { get; } = Guid.NewGuid();
        public Guid PropertyA { get; } = Guid.NewGuid();
        public Guid PropertyB { get; } = Guid.NewGuid();
        public Guid Guest { get; } = Guid.NewGuid();

        /// <summary>1–4 June (Confirmed).</summary>
        public Guid Stay1 { get; } = Guid.NewGuid();

        /// <summary>5–8 June (CheckedOut).</summary>
        public Guid Stay2 { get; } = Guid.NewGuid();

        /// <summary>8–10 June (Confirmed): 8 June is a turnover day with <see cref="Stay2"/>.</summary>
        public Guid Stay3 { get; } = Guid.NewGuid();

        /// <summary>12–14 June, cancelled.</summary>
        public Guid CancelledStay { get; } = Guid.NewGuid();

        public Guid DuringStay1 { get; } = Guid.NewGuid();
        public Guid JustAfterMidnightInRome { get; } = Guid.NewGuid();
        public Guid OnTurnoverDay { get; } = Guid.NewGuid();
        public Guid BetweenStays { get; } = Guid.NewGuid();
        public Guid DuringCancelledStayOnly { get; } = Guid.NewGuid();
        public Guid OnPropertyWithoutStay { get; } = Guid.NewGuid();
        public Guid AlreadyLinked { get; } = Guid.NewGuid();
    }

    /// <summary>
    /// Org, two properties (A with four stays, B with none), a guest, with raw SQL on the schema right before the
    /// migration: EF would write the columns that later migrations add.
    /// </summary>
    private static async Task SeedPropertiesAndStaysAsync(AppDbContext db, Seed s)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Orgs" ("Id", "Name", "Slug", "PlanTier", "DisplayName", "ContactEmail", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES ({s.Org}, 'Host', {"su07-h-" + s.Org.ToString("N")}, 0, 'Host', '', true, now(), now());

            INSERT INTO "Properties" (
                "Id", "OwnerId", "OrgId", "Name", "Description", "Address", "City", "PostalCode",
                "Latitude", "Longitude", "Bedrooms", "Bathrooms", "MaxGuests", "NightlyRate", "CleaningFee", "DamageDeposit",
                "Amenities", "PhotoUrls", "HouseRules", "Timezone", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES
              ({s.PropertyA}, 'auth0|su07-migration', {s.Org}, 'Casa A', '', 'Via A 1', 'Roma', '00100',
               0, 0, 1, 1, 2, 100, 0, 0, ARRAY[]::integer[], ARRAY[]::text[], '', 'Europe/Rome', true, now(), now()),
              ({s.PropertyB}, 'auth0|su07-migration', {s.Org}, 'Casa B', '', 'Via B 1', 'Roma', '00100',
               0, 0, 1, 1, 2, 100, 0, 0, ARRAY[]::integer[], ARRAY[]::text[], '', 'Europe/Rome', true, now(), now());

            INSERT INTO "Guests" (
                "Id", "OrgId", "FirstName", "LastName", "Email", "PhoneNumber", "Address", "City", "PostalCode", "Country",
                "PlaceOfBirth", "Nationality", "DocumentNumber", "DocumentIssuingCountry", "ConsentIpAddress",
                "ErasureRequested", "Notes", "ConsentVersion", "MarketingConsent", "DataRetentionUntil",
                "DataProcessingPurpose", "IsDeleted", "DeletionReason", "CreatedAt", "UpdatedAt")
            VALUES ({s.Guest}, {s.Org}, 'Anna', 'Ospite', 'anna@example.com', '', '', '', '', 'IT',
                    '', '', '', '', '', false, '', '', false, now() + interval '7 years', '', false, '', now(), now());
            """);

        async Task StayAsync(Guid id, int fromDay, int toDay, BookingStatus status) =>
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "Bookings" (
                    "Id", "PropertyId", "OrgId", "GuestId", "CheckInDate", "CheckOutDate", "NumberOfGuests", "Status", "Source",
                    "ExternalId", "BasePrice", "TouristTax", "TotalPrice", "TouristTaxAmount", "NumberOfAdults", "NumberOfChildren",
                    "SpecialRequests", "CreatedAt", "UpdatedAt")
                VALUES ({id}, {s.PropertyA}, {s.Org}, {s.Guest},
                        {new DateTime(2026, 6, fromDay, 0, 0, 0, DateTimeKind.Utc)}, {new DateTime(2026, 6, toDay, 0, 0, 0, DateTimeKind.Utc)},
                        2, {(int)status}, 0, '', 100, 0, 100, 0, 2, 0, '', now(), now());
                """);

        await StayAsync(s.Stay1, 1, 4, BookingStatus.Confirmed);
        await StayAsync(s.Stay2, 5, 8, BookingStatus.CheckedOut);
        await StayAsync(s.Stay3, 8, 10, BookingStatus.Confirmed);
        await StayAsync(s.CancelledStay, 12, 14, BookingStatus.Cancelled);
    }

    /// <summary>Requests as the web created them before SU-07 (schema right before the migration: no RentalContext).</summary>
    private static async Task InsertPreSu07RequestsAsync(AppDbContext db, Seed s)
    {
        var supplier = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Orgs" ("Id", "Name", "Slug", "PlanTier", "DisplayName", "ContactEmail", "IsActive", "CreatedAt", "UpdatedAt", "OrgType")
            VALUES ({supplier}, 'Fornitore', {"su07-s-" + supplier.ToString("N")}, 0, 'Fornitore', '', true, now(), now(), 1);
            """);

        async Task InsertAsync(Guid id, Guid propertyId, Guid? bookingId, DateTime createdAt) =>
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "ServiceRequests" ("Id", "OrgId", "BookingId", "PropertyId", "SupplierOrgId", "Category", "Urgency", "Notes", "Status", "ChargeToGuest", "CreatedAt", "UpdatedAt")
                VALUES ({id}, {s.Org}, {bookingId}, {propertyId}, {supplier}, 'cleaning', 0, '', 0, false, {createdAt}, {Seed.UpdatedAt});
                """);

        static DateTime At(int day, int hour, int minute = 0) => new(2026, 6, day, hour, minute, 0, DateTimeKind.Utc);

        await InsertAsync(s.DuringStay1, s.PropertyA, null, At(2, 10));
        await InsertAsync(s.JustAfterMidnightInRome, s.PropertyA, null, At(4, 22, 30));
        await InsertAsync(s.OnTurnoverDay, s.PropertyA, null, At(8, 9));
        await InsertAsync(s.BetweenStays, s.PropertyA, null, At(11, 9));
        await InsertAsync(s.DuringCancelledStayOnly, s.PropertyA, null, At(13, 9));
        await InsertAsync(s.OnPropertyWithoutStay, s.PropertyB, null, At(2, 10));
        await InsertAsync(s.AlreadyLinked, s.PropertyA, s.Stay3, At(2, 10));
    }

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_AddServiceRequestRentalContext", StringComparison.Ordinal));
        Assert.True(index > 0, "AddServiceRequestRentalContext migration not found.");
        return all[index - 1];
    }

    private static async Task<string> SnapshotAsync(AppDbContext db)
    {
        var rows = await db.ServiceRequests.IgnoreQueryFilters().AsNoTracking()
            .OrderBy(r => r.Id)
            .Select(r => r.Id + ":" + r.BookingId + ":" + (int)r.RentalContext + ":" + r.UpdatedAt)
            .ToListAsync();
        return string.Join("|", rows);
    }
}
