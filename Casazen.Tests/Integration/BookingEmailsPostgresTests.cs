using System.Net;
using System.Net.Http.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Push;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stripe;
using Xunit;
using PaymentMethod = Casazen.Core.Entities.PaymentMethod;

namespace Casazen.Tests.Integration;

/// <summary>
/// BK-10 (A3-11, #58) on real PostgreSQL, with the services of the application: a booking that becomes confirmed (payment
/// webhook, saved card of a deferred payment, host acceptance of a "pay at the property" request) sends one confirmation
/// to the guest and, unless the host confirmed it, one "new booking" email to the host, whatever the number of webhook
/// deliveries. A host cancellation with a refund sends the guest one coherent email, then the refund confirmation only
/// when Stripe confirms later.
/// </summary>
public class BookingEmailsPostgresTests : IClassFixture<BookingEmailsPostgresTests.EmailsFactory>
{
    private const string HostRole = "PropertyOwner";

    /// <summary><c>App:PublicSiteBaseUrl</c> of <see cref="CasazenWebApplicationFactory"/>.</summary>
    private const string PublicSite = "https://casazen-app.vercel.app";

    private readonly EmailsFactory _factory;

    public BookingEmailsPostgresTests(EmailsFactory factory) => _factory = factory;

    private FakeStripeService FakeStripe => (FakeStripeService)_factory.Services.GetRequiredService<IStripeService>();

    [PostgresFact]
    public async Task HandleEventAsync_PaymentSucceeded_OneConfirmationToGuestAndOneNewBookingToHost()
    {
        var host = await SeedHostAsync();
        var seed = await SeedCheckoutHoldAsync(host, PaymentOption.Immediate);

        await HandleAsync(PaymentSucceeded(seed.IntentId, host.Account), WebhookSource.Connected);

        var confirmed = await LoadAsync(seed.BookingId);
        Assert.Equal(BookingStatus.Confirmed, confirmed.Status);
        var guest = Assert.Single(Emails(seed));
        Assert.Equal(EmailTemplates.Names.GuestBookingConfirmed, guest.Template);
        Assert.Equal(seed.GuestEmail, guest.To);
        Assert.Equal($"Prenotazione confermata - {host.PropertyName}", guest.Content.Subject);
        // BK-11: the readable code of "Le mie prenotazioni", never the booking id.
        var bookingCode = BookingCodes.Format(confirmed.BookingCode);
        Assert.Contains($"Codice prenotazione: <strong>{bookingCode}</strong>", guest.Content.HtmlBody);
        Assert.DoesNotContain(seed.BookingId.ToString("D"), guest.Content.HtmlBody);
        Assert.Contains("<li>Soggiorno: 300,00 €</li>", guest.Content.HtmlBody);
        Assert.Contains("<li>Pulizie: 50,00 €</li>", guest.Content.HtmlBody);
        Assert.Contains("<li>Tassa di soggiorno: 12,00 € (inclusa nel totale)</li>", guest.Content.HtmlBody);
        Assert.Contains("<li>Totale: <strong>362,00 €</strong></li>", guest.Content.HtmlBody);
        Assert.Contains("Pagamento ricevuto: <strong>362,00 €</strong>.", guest.Content.HtmlBody);
        var myBookings = EmailHtmlBuilder.Encode(
            $"{PublicSite}/book/{Uri.EscapeDataString(host.OrgSlug)}/my-bookings?code={bookingCode}");
        Assert.Contains($"href=\"{myBookings}\"", guest.Content.HtmlBody);
        Assert.Contains($"contatta <strong>Villa Test Srl</strong> all'indirizzo {host.ContactEmail}.", guest.Content.HtmlBody);
        Assert.DoesNotContain("dopo il tempo previsto", guest.Content.HtmlBody);

        var toHost = Assert.Single(HostEmails(host));
        Assert.Equal(EmailTemplates.Names.HostBookingConfirmed, toHost.Template);
        Assert.Contains("<strong>Giulia Bianchi</strong> ha prenotato", toHost.Content.HtmlBody);
        Assert.Contains("Pagamento online ricevuto: <strong>362,00 €</strong>.", toHost.Content.HtmlBody);
        Assert.Contains($"href=\"{PublicSite}/app/short-rent/bookings/{seed.BookingId:D}\"", toHost.Content.HtmlBody);
    }

