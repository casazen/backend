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
    public async Task ExecuteAsync_WhenBookingHasNoCheckInSession_SendsHostReminder()
    {
        await using var context = CreateContext();
        var bookingId = await SeedBookingWithinReminderWindowAsync(context, contactEmail: "host@example.com");
        var email = new Mock<IEmailService>();
        email.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new EmailSendResult(true));
        var push = new Mock<IPushNotificationService>();
        var job = CreateJob(context, email.Object, push.Object);

        await job.ExecuteAsync();

        email.Verify(e => e.SendEmailAsync(
            "host@example.com",
            It.Is<string>(subject => subject.Contains("Check-in incompleto")),
            It.IsAny<string>()), Times.Once);
        push.Verify(p => p.SendGuestCheckInIncompleteAsync(bookingId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenBookingHasCompletedCheckInSession_SkipsReminder()
    {
        await using var context = CreateContext();
        var bookingId = await SeedBookingWithinReminderWindowAsync(context, contactEmail: "host@example.com");
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
        var job = CreateJob(context, email.Object, push.Object);

        await job.ExecuteAsync();

        email.Verify(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        push.Verify(p => p.SendGuestCheckInIncompleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

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

    [Fact]
    public async Task ExecuteAsync_CheckedInBookingWithIncompleteSession_SendsEmailAndPush()
    {
        await using var db = CreateContext();

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
            Mock.Of<ILogger<GuestCheckInReminderJob>>());

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

    private static async Task<Guid> SeedBookingWithinReminderWindowAsync(AppDbContext context, string contactEmail)
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

    private static async Task<Guid> SeedIncompleteCheckInAsync(AppDbContext context, string contactEmail)
    {
        var bookingId = await SeedBookingWithinReminderWindowAsync(context, contactEmail);
        var orgId = context.Bookings.Single(b => b.Id == bookingId).OrgId;
        context.GuestCheckInSessions.Add(new GuestCheckInSession
        {
            BookingId = bookingId,
            OrgId = orgId,
            TokenHash = new string('a', 64),
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            Status = GuestCheckInSessionStatus.Inviato,
            SentAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
        return bookingId;
    }
}
