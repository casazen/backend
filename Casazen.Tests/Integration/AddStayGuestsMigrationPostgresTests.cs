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
/// CO-12: on a real PostgreSQL database, the <c>AddStayGuestsAndAlloggiatiCodeTables</c> migration gives every existing
/// booking one guest of the stay from its booker: single guest, or head of family when more guests are declared, linked
/// to the booker; sex and document kind "Other" become missing values (they have no Alloggiati value). The code tables
/// start empty.
/// </summary>
public class AddStayGuestsMigrationPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task AddStayGuests_ExistingBookings_GetTheirBookerAsSingleGuestOrHeadOfFamily()
    {
        await using var db = _database!.CreateContext();
        db.GetService<IMigrator>().Migrate(PreviousMigration(db));
        var seed = await SeedPreviousStateAsync(db);

        await db.Database.MigrateAsync();

        db.ChangeTracker.Clear();
        var rows = await db.StayGuests.AsNoTracking().ToDictionaryAsync(s => s.BookingId);
        Assert.Equal(3, rows.Count);

        var single = rows[seed.SingleBooking];
        Assert.Equal(
            (0, StayGuestType.SingleGuest, seed.CompleteGuest, seed.Org),
            (single.Position, single.Type, single.GuestId, single.OrgId));
        Assert.Equal(("Giulia", "Bianchi", Gender.Female), (single.FirstName, single.LastName, single.Gender));
        Assert.Equal(new DateTime(1992, 3, 8, 0, 0, 0, DateTimeKind.Utc), single.DateOfBirth);
        // The old free-text place of birth is kept, but whether it is in Italy is unknown: to complete.
        Assert.Null(single.BornInItaly);
        Assert.Equal("Firenze", single.BirthComuneName);
        Assert.Equal("Italiana", single.CitizenshipName);
        Assert.Equal((GuestDocumentType.IdentityCard, "CA12345AB", "Italia"), (single.DocumentType, single.DocumentNumber, single.DocumentIssuePlaceName));
        Assert.Null(single.BirthComuneCode);
        Assert.Null(single.CitizenshipCode);

        var family = rows[seed.FamilyBooking];
        Assert.Equal(StayGuestType.HeadOfFamily, family.Type);
        Assert.Equal(seed.OtherValuesGuest, family.GuestId);
        Assert.Null(family.Gender); // Gender.Other: no Alloggiati value
        Assert.Null(family.DocumentType); // GuestDocumentType.Other: no Alloggiati value

        Assert.Equal(StayGuestType.SingleGuest, rows[seed.NoGuestsDeclaredBooking].Type);

        Assert.Empty(await db.AlloggiatiCodeEntries.ToListAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        // Idempotent: running the backfill again adds nothing.
        await db.Database.ExecuteSqlRawAsync(AddStayGuestsAndAlloggiatiCodeTables.BackfillStayGuestsSql);
        Assert.Equal(3, await db.StayGuests.CountAsync());
    }

    private sealed record Seed(
        Guid Org,
        Guid CompleteGuest,
        Guid OtherValuesGuest,
        Guid SingleBooking,
        Guid FamilyBooking,
        Guid NoGuestsDeclaredBooking);

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_AddStayGuestsAndAlloggiatiCodeTables", StringComparison.Ordinal));
        Assert.True(index > 0);
        return all[index - 1];
    }

    /// <summary>
    /// Orgs and Guests are written through the model (same schema before and after the migration). Properties and
    /// Bookings are inserted with SQL, only with the columns that exist before the migration: later migrations add columns
    /// to them (e.g. BK-06 AddOnSiteRequestApproval, CO-18 AddPropertyTaxpayerFiscalCode) that the model would write.
    /// </summary>
    private static async Task<Seed> SeedPreviousStateAsync(AppDbContext db)
    {
        var org = new OrgEntity
        {
            Name = "Org CO-12",
            Slug = $"co12-{Guid.NewGuid():N}",
            DisplayName = "Org CO-12",
            ContactEmail = "co12@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        };
        var property = new Property
        {
            OwnerId = "auth0|co12",
            OrgId = org.Id,
            Name = "Casa CO-12",
            Description = "Casa",
            Address = "Via Roma 1",
            City = "Roma",
            PostalCode = "00100",
            MaxGuests = 4,
            NightlyRate = 100m,
            IsActive = true,
        };
        var complete = new Guest
        {
            OrgId = org.Id,
            FirstName = "Giulia",
            LastName = "Bianchi",
            Email = "giulia@example.com",
            Gender = Gender.Female,
            DateOfBirth = new DateTime(1992, 3, 8, 0, 0, 0, DateTimeKind.Utc),
            PlaceOfBirth = "Firenze",
            Nationality = "Italiana",
            DocumentType = GuestDocumentType.IdentityCard,
            DocumentNumber = "CA12345AB",
            DocumentIssuingCountry = "Italia",
        };
        var otherValues = new Guest
        {
            OrgId = org.Id,
            FirstName = "Alex",
            LastName = "Neri",
            Email = "alex@example.com",
            Gender = Gender.Other,
            DocumentType = GuestDocumentType.Other,
            DocumentNumber = "X1",
        };

        db.Orgs.Add(org);
        db.Guests.AddRange(complete, otherValues);
        await db.SaveChangesAsync();
        await PreviousSchemaSeed.InsertPropertyAsync(db, property);

        async Task<Guid> InsertBookingAsync(Guest guest, int guests)
        {
            var id = Guid.NewGuid();
            var checkIn = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
            var checkOut = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);
            var now = DateTime.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Bookings" ("Id", "PropertyId", "OrgId", "GuestId", "CheckInDate", "CheckOutDate",
                    "NumberOfGuests", "NumberOfAdults", "NumberOfChildren", "Status", "Source", "ExternalId", "BasePrice",
                    "TouristTax", "TotalPrice", "TouristTaxAmount", "SpecialRequests", "PaymentOption", "CreatedAt", "UpdatedAt")
                VALUES ({id}, {property.Id}, {org.Id}, {guest.Id}, {checkIn}, {checkOut},
                    {guests}, {guests}, 0, {(int)BookingStatus.Confirmed}, {(int)BookingSource.Direct}, '', 0,
                    0, 0, 0, '', {(int)PaymentOption.Immediate}, {now}, {now})
                """);
            return id;
        }

        var single = await InsertBookingAsync(complete, 1);
        var family = await InsertBookingAsync(otherValues, 3);
        var noneDeclared = await InsertBookingAsync(complete, 0);

        return new Seed(org.Id, complete.Id, otherValues.Id, single, family, noneDeclared);
    }
}