    [PostgresFact]
    public async Task HandleEventAsync_DuplicateAndRepeatedPaymentEvents_NoSecondEmail()
    {
        var host = await SeedHostAsync();
        var seed = await SeedCheckoutHoldAsync(host, PaymentOption.Immediate);
        var stripeEvent = PaymentSucceeded(seed.IntentId, host.Account);

        // Two workers on the same delivery at once, the same event again, then another event of the same payment.
        await Task.WhenAll(
            Task.Run(() => HandleAsync(stripeEvent, WebhookSource.Connected)),
            Task.Run(() => HandleAsync(stripeEvent, WebhookSource.Connected)));
        await HandleAsync(stripeEvent, WebhookSource.Connected);
        await HandleAsync(PaymentSucceeded(seed.IntentId, host.Account), WebhookSource.Connected);

        Assert.Equal(BookingStatus.Confirmed, (await LoadAsync(seed.BookingId)).Status);
        Assert.Equal([EmailTemplates.Names.GuestBookingConfirmed], Emails(seed).Select(e => e.Template));
        Assert.Equal([EmailTemplates.Names.HostBookingConfirmed], HostEmails(host).Select(e => e.Template));
        // MO-04 (A6-08): one "new booking" push to the hosts, queued with the host email.
        var push = Assert.Single(Pushes(seed));
        Assert.Equal(PushTypes.NewBooking, push.Payload.Type);
        Assert.Equal(PushAudience.BookingHosts(seed.BookingId), push.Audience);
    }

    [PostgresFact]
    public async Task HandleEventAsync_SetupIntentSucceededTwice_ConfirmsDeferredBookingWithChargeDateOnce()
    {
        var host = await SeedHostAsync();
        var seed = await SeedCheckoutHoldAsync(host, PaymentOption.OnCancellationDeadline);

        await Task.WhenAll(
            Task.Run(() => HandleAsync(SetupSucceeded(seed, host.Account), WebhookSource.Connected)),
            Task.Run(() => HandleAsync(SetupSucceeded(seed, host.Account), WebhookSource.Connected)));

        Assert.Equal(BookingStatus.Confirmed, (await LoadAsync(seed.BookingId)).Status);
        var guest = Assert.Single(Emails(seed));
        Assert.Equal(EmailTemplates.Names.GuestBookingConfirmed, guest.Template);
        Assert.Contains(
            $"il totale di <strong>362,00 €</strong> sarà addebitato il <strong>{seed.FreeRefundDeadline:dd/MM/yyyy}</strong>",
            guest.Content.HtmlBody);
        Assert.DoesNotContain("Pagamento ricevuto", guest.Content.HtmlBody);
        var toHost = Assert.Single(HostEmails(host));
        Assert.Equal(EmailTemplates.Names.HostBookingConfirmed, toHost.Template);
        Assert.Contains("L'ospite ha salvato un metodo di pagamento", toHost.Content.HtmlBody);
    }

