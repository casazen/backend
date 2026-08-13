using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

public class CheckoutReminderJobTests
{
    [Fact]
    public async Task SendReminderAsync_CheckedInBooking_SendsReminder()
    {
        await using var context = CreateContext();
        var bookingId = await SeedBookingAsync(context, BookingStatus.CheckedIn);
        var notificationService = new Mock<INotificationService>();
        var job = CreateJob(context, notificationService.Object);

        await job.SendReminderAsync(bookingId);

        notificationService.Verify(s => s.SendCheckoutReminderAsync(bookingId), Times.Once);
    }

    [Theory]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.CheckedOut)]
    [InlineData(BookingStatus.Pending)]
    [InlineData(BookingStatus.Confirmed)]
    public async Task SendReminderAsync_InactiveBooking_DoesNotSendReminder(BookingStatus status)
    {
        await using var context = CreateContext();
        var bookingId = await SeedBookingAsync(context, status);
        var notificationService = new Mock<INotificationService>();
        var job = CreateJob(context, notificationService.Object);

        await job.SendReminderAsync(bookingId);

        notificationService.Verify(s => s.SendCheckoutReminderAsync(It.IsAny<Guid>()), Times.Never);
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static CheckoutReminderJob CreateJob(
        AppDbContext context,
        INotificationService notificationService) =>
        new(
            context,
            notificationService,
            Mock.Of<ILogger<CheckoutReminderJob>>());

    private static async Task<Guid> SeedBookingAsync(AppDbContext context, BookingStatus status)
    {
        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var guestId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();

        context.Orgs.Add(new OrgEntity
        {
            Id = orgId,
            Name = "Test Org",
            Slug = $"org-{orgId:N}",
            DisplayName = "Test Org",
            ContactEmail = "host@example.com",
        });

        context.Properties.Add(new Property
        {
            Id = propertyId,
            OrgId = orgId,
            OwnerId = "auth0|owner",
            Name = "Casa Test",
            Address = "Via Test 1",
            City = "Roma",
            PostalCode = "00100",
            NightlyRate = 100m,
            MaxGuests = 4,
        });

        context.Guests.Add(new Guest
        {
            Id = guestId,
            FirstName = "Anna",
            LastName = "Bianchi",
            Email = "anna@example.com",
        });

        context.Bookings.Add(new Booking
        {
            Id = bookingId,
            PropertyId = propertyId,
            OrgId = orgId,
            GuestId = guestId,
            CheckInDate = DateTime.UtcNow.Date.AddDays(-1),
            CheckOutDate = DateTime.UtcNow.Date,
            Status = status,
            Source = BookingSource.Direct,
            NumberOfGuests = 1,
            TotalPrice = 100m,
        });

        await context.SaveChangesAsync();
        return bookingId;
    }
}
