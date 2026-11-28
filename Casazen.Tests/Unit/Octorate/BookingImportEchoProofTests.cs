using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Casazen.Tests.Unit.Octorate.BookingImportTestKit;

namespace Casazen.Tests.Unit.Octorate;

public class BookingImportEchoProofTests
{
    private const string Code = "K7Q2M9X4TB";

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

        var again = await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);
        Assert.Equal((0, 0), (again.Created, again.Updated));
        await using var last = h.NewDb();
        Assert.Single(await last.Bookings.AsNoTracking().Where(b => b.OrgId == OrgA).ToListAsync());
    }

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

        Assert.Equal(1, result.Created);
        await using var read = h.NewDb();
        var bookings = await read.Bookings.AsNoTracking().Where(b => b.OrgId == OrgA).ToListAsync();
        Assert.Equal(3, bookings.Count);
        Assert.Null(bookings.Single(b => b.Id == own).OctorateReservationId);
        Assert.Null(bookings.Single(b => b.Id == elsewhere).OctorateReservationId);
        Assert.Equal("9002", bookings.Single(b => b.Id != own && b.Id != elsewhere).OctorateReservationId);
    }

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
