using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Web.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

public class GuestCheckInReminderJobTests
{
    [Fact]
    public async Task ExecuteAsync_WhenBookingHasNoCheckInSession_SendsHostReminder()
    {
        await using var context = CreateDbContext();
        var bookingId = await SeedBookingWithinReminderWindowAsync(context);
        var email = new Mock<IEmailService>();
        var push = new Mock<IPushNotificationService>();
        email.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new EmailSendResult(true));
        var job = CreateJob(context, email, push);

        await job.ExecuteAsync();

        email.Verify(e => e.SendEmailAsync(
            "host@example.com",
            It.Is<string>(subject => subject.Contains("Check-in incompleto")),
            It.IsAny<string>()), Times.Once);
        push.Verify(p => p.SendGuestCheckInIncompleteAsync(bookingId, default), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenBookingHasCompletedCheckInSession_SkipsReminder()
    {
        await using var context = CreateDbContext();
        var bookingId = await SeedBookingWithinReminderWindowAsync(context);
        context.GuestCheckInSessions.Add(new GuestCheckInSession
        {
            BookingId = bookingId,
            OrgId = context.Bookings.Single(b => b.Id == bookingId).OrgId,
            TokenHash = new string('a', 64),
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            Status = GuestCheckInSessionStatus.Completo,
            SentAt = DateTime.UtcNow.AddHours(-1),
            CompletedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
        var email = new Mock<IEmailService>();
        var push = new Mock<IPushNotificationService>();
        var job = CreateJob(context, email, push);

        await job.ExecuteAsync();

        email.Verify(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        push.Verify(p => p.SendGuestCheckInIncompleteAsync(It.IsAny<Guid>(), default), Times.Never);
    }

    private static GuestCheckInReminderJob CreateJob(
        AppDbContext context,
        Mock<IEmailService> email,
        Mock<IPushNotificationService> push) =>
        new(
            context,
            email.Object,
            push.Object,
            NullLogger<GuestCheckInReminderJob>.Instance);

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<Guid> SeedBookingWithinReminderWindowAsync(AppDbContext context)
    {
        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var guestId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();

        context.Orgs.Add(new OrgEntity
        {
            Id = orgId,
            Name = "Host Org",
            Slug = $"host-{orgId:N}",
            DisplayName = "Host Org",
            ContactEmail = "host@example.com",
        });

        context.Properties.Add(new Property
        {
            Id = propertyId,
            OrgId = orgId,
            OwnerId = "auth0|owner",
            Name = "Test Property",
            Address = "Via Test 1",
            City = "Roma",
            PostalCode = "00100",
            NightlyRate = 100m,
            MaxGuests = 4,
            CinCode = "IT-TEST",
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
            CheckInDate = DateTime.UtcNow.AddHours(12),
            CheckOutDate = DateTime.UtcNow.AddDays(3),
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            NumberOfGuests = 1,
        });

        await context.SaveChangesAsync();
        return bookingId;
    }
}
