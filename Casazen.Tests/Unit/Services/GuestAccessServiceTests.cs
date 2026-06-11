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
    public async Task IsGuestAccessibleAsync_GuestWithBookingInOrg_ReturnsTrue()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var guestId = Guid.NewGuid();
        db.Bookings.Add(new Booking
        {
            PropertyId = Guid.NewGuid(),
            GuestId = guestId,
            OrgId = orgId,
            CheckInDate = DateTime.UtcNow,
            CheckOutDate = DateTime.UtcNow.AddDays(2),
            Status = BookingStatus.Confirmed,
        });
        await db.SaveChangesAsync();

        var service = new GuestAccessService(db);
        var result = await service.IsGuestAccessibleAsync(guestId, orgId);

        Assert.True(result);
    }

    [Fact]
    public async Task IsGuestAccessibleAsync_GuestOnlyInOtherOrg_ReturnsFalse()
    {
        await using var db = NewDb();
        var guestId = Guid.NewGuid();
        db.Bookings.Add(new Booking
        {
            PropertyId = Guid.NewGuid(),
            GuestId = guestId,
            OrgId = Guid.NewGuid(),
            CheckInDate = DateTime.UtcNow,
            CheckOutDate = DateTime.UtcNow.AddDays(2),
            Status = BookingStatus.Confirmed,
        });
        await db.SaveChangesAsync();

        var service = new GuestAccessService(db);
        var result = await service.IsGuestAccessibleAsync(guestId, Guid.NewGuid());

        Assert.False(result);
    }

    [Fact]
    public async Task IsGuestAccessibleAsync_GuestWithNoBookings_ReturnsFalse()
    {
        await using var db = NewDb();
        var service = new GuestAccessService(db);
        var result = await service.IsGuestAccessibleAsync(Guid.NewGuid(), Guid.NewGuid());
        Assert.False(result);
    }
}
