using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Web.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

public class GuestCheckInReminderJobTests
{
    [Fact]
    public async Task ExecuteAsync_SendsPush_WhenHostEmailMissing()
    {
        await using var context = CreateContext();
        var bookingId = await SeedIncompleteCheckInAsync(context, contactEmail: string.Empty);
        var emailService = new Mock<IEmailService>();
        var pushService = new Mock<IPushNotificationService>();
        var job = CreateJob(context, emailService.Object, pushService.Object);

        await job.ExecuteAsync();

        pushService.Verify(p => p.SendGuestCheckInIncompleteAsync(bookingId, It.IsAny<CancellationToken>()), Times.Once);
        emailService.Verify(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_SendsPushAndEmail_WhenHostEmailConfigured()
    {
        await using var context = CreateContext();
        var bookingId = await SeedIncompleteCheckInAsync(context, contactEmail: "host@example.com");
        var emailService = new Mock<IEmailService>();
        emailService
            .Setup(e => e.SendEmailAsync("host@example.com", It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new EmailSendResult(true));
        var pushService = new Mock<IPushNotificationService>();
        var job = CreateJob(context, emailService.Object, pushService.Object);

        await job.ExecuteAsync();

        pushService.Verify(p => p.SendGuestCheckInIncompleteAsync(bookingId, It.IsAny<CancellationToken>()), Times.Once);
        emailService.Verify(e => e.SendEmailAsync("host@example.com", It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new AppDbContext(options);
    }

    private static GuestCheckInReminderJob CreateJob(
        AppDbContext context,
        IEmailService emailService,
        IPushNotificationService pushService) =>
        new(
            context,
            emailService,
            pushService,
            Mock.Of<ILogger<GuestCheckInReminderJob>>());

    private static async Task<Guid> SeedIncompleteCheckInAsync(AppDbContext context, string contactEmail)
    {
        var org = new OrgEntity
        {
            Id = Guid.NewGuid(),
            Name = "CasaZen Host",
            DisplayName = "CasaZen Host",
            Slug = $"host-{Guid.NewGuid():N}",
            ContactEmail = contactEmail,
            IsActive = true,
        };
        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            Org = org,
            OwnerId = "auth0|owner",
            Name = "Apartment",
            Address = "Via Roma 1",
            City = "Roma",
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 100m,
        };
        var guest = new Guest
        {
            Id = Guid.NewGuid(),
            FirstName = "Ada",
            LastName = "Lovelace",
            Email = "ada@example.com",
        };
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            Org = org,
            PropertyId = property.Id,
            Property = property,
            GuestId = guest.Id,
            Guest = guest,
            CheckInDate = DateTime.UtcNow.AddHours(12),
            CheckOutDate = DateTime.UtcNow.AddDays(3),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            TotalPrice = 100m,
        };

        context.Orgs.Add(org);
        context.Properties.Add(property);
        context.Guests.Add(guest);
        context.Bookings.Add(booking);
        context.GuestCheckInSessions.Add(new GuestCheckInSession
        {
            BookingId = booking.Id,
            Booking = booking,
            OrgId = org.Id,
            TokenHash = new string('a', 64),
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            Status = GuestCheckInSessionStatus.Inviato,
            SentAt = DateTime.UtcNow,
        });

        await context.SaveChangesAsync();
        return booking.Id;
    }
}
