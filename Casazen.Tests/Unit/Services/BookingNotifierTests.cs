using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Push;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>BK-10 (A3-11): the booking confirmation emails, to the guest and to the host, rendered from the booking.</summary>
public class BookingNotifierTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"booking-notifier-{Guid.NewGuid():N}")
        .Options);

    private readonly RecordingEmailQueue _emails = new();
    private readonly RecordingPushQueue _pushes = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task BookingConfirmedAsync_PaidOnline_QueuesGuestConfirmationThenHostNewBooking()
    {
        var bookingId = await SeedAsync(BookingStatus.Confirmed, paid: 362m);

        await Notifier().BookingConfirmedAsync(bookingId, BookingConfirmationKind.PaidOnline);

        var emails = _emails.Snapshot();
        Assert.Equal(2, emails.Count);
        Assert.Equal(("guest@example.com", EmailTemplates.Names.GuestBookingConfirmed), (emails[0].To, emails[0].Template));
        Assert.Equal(("host@example.com", EmailTemplates.Names.HostBookingConfirmed), (emails[1].To, emails[1].Template));
        Assert.Contains("Pagamento ricevuto: <strong>362,00 €</strong>.", emails[0].Content.HtmlBody);
        // BK-11: the readable booking code, in the text and in the link to "Le mie prenotazioni"; never the booking id.
        var code = Casazen.Core.Services.BookingCodes.Format((await _db.Bookings.SingleAsync(b => b.Id == bookingId)).BookingCode);
        Assert.Contains($"Codice prenotazione: <strong>{code}</strong>", emails[0].Content.HtmlBody);
        Assert.Contains($"href=\"https://casazen-app.test/book/villa-org/my-bookings?code={code}\"", emails[0].Content.HtmlBody);
        Assert.DoesNotContain(bookingId.ToString("D"), emails[0].Content.HtmlBody);
        Assert.Contains($"href=\"https://casazen-app.test/app/short-rent/bookings/{bookingId:D}\"", emails[1].Content.HtmlBody);
    }

    [Theory]
    [InlineData(BookingConfirmationKind.PaidOnline)]
    [InlineData(BookingConfirmationKind.PaidOnlineLate)]
    [InlineData(BookingConfirmationKind.DeferredCharge)]
    public async Task BookingConfirmedAsync_ConfirmedWithoutTheHost_QueuesOneNewBookingPushToTheHosts(BookingConfirmationKind kind)
    {
        var bookingId = await SeedAsync(BookingStatus.Confirmed, paid: kind == BookingConfirmationKind.DeferredCharge ? 0m : 362m);

        await Notifier().BookingConfirmedAsync(bookingId, kind);

        // MO-04 (A6-08): next to the host email, through the extension point of BK-10.
        var push = Assert.Single(_pushes.Queued);
        Assert.Equal(PushDeliveryKeys.NewBooking(bookingId), push.DeliveryKey);
        Assert.Equal(PushAudience.BookingHosts(bookingId), push.Audience);
        Assert.Equal(PushTypes.NewBooking, push.Payload.Type);
        Assert.Equal("Nuova prenotazione confermata", push.Payload.Title);
        Assert.Equal("Villa Rosa: dal 05/10/2026 al 08/10/2026, ospiti: 2.", push.Payload.Body);
        Assert.Equal(PushRoutes.Booking(bookingId), push.Payload.Route);
        Assert.Equal(bookingId, push.Payload.BookingId);
        // No guest name on the lock screen.
        Assert.DoesNotContain("Anna", push.Payload.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Verdi", push.Payload.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BookingConfirmedAsync_OnSiteAcceptedByHost_EmailsTheGuestOnly()
    {
        var bookingId = await SeedAsync(BookingStatus.Confirmed, paid: 0m);

        await Notifier().BookingConfirmedAsync(bookingId, BookingConfirmationKind.OnSite);

        var email = Assert.Single(_emails.Snapshot());
        Assert.Equal(EmailTemplates.Names.GuestBookingConfirmed, email.Template);
        Assert.Contains("direttamente in struttura", email.Content.HtmlBody);
        // The host confirmed it: no "new booking" push either.
        Assert.Empty(_pushes.Queued);
    }

    [Fact]
    public async Task BookingConfirmedAsync_WithTheRealQueueAndJob_HostDevicesGetTheNewBookingPush()
    {
        var bookingId = await SeedAsync(BookingStatus.Confirmed, paid: 362m);
        var booking = await _db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
        _db.Users.Add(new User { Id = "auth0|host", Email = "host@example.com", OrgId = booking.OrgId, Role = UserRole.PropertyOwner, IsActive = true });
        _db.DeviceRegistrations.Add(new DeviceRegistration
        {
            UserId = "auth0|host",
            OrgId = booking.OrgId,
            Platform = "ios",
            PushToken = "ExponentPushToken[host]",
            DeviceId = Guid.NewGuid().ToString(),
        });
        await _db.SaveChangesAsync();
        var pipeline = new PushPipeline();
        var notifier = new BookingNotifier(_db, _emails, EmailTestHelpers.Links(), pipeline.Queue, NullLogger<BookingNotifier>.Instance);

        await notifier.BookingConfirmedAsync(bookingId, BookingConfirmationKind.PaidOnline);
        Assert.Empty(pipeline.Expo.SendRequests);
        await pipeline.RunQueuedJobsAsync(_db);
        await pipeline.RunQueuedJobsAsync(_db);

        var message = Assert.Single(pipeline.Expo.Messages);
        Assert.Equal("ExponentPushToken[host]", message.To);
        Assert.Equal(PushTypes.NewBooking, message.Data["type"]);
        Assert.Equal(PushRoutes.Booking(bookingId), message.Data["route"]);
    }

    [Fact]
    public async Task BookingConfirmedAsync_BookingCancelledMeanwhile_SendsNothing()
    {
        var bookingId = await SeedAsync(BookingStatus.Cancelled, paid: 362m);

        await Notifier().BookingConfirmedAsync(bookingId, BookingConfirmationKind.PaidOnline);

        Assert.Empty(_emails.Snapshot());
        Assert.Empty(_pushes.Queued);
    }

    [Fact]
    public async Task BookingConfirmedAsync_PublicSiteNotConfigured_DoesNotThrowAndQueuesNothing()
    {
        var bookingId = await SeedAsync(BookingStatus.Confirmed, paid: 362m);
        var notifier = new BookingNotifier(_db, _emails, EmailTestHelpers.Links(null), _pushes, NullLogger<BookingNotifier>.Instance);

        await notifier.BookingConfirmedAsync(bookingId, BookingConfirmationKind.PaidOnline);

        Assert.Empty(_emails.Snapshot());
    }

    private BookingNotifier Notifier() =>
        new(_db, _emails, EmailTestHelpers.Links(), _pushes, NullLogger<BookingNotifier>.Instance);

    private async Task<Guid> SeedAsync(BookingStatus status, decimal paid)
    {
        var org = new OrgEntity
        {
            Name = "Villa Org",
            Slug = "villa-org",
            DisplayName = "Villa Org",
            ContactEmail = "host@example.com",
        };
        var property = new Property { OrgId = org.Id, OwnerId = "auth0|host", Name = "Villa Rosa", Address = "Via Roma 1", City = "Roma" };
        var guest = new Guest { OrgId = org.Id, FirstName = "Anna", LastName = "Verdi", Email = "guest@example.com" };
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = org.Id,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc),
            NumberOfGuests = 2,
            Status = status,
            Source = BookingSource.Direct,
            BasePrice = 350m,
            CleaningFee = 50m,
            TouristTax = 12m,
            TotalPrice = 362m,
        };
        _db.AddRange(org, property, guest, booking);
        if (paid > 0m)
        {
            _db.Payments.Add(new Payment
            {
                BookingId = booking.Id,
                OrgId = org.Id,
                Amount = paid,
                Status = PaymentStatus.Completed,
                TransactionId = $"pi_{Guid.NewGuid():N}",
            });
        }

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return booking.Id;
    }
}
