using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class GuestAccessServiceTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"guest-access-{Guid.NewGuid()}")
            .Options);

    [Fact]
    public async Task IsGuestAccessibleAsync_GuestOfOrg_ReturnsTrue()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var guest = new Guest { OrgId = orgId, FirstName = "Mario", LastName = "Rossi", Email = "mario@example.com" };
        db.Guests.Add(guest);
        await db.SaveChangesAsync();

        var result = await new GuestAccessService(db).IsGuestAccessibleAsync(guest.Id, orgId);

        Assert.True(result);
    }

    [Fact]
    public async Task IsGuestAccessibleAsync_GuestOfOtherOrgWithBookingInCallerOrg_ReturnsFalse()
    {
        // The pre-TN-1 rule ("a booking of my org points at the guest") no longer grants access.
        await using var db = NewDb();
        var ownerOrg = Guid.NewGuid();
        var callerOrg = Guid.NewGuid();
        var guest = new Guest { OrgId = ownerOrg, FirstName = "Mario", LastName = "Rossi", Email = "mario@example.com" };
        db.Guests.Add(guest);
        db.Bookings.Add(new Booking
        {
            PropertyId = Guid.NewGuid(),
            GuestId = guest.Id,
            OrgId = callerOrg,
            CheckInDate = DateTime.UtcNow,
            CheckOutDate = DateTime.UtcNow.AddDays(2),
            Status = BookingStatus.Confirmed,
        });
        await db.SaveChangesAsync();

        var result = await new GuestAccessService(db).IsGuestAccessibleAsync(guest.Id, callerOrg);

        Assert.False(result);
    }

    [Fact]
    public async Task IsGuestAccessibleAsync_UnknownGuest_ReturnsFalse()
    {
        await using var db = NewDb();

        var result = await new GuestAccessService(db).IsGuestAccessibleAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.False(result);
    }
}
