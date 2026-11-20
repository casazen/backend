using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Models;
using Casazen.Core.Options;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The rights of the guest over the data of the Ticino police file (E30/F01/US01/T01, NF-132, art. 15 and 17 GDPR): the plate of the car of a stay and the home
/// address each guest of the stay gave at the check-in are the guest's data. They are in the export of the guest's data, and they go at the erasure with the
/// guests' data of the stay. On EF InMemory, the real services.
/// </summary>
public class SwissGuestDataGdprTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTime Now = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// PROOF TEST: privacy rule (art. 15). The export of a guest has, on each stay, the plate of the car as the booking holds it (none for a stay with no plate),
    /// and on each guest of those stays the home address recorded at the check-in; the plate and the home of another guest's stay are not in it.
    /// </summary>
    [Fact]
    public async Task GdprExport_HasThePlateOfEachStay_AndTheHomeOfEachGuestOfTheStays_AndNothingOfAnotherGuest_Test()
    {
        var store = CrmTestWorld.NewStore();
        var orgId = Guid.NewGuid();
        Guid guestId;
        await using (var seed = CrmTestWorld.Open(store))
        {
            var property = NewProperty(orgId);
            seed.Properties.Add(property);
            var mario = CrmTestWorld.AddGuest(seed, orgId, "Mario", "Rossi", "mario.rossi@example.com");
            var anna = CrmTestWorld.AddGuest(seed, orgId, "Anna", "Neri", "anna.neri@example.com");
            CrmTestWorld.AddBooking(seed, mario, property.Id, BookingStatus.CheckedOut, BookingSource.Direct, -60, 3, 300m);
            var stay = CrmTestWorld.AddBooking(seed, mario, property.Id, BookingStatus.CheckedOut, BookingSource.Direct, -20, 3, 300m);
            stay.VehiclePlate = "TI123456";
            var others = CrmTestWorld.AddBooking(seed, anna, property.Id, BookingStatus.CheckedOut, BookingSource.Direct, -10, 2, 200m);
            others.VehiclePlate = "MI999999";
            seed.StayGuests.Add(GuestOf(stay, 0, mario.Id, "Mario", "Via Nassa 15", "6900", "Lugano"));
            seed.StayGuests.Add(GuestOf(stay, 1, null, "Giulia", "Via Nassa 15", "6900", "Lugano"));
            seed.StayGuests.Add(GuestOf(others, 0, anna.Id, "Anna", "Rue du Rhone 1", "1204", "Geneve"));
            await seed.SaveChangesAsync(Ct);
            guestId = mario.Id;
        }

        GuestDataExport export;
        await using (var db = CrmTestWorld.Open(store))
            export = await Service(db).ExportGuestDataAsync(orgId, guestId, new HostScope(orgId), false, "auth0|host", Ct);

        // The stays come oldest first: the one with no plate, then the one with it.
        Assert.Equal(2, export.Bookings.Count);
        Assert.Null(export.Bookings[0].VehiclePlate);
        Assert.Equal("TI123456", export.Bookings[1].VehiclePlate);

        // The two guests of the stay with their home; the guest of the other stay is not in the file.
        Assert.Equal(2, export.StayGuests.Count);
        Assert.Equal(("Via Nassa 15", "6900", "Lugano"), HomeOf(export.StayGuests[0]));
        Assert.Equal(("Via Nassa 15", "6900", "Lugano"), HomeOf(export.StayGuests[1]));
        var json = JsonSerializer.Serialize(export);
        Assert.DoesNotContain("MI999999", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Rue du Rhone", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// PROOF TEST: privacy rule (art. 17). The erasure of a guest clears the plate of the guest's stays and the home of every guest of those stays, touches no
    /// stay of another guest, and what the export then gives of the erased guest holds none of them.
    /// </summary>
    [Fact]
    public async Task GuestErasure_ClearsThePlateAndTheHome_KeepsOthersAndTheExportHasNone_Test()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"eraser-swiss-{Guid.NewGuid()}")
            .Options);
        var orgId = Guid.NewGuid();
        var property = NewProperty(orgId);
        var erased = new Guest { OrgId = orgId, FirstName = "Luigi", LastName = "Verdi", Email = "luigi@example.com" };
        var other = new Guest { OrgId = orgId, FirstName = "Anna", LastName = "Neri", Email = "anna@example.com" };
        db.Properties.Add(property);
        db.Guests.AddRange(erased, other);
        var stay = StayOf(erased, property.Id, "TI123456");
        var othersStay = StayOf(other, property.Id, "MI999999");
        db.Bookings.AddRange(stay, othersStay);
        db.StayGuests.AddRange(
            GuestOf(stay, 0, erased.Id, "Luigi", "Via Nassa 15", "6900", "Lugano"),
            GuestOf(stay, 1, null, "Giulia", "Via Nassa 15", "6900", "Lugano"),
            GuestOf(othersStay, 0, other.Id, "Anna", "Rue du Rhone 1", "1204", "Geneve"));
        await db.SaveChangesAsync(Ct);
        var eraser = new GuestDataEraser(db, Mock.Of<IFileStorage>(), NullLogger<GuestDataEraser>.Instance);

        var outcome = await eraser.AnonymizeAsync(erased, Now, Ct);
        await db.SaveChangesAsync(Ct);

        Assert.True(outcome.Changed);
        db.ChangeTracker.Clear();
        Assert.Null((await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == stay.Id, Ct)).VehiclePlate);
        var cleared = await db.StayGuests.AsNoTracking().Where(s => s.BookingId == stay.Id).ToListAsync(Ct);
        Assert.Equal(2, cleared.Count);
        Assert.All(cleared, row => Assert.Equal(((string?)null, (string?)null, (string?)null), HomeOf(row)));

        // The stay of another guest keeps its plate and its home.
        Assert.Equal("MI999999", (await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == othersStay.Id, Ct)).VehiclePlate);
        var kept = await db.StayGuests.AsNoTracking().SingleAsync(s => s.BookingId == othersStay.Id, Ct);
        Assert.Equal(("Rue du Rhone 1", "1204", "Geneve"), HomeOf(kept));

        // The export of the erased guest gives none of them either: what the export lists is what the erasure clears.
        var export = await Service(db).ExportGuestDataAsync(orgId, erased.Id, new HostScope(orgId), false, "auth0|host", Ct);
        Assert.All(export.Bookings, booking => Assert.Null(booking.VehiclePlate));
        Assert.All(export.StayGuests, row => Assert.Equal(((string?)null, (string?)null, (string?)null), HomeOf(row)));
        Assert.DoesNotContain("Via Nassa", JsonSerializer.Serialize(export), StringComparison.Ordinal);
    }

    private static (string?, string?, string?) HomeOf(GuestExportStayGuest row) => (row.ResidenceStreet, row.ResidencePostalCode, row.ResidenceCityName);

    private static (string?, string?, string?) HomeOf(StayGuest row) => (row.ResidenceStreet, row.ResidencePostalCode, row.ResidenceCityName);

    /// <summary>A guest of a stay with the home the check-in recorded (added to nothing, not saved).</summary>
    private static StayGuest GuestOf(Booking stay, int position, Guid? guestId, string firstName, string street, string postalCode, string city) => new()
    {
        BookingId = stay.Id,
        OrgId = stay.OrgId,
        Position = position,
        GuestId = guestId,
        FirstName = firstName,
        LastName = "Test",
        ResidenceStreet = street,
        ResidencePostalCode = postalCode,
        ResidenceCityName = city,
    };

    /// <summary>A confirmed stay at the property with a plate and nothing else the erasure would clear.</summary>
    private static Booking StayOf(Guest guest, Guid propertyId, string plate) => new()
    {
        OrgId = guest.OrgId,
        PropertyId = propertyId,
        GuestId = guest.Id,
        Status = BookingStatus.Confirmed,
        Source = BookingSource.Direct,
        BookingCode = Guid.NewGuid().ToString("N")[..10],
        CheckInDate = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        CheckOutDate = new DateTime(2026, 12, 4, 0, 0, 0, DateTimeKind.Utc),
        NumberOfGuests = 2,
        TotalPrice = 300m,
        VehiclePlate = plate,
    };

    private static GdprService Service(AppDbContext db)
    {
        // No stay is open: the erasure is not refused (art. 17.3.b).
        var repository = new Mock<IGuestRepository>();
        repository
            .Setup(r => r.GetUsageAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuestUsage(HasReferences: true, HasOpenBookings: false));

        return new GdprService(
            db,
            repository.Object,
            new GuestDataEraser(db, Mock.Of<IFileStorage>(), NullLogger<GuestDataEraser>.Instance),
            Options.Create(new GdprOptions()),
            CrmTestWorld.Clock(),
            NullLogger<GdprService>.Instance);
    }

    private static Property NewProperty(Guid orgId) => new()
    {
        OrgId = orgId,
        OwnerId = "auth0|owner",
        Name = "Casa del Lago",
        Address = "Via del Lago 1",
        City = "Como",
        MaxGuests = 4,
        Timezone = "Europe/Rome",
        Currency = "EUR",
    };
}
