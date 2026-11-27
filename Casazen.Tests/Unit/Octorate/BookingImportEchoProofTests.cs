using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Casazen.Tests.Unit.Octorate.BookingImportTestKit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The import meets the echo of a reservation CasaZen created itself (E08/F01/US01): the reconciliation, a webhook or a lookup that found
/// nothing conclusive can read the new reservation before the push saved its id. The rule that costs if it fails: that echo is the booking
/// CasaZen already has, not a second one on the same dates (which would also make the push, saving the same id, hit the unique index).
/// </summary>
public class BookingImportEchoProofTests
{
    private const string Code = "K7Q2M9X4TB";

    /// <summary>
    /// PROOF TEST: data rule. A booking the host entered was pushed, and its reservation is read by the import before the push saved the id:
    /// the reservation carries the booking code as <c>refer</c>, so the booking is linked to it (id and version) and nothing else changes; no
    /// second booking is made, no event is recorded, and reading it again changes nothing.
    /// </summary>
    [Fact]
    public async Task BookingImport_EchoOfAReservationCasaZenCreated_IsLinkedAndNeverImportedTwice_Test()
    {
        var h = new BookingImportHarness();
        var own = await SeedOwnBookingAsync(h, Code);
        var echo = Res("9001") with { Refer = Code, ChannelId = "270", ChannelName = "Direct Reservations" };
        h.Serve(echo);
        await using var db = h.NewDb();

        var first = await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);

        Assert.Equal((0, 0), (first.Created, first.Updated));
        await using (var read = h.NewDb())
        {
            var only = Assert.Single(await read.Bookings.AsNoTracking().Where(b => b.OrgId == OrgA).ToListAsync());
            Assert.Equal(own, only.Id);
            Assert.Equal("9001", only.OctorateReservationId);
            Assert.Equal(FirstVersion, only.OctorateUpdatedAt);
            Assert.Equal(BookingSource.Manual, only.Source);
            Assert.Equal(Code, only.BookingCode);
        }

        Assert.Empty(await h.ReadEventsAsync());

        // Read again: found by its id now, the same version, nothing to do.
        var again = await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);
        Assert.Equal((0, 0), (again.Created, again.Updated));
        await using var last = h.NewDb();
        Assert.Single(await last.Bookings.AsNoTracking().Where(b => b.OrgId == OrgA).ToListAsync());
    }

    /// <summary>
    /// The match is by the reference of the reservation, in the same org and property, and only for a booking with no Octorate id yet: a
    /// reservation with another reference is a booking of its own, and the booking that does not match is left as it was.
    /// </summary>
    [Fact]
    public async Task BookingImport_AReservationOfAnotherReference_IsImportedAsItsOwnBooking_Test()
    {
        var h = new BookingImportHarness();
        var own = await SeedOwnBookingAsync(h, Code);
        var other = await h.AddListingAsync("585499", "956999");
        var elsewhere = await SeedOwnBookingAsync(h, "B0THERPR01", propertyId: other);
        h.Serve(Res("9002") with { Refer = "B0THERPR01", ChannelId = "270", ChannelName = "Direct Reservations" });
        await using var db = h.NewDb();

        var result = await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);

        // The reference belongs to a booking of another property: it is not this property's echo, so the reservation is imported on its own.
        Assert.Equal(1, result.Created);
        await using var read = h.NewDb();
        var bookings = await read.Bookings.AsNoTracking().Where(b => b.OrgId == OrgA).ToListAsync();
        Assert.Equal(3, bookings.Count);
        Assert.Null(bookings.Single(b => b.Id == own).OctorateReservationId);
        Assert.Null(bookings.Single(b => b.Id == elsewhere).OctorateReservationId);
        Assert.Equal("9002", bookings.Single(b => b.Id != own && b.Id != elsewhere).OctorateReservationId);
    }

    /// <summary>A booking the host entered, confirmed, on the property of the harness, with the code that was sent as <c>refer</c> and no Octorate id yet.</summary>
    private static async Task<Guid> SeedOwnBookingAsync(BookingImportHarness h, string code, Guid? propertyId = null)
    {
        await using var db = h.NewDb();
        var guest = new Guest { OrgId = OrgA, FirstName = "Giulia", LastName = "Rossi", Email = $"{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            OrgId = OrgA,
            PropertyId = propertyId ?? h.PropertyId,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Manual,
            BasePrice = 300m,
            TouristTax = 6m,
            TotalPrice = 306m,
            BookingCode = code,
            UpdatedAt = FirstVersion,
        };
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking.Id;
    }
}