    [PostgresFact]
    public async Task ApproveOnSiteRequest_Host_GuestGetsTheConfirmationAndHostNoNewBookingEmail()
    {
        var host = await SeedHostAsync();
        var seed = await SeedOnSiteRequestAsync(host);
        using var client = _factory.CreateAuthenticatedClient(host.HostId, HostRole);

        var response = await client.PostAsync($"/api/bookings/{seed.BookingId}/approve", null);
        var again = await client.PostAsync($"/api/bookings/{seed.BookingId}/approve", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var guest = Assert.Single(Emails(seed));
        Assert.Equal(EmailTemplates.Names.GuestBookingConfirmed, guest.Template);
        Assert.Contains("l'host ha accettato la tua richiesta", guest.Content.HtmlBody);
        Assert.Contains("Pagherai <strong>362,00 €</strong> direttamente in struttura.", guest.Content.HtmlBody);
        Assert.Empty(HostEmails(host));
        Assert.Empty(Pushes(seed));
    }

    [PostgresFact]
    public async Task CancelBooking_RefundConfirmedAtOnce_GuestGetsOneEmailWithTheRefundAndNoLaterDuplicate()
    {
        var host = await SeedHostAsync();
        var seed = await SeedPaidBookingAsync(host);
        FakeStripe.RefundStatus = "succeeded";
        using var client = _factory.CreateAuthenticatedClient(host.HostId, HostRole);

        var response = await client.PostAsJsonAsync(
            $"/api/bookings/{seed.BookingId}/cancel", new { refundAmount = 362m, reason = "Nota interna" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var email = Assert.Single(Emails(seed));
        Assert.Equal(EmailTemplates.Names.GuestBookingCancelled, email.Template);
        Assert.Contains("Ti abbiamo rimborsato <strong>362,00 €</strong>.", email.Content.HtmlBody);
        Assert.DoesNotContain("È stato avviato", email.Content.HtmlBody);
        Assert.DoesNotContain("Nota interna", email.Content.HtmlBody);
        var refund = await SingleRefundAsync(seed.BookingId);
        Assert.NotNull(refund.GuestNotifiedAt);

        // Stripe's webhook for the same refund arrives afterwards: nothing more for the guest.
        await HandleAsync(RefundUpdated(refund, seed.IntentId, host.Account), WebhookSource.Connected);
        Assert.Single(Emails(seed));
        Assert.Empty(HostEmails(host));
    }

    [PostgresFact]
    public async Task CancelBooking_RefundPendingThenConfirmedByWebhook_StartedThenConfirmedOnce()
    {
        var host = await SeedHostAsync();
        var seed = await SeedPaidBookingAsync(host);
        FakeStripe.RefundStatus = "pending";
        using var client = _factory.CreateAuthenticatedClient(host.HostId, HostRole);

        var response = await client.PostAsJsonAsync($"/api/bookings/{seed.BookingId}/cancel", new { refundAmount = 362m });
        FakeStripe.RefundStatus = "succeeded";

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cancelled = Assert.Single(Emails(seed));
        Assert.Equal(EmailTemplates.Names.GuestBookingCancelled, cancelled.Template);
        Assert.Contains("È stato avviato un rimborso di <strong>362,00 €</strong>", cancelled.Content.HtmlBody);
        Assert.DoesNotContain("Ti abbiamo rimborsato", cancelled.Content.HtmlBody);

        var refund = await SingleRefundAsync(seed.BookingId);
        await HandleAsync(RefundUpdated(refund, seed.IntentId, host.Account), WebhookSource.Connected);
        await HandleAsync(RefundUpdated(refund, seed.IntentId, host.Account), WebhookSource.Connected);

        Assert.Equal(
            [EmailTemplates.Names.GuestBookingCancelled, EmailTemplates.Names.GuestRefundConfirmed],
            Emails(seed).Select(e => e.Template));
        Assert.Equal(PaymentStatus.Refunded, (await LoadAsync(seed.BookingId)).Payments.Single().Status);
    }

    /// <summary>Emails of the guest of <paramref name="seed"/>, in queue order.</summary>
    private List<(string? To, EmailContent Content, string Template)> Emails(BookingSeed seed) =>
        _factory.Emails.Snapshot().Where(e => e.To == seed.GuestEmail).ToList();

    /// <summary>Pushes queued about the booking (MO-04).</summary>
    private List<QueuedPushRecord> Pushes(BookingSeed seed) =>
        _factory.Pushes.Queued.Where(p => p.Payload.BookingId == seed.BookingId).ToList();

    private List<(string? To, EmailContent Content, string Template)> HostEmails(HostSeed host) =>
        _factory.Emails.Snapshot().Where(e => e.To == host.ContactEmail).ToList();

    /// <summary>One delivery of the event with the application's handler, in its own DI scope, as the Hangfire job runs it.</summary>
    private async Task HandleAsync(Event stripeEvent, WebhookSource source)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<StripeWebhookHandler>().HandleEventAsync(stripeEvent, source);
    }

    private static Event PaymentSucceeded(string paymentIntentId, string account) => new()
    {
        Id = StripeTestEvents.NewEventId(),
        Type = "payment_intent.succeeded",
        Account = account,
        Data = new EventData
        {
            Object = new PaymentIntent
            {
                Id = paymentIntentId,
                Status = "succeeded",
                Metadata = new Dictionary<string, string> { ["kind"] = "direct-booking" },
            },
        },
    };

    private static Event SetupSucceeded(BookingSeed seed, string account) => new()
    {
        Id = StripeTestEvents.NewEventId(),
        Type = "setup_intent.succeeded",
        Account = account,
        Data = new EventData
        {
            Object = new SetupIntent
            {
                Id = seed.IntentId,
                Status = "succeeded",
                CustomerId = $"cus_{Guid.NewGuid():N}",
                PaymentMethodId = $"pm_{Guid.NewGuid():N}",
                Metadata = new Dictionary<string, string>
                {
                    ["kind"] = "direct-booking-setup",
                    ["bookingId"] = seed.BookingId.ToString(),
                },
            },
        },
    };

    private static Event RefundUpdated(PaymentRefund refund, string paymentIntentId, string account) => new()
    {
        Id = StripeTestEvents.NewEventId(),
        Type = "refund.updated",
        Account = account,
        Data = new EventData
        {
            Object = new Refund
            {
                Id = refund.StripeRefundId,
                PaymentIntentId = paymentIntentId,
                Amount = (long)(refund.Amount * 100m),
                Status = "succeeded",
                Metadata = new Dictionary<string, string> { ["paymentRefundId"] = refund.Id.ToString() },
            },
        },
    };

    private sealed record HostSeed(
        string HostId,
        Guid OrgId,
        string OrgSlug,
        string ContactEmail,
        string Account,
        Guid PropertyId,
        string PropertyName);

    private sealed record BookingSeed(Guid BookingId, string IntentId, string GuestEmail, DateTime? FreeRefundDeadline);

    /// <summary>A host whose org has a connected account, a public name and contact email, and a bookable property.</summary>
    private async Task<HostSeed> SeedHostAsync()
    {
        var hostId = $"auth0|bk10-host-{Guid.NewGuid():N}";
        var seeded = await _factory.SeedPropertyAsync(hostId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.SingleAsync(o => o.Id == seeded.OrgId);
        org.StripeConnectedAccountId = $"acct_bk10_{Guid.NewGuid():N}";
        org.ConnectChargesEnabled = true;
        org.DisplayName = "Villa Test Srl";
        org.ContactEmail = $"host.{Guid.NewGuid():N}@example.com";
        var property = await db.Properties.SingleAsync(p => p.Id == seeded.Id);
        property.Name = $"Casa {Guid.NewGuid():N}"[..20];
        property.CinCode = "IT058091C27G5FFZDZ";
        property.ComplianceStatus = PropertyComplianceStatus.Active;
        await db.SaveChangesAsync();
        return new HostSeed(hostId, org.Id, org.Slug, org.ContactEmail, org.StripeConnectedAccountId, property.Id, property.Name);
    }

    /// <summary>
    /// A checkout hold made 5 minutes ago (valid): 3 nights, 300 lodging + 50 cleaning + 12 tourist tax = 362. Immediate:
    /// a pending PaymentIntent; deferred: a pending SetupIntent.
    /// </summary>
    private async Task<BookingSeed> SeedCheckoutHoldAsync(HostSeed host, PaymentOption option)
    {
        var deferred = option == PaymentOption.OnCancellationDeadline;
        var intentId = deferred ? $"seti_bk10_{Guid.NewGuid():N}" : $"pi_bk10_{Guid.NewGuid():N}";
        var (booking, guest) = NewBooking(host, BookingStatus.Pending, option, minutesAgo: 5);
        booking.StripeSetupIntentId = deferred ? intentId : null;
        var payment = new Payment
        {
            BookingId = booking.Id,
            OrgId = host.OrgId,
            Amount = booking.TotalPrice,
            Status = PaymentStatus.Pending,
            Method = PaymentMethod.CreditCard,
            TransactionId = intentId,
            StripePaymentIntentId = deferred ? null : intentId,
            StripeAccountId = host.Account,
            Description = deferred
                ? "Direct checkout - deferred payment (charged at deadline)"
                : "Direct checkout - immediate payment",
            CreatedAt = booking.CreatedAt,
            UpdatedAt = booking.CreatedAt,
        };
        await SaveAsync(guest, booking, payment);
        return new BookingSeed(booking.Id, intentId, guest.Email, booking.FreeRefundDeadline);
    }

    /// <summary>A "pay at the property" request whose guest confirmed the email, waiting for the host (BK-06).</summary>
    private async Task<BookingSeed> SeedOnSiteRequestAsync(HostSeed host)
    {
        var (booking, guest) = NewBooking(host, BookingStatus.Pending, PaymentOption.OnSite, minutesAgo: 30);
        booking.GuestEmailVerifiedAt = DateTime.UtcNow.AddMinutes(-20);
        booking.RequestExpiresAt = DateTime.UtcNow.AddHours(20);
        var payment = new Payment
        {
            BookingId = booking.Id,
            OrgId = host.OrgId,
            Amount = booking.TotalPrice,
            Status = PaymentStatus.Pending,
            Method = PaymentMethod.CashOnArrival,
            Description = "Direct checkout - payment on site",
        };
        await SaveAsync(guest, booking, payment);
        return new BookingSeed(booking.Id, string.Empty, guest.Email, booking.FreeRefundDeadline);
    }

    /// <summary>A confirmed booking of the booking site, paid on Stripe on the host's connected account.</summary>
    private async Task<BookingSeed> SeedPaidBookingAsync(HostSeed host)
    {
        var intentId = $"pi_bk10_{Guid.NewGuid():N}";
        var (booking, guest) = NewBooking(host, BookingStatus.Confirmed, PaymentOption.Immediate, minutesAgo: 60);
        var payment = new Payment
        {
            BookingId = booking.Id,
            OrgId = host.OrgId,
            Amount = booking.TotalPrice,
            Status = PaymentStatus.Completed,
            Method = PaymentMethod.CreditCard,
            TransactionId = intentId,
            StripePaymentIntentId = intentId,
            StripeAccountId = host.Account,
            ProcessedAt = booking.CreatedAt,
        };
        await SaveAsync(guest, booking, payment);
        return new BookingSeed(booking.Id, intentId, guest.Email, booking.FreeRefundDeadline);
    }

    private static (Booking Booking, Guest Guest) NewBooking(HostSeed host, BookingStatus status, PaymentOption option, int minutesAgo)
    {
        var createdAt = DateTime.UtcNow.AddMinutes(-minutesAgo);
        var checkIn = TimeProvider.System.TodayInRome().AddDays(40);
        var guest = new Guest
        {
            OrgId = host.OrgId,
            FirstName = "Giulia",
            LastName = "Bianchi",
            Email = $"giulia.{Guid.NewGuid():N}@example.com",
            DataProcessingPurpose = "Direct Booking Checkout",
        };
        var booking = new Booking
        {
            PropertyId = host.PropertyId,
            OrgId = host.OrgId,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(3),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = status,
            Source = BookingSource.Direct,
            PaymentOption = option,
            BasePrice = 350m,
            CleaningFee = 50m,
            TouristTax = 12m,
            TouristTaxAmount = 12m,
            TotalPrice = 362m,
            FreeRefundDeadline = checkIn.AddDays(-7),
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        return (booking, guest);
    }

    private async Task SaveAsync(Guest guest, Booking booking, Payment payment)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AddRange(guest, booking, payment);
        await db.SaveChangesAsync();
    }

    private async Task<Booking> LoadAsync(Guid bookingId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Bookings.AsNoTracking().Include(b => b.Payments).SingleAsync(b => b.Id == bookingId);
    }

    private async Task<PaymentRefund> SingleRefundAsync(Guid bookingId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PaymentRefunds.AsNoTracking().SingleAsync(r => r.Payment.BookingId == bookingId);
    }

    /// <summary>The integration host with a recording email queue.</summary>
    public sealed class EmailsFactory : CasazenWebApplicationFactory
    {
        internal RecordingEmailQueue Emails { get; } = new();

        internal RecordingPushQueue Pushes { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                RemoveAllOf<IEmailQueue>(services);
                services.AddSingleton<IEmailQueue>(Emails);
                RemoveAllOf<IPushNotificationService>(services);
                services.AddSingleton<IPushNotificationService>(Pushes);
            });
        }
    }
}
