using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Bookings;
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
/// The rights of the guest over where the guest came from (E27/F01/US01/T01, NF-123, art. 15 and 17 GDPR): the four attribution values of a booking are the guest's
/// data, because they are tied to the guest's stay. They are in the export of the guest's data, and they go at the erasure with the special requests of the stay. On
/// EF InMemory, the real services.
/// </summary>
public class BookingAttributionGdprTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTime Now = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// PROOF TEST: privacy rule (art. 15). The export of a guest has, on each stay, where the guest came from: the source, the medium, the campaign and the host name
    /// as the booking holds them; a stay with no link has none; the reservation of the booking engine of the agency goes by a neutral word, so the file names no
    /// supplier; the attribution of another guest's stay is not in it.
    /// </summary>
    [Fact]
    public async Task GdprExport_HasWhereTheGuestCameFromOnEachStay_AndNamesNoSupplier_Test()
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
            Attribute(
                CrmTestWorld.AddBooking(seed, mario, property.Id, BookingStatus.CheckedOut, BookingSource.Direct, -60, 3, 300m),
                "agency-site",
                "link",
                "estate-2026",
                "www.agenzia-test.example.com");
            Attribute(
                CrmTestWorld.AddBooking(seed, mario, property.Id, BookingStatus.CheckedOut, BookingSource.Direct, -40, 3, 300m),
                BookingAttribution.OctorateBookingEngineSource);
            CrmTestWorld.AddBooking(seed, mario, property.Id, BookingStatus.CheckedOut, BookingSource.Direct, -20, 3, 300m);
            Attribute(
                CrmTestWorld.AddBooking(seed, anna, property.Id, BookingStatus.CheckedOut, BookingSource.Direct, -10, 2, 200m),
                "agency-site",
                "qr",
                "campagna-anna",
                "www.altro.example.com");
            await seed.SaveChangesAsync(Ct);
            guestId = mario.Id;
        }

        GuestDataExport export;
        await using (var db = CrmTestWorld.Open(store))
            export = await Service(db).ExportGuestDataAsync(orgId, guestId, new HostScope(orgId), false, "auth0|host", Ct);

        // The stays come oldest first: the link of the agency, the booking engine of the agency, the booking site with no link.
        Assert.Equal(3, export.Bookings.Count);
        Assert.Equal(
            ("agency-site", "link", "estate-2026", "www.agenzia-test.example.com"),
            ValuesOf(export.Bookings[0]));
        Assert.Equal(
            (BookingAttribution.AgencyBookingEngineExportName, (string?)null, (string?)null, (string?)null),
            ValuesOf(export.Bookings[1]));
        Assert.Equal(((string?)null, (string?)null, (string?)null, (string?)null), ValuesOf(export.Bookings[2]));

        // The file names no supplier and holds nothing of another guest.
        var json = JsonSerializer.Serialize(export);
        Assert.DoesNotContain("octorate", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("campagna-anna", json, StringComparison.Ordinal);
        Assert.DoesNotContain("altro.example.com", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// PROOF TEST: privacy rule (art. 17). The erasure of a guest clears where the guest came from on every stay of the guest, also on a stay that holds no special
    /// request and no arrival time, keeps the stay with its source and its amount, touches no stay of another guest, and a second run finds nothing left to clear.
    /// </summary>
    [Fact]
    public async Task GuestErasure_ClearsWhereTheGuestCameFrom_AndKeepsTheStayAndTheStaysOfOthers_Test()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"eraser-attribution-{Guid.NewGuid()}")
            .Options);
        var orgId = Guid.NewGuid();
        var erased = new Guest { OrgId = orgId, FirstName = "Luigi", LastName = "Verdi", Email = "luigi@example.com" };
        var other = new Guest { OrgId = orgId, FirstName = "Anna", LastName = "Neri", Email = "anna@example.com" };
        var link = StayOf(erased, "agency-site", "link", "estate-2026", "www.agenzia-test.example.com");
        var engine = StayOf(erased, BookingAttribution.OctorateBookingEngineSource);
        var others = StayOf(other, "agency-site", "qr", "campagna-anna", "www.altro.example.com");
        db.Guests.AddRange(erased, other);
        db.Bookings.AddRange(link, engine, others);
        await db.SaveChangesAsync();
        var eraser = new GuestDataEraser(db, Mock.Of<IFileStorage>(), NullLogger<GuestDataEraser>.Instance);

        var outcome = await eraser.AnonymizeAsync(erased, Now, Ct);
        await db.SaveChangesAsync();

        Assert.True(outcome.Changed);
        db.ChangeTracker.Clear();
        foreach (var id in new[] { link.Id, engine.Id })
        {
            var stay = await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == id);
            Assert.Equal(((string?)null, (string?)null, (string?)null, (string?)null), ValuesOf(stay));
            Assert.Equal((BookingSource.Direct, 300m), (stay.Source, stay.TotalPrice));
        }

        var untouched = await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == others.Id);
        Assert.Equal(("agency-site", "qr", "campagna-anna", "www.altro.example.com"), ValuesOf(untouched));

        // Idempotent: a second run finds nothing left to clear.
        var again = await eraser.AnonymizeAsync(await db.Guests.SingleAsync(g => g.Id == erased.Id), Now.AddDays(1), Ct);
        Assert.False(again.Changed);
    }

    private static (string?, string?, string?, string?) ValuesOf(GuestExportBooking stay) =>
        (stay.AttributionSource, stay.AttributionMedium, stay.AttributionCampaign, stay.AttributionReferrerHost);

    private static (string?, string?, string?, string?) ValuesOf(Booking stay) =>
        (stay.AttributionSource, stay.AttributionMedium, stay.AttributionCampaign, stay.AttributionReferrerHost);

    private static void Attribute(Booking stay, string source, string? medium = null, string? campaign = null, string? referrerHost = null)
    {
        stay.AttributionSource = source;
        stay.AttributionMedium = medium;
        stay.AttributionCampaign = campaign;
        stay.AttributionReferrerHost = referrerHost;
    }

    /// <summary>A confirmed stay of the booking site with nothing but its attribution to clear: no special request, no arrival time.</summary>
    private static Booking StayOf(Guest guest, string source, string? medium = null, string? campaign = null, string? referrerHost = null)
    {
        var stay = new Booking
        {
            OrgId = guest.OrgId,
            PropertyId = Guid.NewGuid(),
            GuestId = guest.Id,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BookingCode = Guid.NewGuid().ToString("N")[..10],
            CheckInDate = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 12, 4, 0, 0, 0, DateTimeKind.Utc),
            NumberOfGuests = 2,
            TotalPrice = 300m,
        };
        Attribute(stay, source, medium, campaign, referrerHost);
        return stay;
    }

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
