using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class NotificationServiceTests
{
    [Theory]
    [InlineData(StayAlertKind.GuestDataMissing, "guest-checkin-incomplete", "guest-data-missing", "Dati ospiti mancanti")]
    [InlineData(StayAlertKind.AlloggiatiDeadlineApproaching, "alloggiati-deadline", "alloggiati-deadline", "Alloggiati Web in scadenza")]
    [InlineData(StayAlertKind.AlloggiatiOverdue, "alloggiati-overdue", "alloggiati-overdue", "Alloggiati Web scaduta")]
    [InlineData(StayAlertKind.AlloggiatiFailed, "alloggiati-failed", "alloggiati-failed", "Invio Alloggiati Web non riuscito")]
    public async Task SendStayAlertAsync_AlloggiatiAlert_QueuesItsEmailAndPushesItsOwnType(
        StayAlertKind kind,
        string template,
        string pushType,
        string subject)
    {
        await using var context = CreateContext();
        var bookingId = await SeedBookingAsync(context, contactEmail: "host@example.com");
        var emails = new RecordingEmailQueue();
        var push = new Mock<IPushNotificationService>();
        PushNotificationPayload? sent = null;
        push.Setup(p => p.SendToBookingHostsAsync(It.IsAny<PushNotificationPayload>(), It.IsAny<CancellationToken>()))
            .Callback<PushNotificationPayload, CancellationToken>((payload, _) => sent = payload)
            .Returns(Task.CompletedTask);

        await CreateService(context, emails, push.Object).SendStayAlertAsync(new StayAlert(bookingId, kind));

        var email = Assert.Single(emails.Queued);
        Assert.Equal("host@example.com", email.To);
        Assert.Equal(template, email.Template);
        Assert.StartsWith(subject, email.Content.Subject, StringComparison.Ordinal);
        Assert.Contains("Test Property", email.Content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("<strong>Anna Bianchi</strong>", email.Content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(
            $"href=\"{EmailTestHelpers.PublicSiteBaseUrl}/app/short-rent/bookings/{bookingId:D}\"",
            email.Content.HtmlBody,
            StringComparison.Ordinal);
        Assert.NotNull(sent);
        Assert.Equal(pushType, sent.Type);
        Assert.Equal(bookingId, sent.BookingId);
        Assert.Equal($"/bookings/{bookingId}", sent.Route);
        Assert.Contains("Test Property", sent.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Check-in incompleto", sent.Title, StringComparison.Ordinal);
        push.Verify(p => p.SendCheckoutReminderAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendStayAlertAsync_CheckoutReminder_QueuesEmailAndSendsTheCheckoutPush()
    {
        await using var context = CreateContext();
        var bookingId = await SeedBookingAsync(context, contactEmail: "host@example.com");
        var emails = new RecordingEmailQueue();
        var push = new Mock<IPushNotificationService>();

        await CreateService(context, emails, push.Object)
            .SendStayAlertAsync(new StayAlert(bookingId, StayAlertKind.CheckoutReminder));

        var email = Assert.Single(emails.Queued);
        Assert.Equal("checkout-reminder", email.Template);
        Assert.StartsWith("Check-out di oggi - Test Property", email.Content.Subject, StringComparison.Ordinal);
        push.Verify(p => p.SendCheckoutReminderAsync(bookingId, It.IsAny<CancellationToken>()), Times.Once);
        push.Verify(p => p.SendToBookingHostsAsync(It.IsAny<PushNotificationPayload>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendStayAlertAsync_OverdueReminderWithRegisteredArrival_ShowsDeadlineAndReminderCount()
    {
        await using var context = CreateContext();
        var bookingId = await SeedBookingAsync(context, contactEmail: "host@example.com");
        var emails = new RecordingEmailQueue();
        var deadline = new DateTime(2026, 10, 6, 13, 0, 0, DateTimeKind.Utc);

        await CreateService(context, emails, Mock.Of<IPushNotificationService>())
            .SendStayAlertAsync(new StayAlert(bookingId, StayAlertKind.AlloggiatiOverdue, deadline, ReminderNumber: 2, MaxReminders: 2));

        var html = Assert.Single(emails.Queued).Content.HtmlBody;
        Assert.Contains("Scadenza: <strong>06/10/2026 15:00</strong>", html, StringComparison.Ordinal);
        Assert.Contains("Promemoria 2 di 2.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendStayAlertAsync_WithoutContactEmail_StillSendsPush()
    {
        await using var context = CreateContext();
        var bookingId = await SeedBookingAsync(context, contactEmail: string.Empty);
        var queue = new Mock<IEmailQueue>();
        queue.Setup(q => q.Enqueue(It.IsAny<string?>(), It.IsAny<EmailContent>(), It.IsAny<string>()))
            .Returns((string? to, EmailContent _, string _) => !string.IsNullOrWhiteSpace(to));
        var push = new Mock<IPushNotificationService>();

        await CreateService(context, queue.Object, push.Object)
            .SendStayAlertAsync(new StayAlert(bookingId, StayAlertKind.AlloggiatiDeadlineApproaching));

        push.Verify(
            p => p.SendToBookingHostsAsync(
                It.Is<PushNotificationPayload>(payload => payload.BookingId == bookingId),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SendStayAlertAsync_UnknownBooking_SendsNothing()
    {
        await using var context = CreateContext();
        var emails = new RecordingEmailQueue();
        var push = new Mock<IPushNotificationService>(MockBehavior.Strict);

        await CreateService(context, emails, push.Object)
            .SendStayAlertAsync(new StayAlert(Guid.NewGuid(), StayAlertKind.AlloggiatiOverdue));

        Assert.Empty(emails.Queued);
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static NotificationService CreateService(AppDbContext context, IEmailQueue emails, IPushNotificationService push) =>
        new(context, emails, push, EmailTestHelpers.Links(), Mock.Of<ILogger<NotificationService>>());

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
