using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class NotificationServiceTests
{
    [Fact]
    public async Task SendAlloggiatiDeadlineAlertAsync_WithContactEmail_SendsEmailAndPush()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new AppDbContext(options);
        var bookingId = await SeedBookingAsync(context, contactEmail: "host@example.com");
        var emailService = new Mock<IEmailService>();
        emailService
            .Setup(s => s.SendEmailAsync("host@example.com", It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new EmailSendResult(true));
        var pushNotificationService = new Mock<IPushNotificationService>();
        var service = new NotificationService(
            context,
            emailService.Object,
            pushNotificationService.Object,
            Mock.Of<ILogger<NotificationService>>());

        await service.SendAlloggiatiDeadlineAlertAsync(bookingId);

        emailService.Verify(
            s => s.SendEmailAsync(
                "host@example.com",
                It.Is<string>(subject => subject.Contains("Alloggiati Web", StringComparison.Ordinal)),
                It.Is<string>(html => html.Contains("Test Property", StringComparison.Ordinal))),
            Times.Once);
        pushNotificationService.Verify(
            s => s.SendGuestCheckInIncompleteAsync(bookingId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SendAlloggiatiDeadlineAlertAsync_WithoutContactEmail_StillSendsPush()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new AppDbContext(options);
        var bookingId = await SeedBookingAsync(context, contactEmail: string.Empty);
        var emailService = new Mock<IEmailService>();
        var pushNotificationService = new Mock<IPushNotificationService>();
        var service = new NotificationService(
            context,
            emailService.Object,
            pushNotificationService.Object,
            Mock.Of<ILogger<NotificationService>>());

        await service.SendAlloggiatiDeadlineAlertAsync(bookingId);

        emailService.Verify(
            s => s.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
        pushNotificationService.Verify(
            s => s.SendGuestCheckInIncompleteAsync(bookingId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static async Task<Guid> SeedBookingAsync(AppDbContext context, string contactEmail)
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
            ContactEmail = contactEmail,
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
            IsActive = true,
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
