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
    public async Task ExecuteAsync_CheckedInBookingWithIncompleteSession_SendsEmailAndPush()
    {
        await using var db = CreateDbContext();

        var org = new OrgEntity
        {
            Name = "Reminder Org",
            Slug = $"reminder-{Guid.NewGuid():N}"[..30],
            DisplayName = "Reminder Org",
            ContactEmail = "host@example.com",
            IsActive = true,
        };
        var guest = new Guest
        {
            FirstName = "Guest",
            LastName = "Incomplete",
            Email = "guest@example.com",
        };
        var property = new Property
        {
            OrgId = org.Id,
            OwnerId = "auth0|owner",
            Name = "Reminder Villa",
            Description = "A property",
            Address = "Via Test 1",
            City = "Roma",
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 100m,
            IsActive = true,
        };
        var booking = new Booking
        {
            OrgId = org.Id,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = DateTime.UtcNow.Date,
            CheckOutDate = DateTime.UtcNow.Date.AddDays(2),
            NumberOfGuests = 1,
            Status = BookingStatus.CheckedIn,
            Source = BookingSource.Direct,
            BasePrice = 100m,
            TotalPrice = 100m,
        };
        var session = new GuestCheckInSession
        {
            OrgId = org.Id,
            BookingId = booking.Id,
            TokenHash = new string('a', 64),
            Status = GuestCheckInSessionStatus.InCompilazione,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            SentAt = DateTime.UtcNow.AddHours(-1),
        };

        db.AddRange(org, guest, property, booking, session);
        await db.SaveChangesAsync();

        var emailService = new Mock<IEmailService>();
        emailService
            .Setup(s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new EmailSendResult(true));

        var pushNotificationService = new Mock<IPushNotificationService>();
        var job = new GuestCheckInReminderJob(
            db,
            emailService.Object,
            pushNotificationService.Object,
            NullLogger<GuestCheckInReminderJob>.Instance);

        await job.ExecuteAsync();

        emailService.Verify(
            s => s.SendEmailAsync(
                "host@example.com",
                It.Is<string>(subject => subject.Contains("Check-in incompleto")),
                It.IsAny<string>()),
            Times.Once);
        pushNotificationService.Verify(
            s => s.SendGuestCheckInIncompleteAsync(booking.Id, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }
}
