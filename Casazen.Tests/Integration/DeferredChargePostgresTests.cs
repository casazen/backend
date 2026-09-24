using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Casazen.Tests.Unit.Email;
using Casazen.Web.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// BK-08 (A3-14) on real PostgreSQL: the deferred charge of "Paga alla scadenza" bookings. The payment is Completed only
/// when Stripe says <c>succeeded</c>; <c>requires_action</c> fails it and emails the guest (link to the outcome page, which
/// then offers the same PaymentIntent) and the host; <c>processing</c> waits for the webhook; attempts are limited and an
/// unpaid booking is cancelled on its cancellation day with its dates released; two concurrent runs create one
/// PaymentIntent; the webhooks handle the <c>direct-booking-deadline-charge</c> kind on the Connect endpoint and on the
/// platform endpoint (with the event's account).
/// </summary>
/// <remarks>
/// The job runs over the whole test database with a scripted Stripe that knows only the customers of the running test:
/// bookings left by earlier tests get errors and each test asserts on its own booking only. The clock is a fixed
/// Europe/Rome day relative to today, so the public availability (real clock) still sees the stay in the future.
/// </remarks>
public class DeferredChargePostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const decimal Total = 350m;

    private static readonly DateTime Day0 = TimeProvider.System.TodayInRome();

    private readonly CasazenWebApplicationFactory _factory;
    private readonly ScriptedDeferredChargeStripe _stripe = new();
    private readonly RecordingEmailQueue _emails = new();

    public DeferredChargePostgresTests(CasazenWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [PostgresFact]
    public async Task RunDueCharges_PaymentIntentSucceeded_CompletesPaymentOnTheCardAccount()
    {
        var seeded = await SeedDeferredBookingAsync("succeeded");

        await RunJobAsync(day: 0);

        var creation = Assert.Single(_stripe.CreationsOf(seeded.CustomerId));
        Assert.Equal(seeded.Account, creation.Account);
        Assert.Equal($"direct-booking-deadline:{seeded.BookingId:N}:{Day0:yyyyMMdd}:1", creation.IdempotencyKey);
        Assert.Equal(35_000, creation.AmountCents);
        var booking = await LoadBookingAsync(seeded.BookingId);
        var payment = Assert.Single(booking.Payments);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Equal(creation.PaymentIntentId, payment.StripePaymentIntentId);
        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        Assert.Null(booking.DeferredChargeFailedAt);
        Assert.Empty(EmailsOf(seeded));
    }

    [PostgresFact]
    public async Task RunDueCharges_RequiresAction_FailsWithoutCompletingEmailsGuestAndHostAndTheLinkPaysTheSameIntent()
    {
        var seeded = await SeedDeferredBookingAsync("requires_action");

        await RunJobAsync(day: 0);
        await RunJobAsync(day: 0); // same day again: no second attempt, no second email

        var paymentIntentId = Assert.Single(_stripe.CreationsOf(seeded.CustomerId)).PaymentIntentId;
        var booking = await LoadBookingAsync(seeded.BookingId);
        var payment = Assert.Single(booking.Payments);
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.NotEqual(PaymentStatus.Completed, payment.Status);
        Assert.Null(payment.ProcessedAt);
        Assert.Equal(paymentIntentId, payment.StripePaymentIntentId);
        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        Assert.Equal(1, booking.DeferredChargeAttempts);
        Assert.NotNull(booking.DeferredChargeFailedAt);

        var emails = EmailsOf(seeded);
        Assert.Equal(2, emails.Count);
        var guestEmail = Assert.Single(emails, e => e.Template == EmailTemplates.Names.GuestDeferredChargeFailed);
        Assert.Single(emails, e => e.Template == EmailTemplates.Names.HostDeferredChargeFailed && e.To == seeded.HostEmail);
        var token = Regex.Match(guestEmail.Content.HtmlBody, $@"/booking/{seeded.BookingId:D}\?token=([A-Za-z0-9_-]+)").Groups[1].Value;
        Assert.NotEmpty(token);

        // The link of the email opens the outcome page: payment to complete, same PaymentIntent, until the cancellation.
        using var guest = _factory.CreateClient();
        var outcome = await guest.PostAsJsonAsync($"/api/public/bookings/{seeded.BookingId}/outcome", new { token });
        Assert.Equal(HttpStatusCode.OK, outcome.StatusCode);
        var outcomeBody = await outcome.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PaymentFailed", outcomeBody.GetProperty("state").GetString());
        Assert.Equal(
            RomeCalendar.StartOfDayUtc(Day0.AddDays(DeferredCharges.ProvisionalCancelAfterDays)),
            outcomeBody.GetProperty("expiresAt").GetDateTime().ToUniversalTime());
        var session = await guest.PostAsJsonAsync($"/api/public/bookings/{seeded.BookingId}/payment-session", new { token });
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.Equal($"{paymentIntentId}_secret_test", (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("clientSecret").GetString());

        // The token of the checkout (the guest's browser) was replaced by the one of the email.
        var old = await guest.PostAsJsonAsync($"/api/public/bookings/{seeded.BookingId}/outcome", new { token = seeded.CheckoutToken });
        Assert.Equal(HttpStatusCode.NotFound, old.StatusCode);
    }

    [PostgresFact]
    public async Task RunDueCharges_Processing_WaitsThenConnectWebhookSucceededCompletes()
    {
        var seeded = await SeedDeferredBookingAsync("processing");

        await RunJobAsync(day: 0);

        var paymentIntentId = Assert.Single(_stripe.CreationsOf(seeded.CustomerId)).PaymentIntentId;
        var processing = Assert.Single((await LoadBookingAsync(seeded.BookingId)).Payments);
        Assert.Equal(PaymentStatus.Processing, processing.Status);
        Assert.Null(processing.ProcessedAt);

        var succeeded = DeferredChargeEvent("payment_intent.succeeded", paymentIntentId, seeded, seeded.Account);
        await HandleWebhookAsync(succeeded, WebhookSource.Connected);
        await HandleWebhookAsync(succeeded, WebhookSource.Connected); // duplicate delivery

        var booking = await LoadBookingAsync(seeded.BookingId);
        var payment = Assert.Single(booking.Payments);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.NotNull(payment.ProcessedAt);
        Assert.Equal(BookingStatus.Confirmed, booking.Status);

        // Collected: the next days never charge it again.
        await RunJobAsync(day: 1);
        Assert.Single(_stripe.CreationsOf(seeded.CustomerId));
        Assert.Empty(_stripe.ConfirmationsOf(paymentIntentId));
        Assert.Empty(EmailsOf(seeded));
    }

    [PostgresFact]
    public async Task RunDueCharges_EveryAttemptFails_CancelsOnTheCancellationDayAndReleasesTheDates()
    {
        var seeded = await SeedDeferredBookingAsync("authentication_required", "authentication_required", "authentication_required");

        for (var day = 0; day < DeferredCharges.ProvisionalMaxAttempts; day++)
            await RunJobAsync(day);

        var paymentIntentId = Assert.Single(_stripe.CreationsOf(seeded.CustomerId)).PaymentIntentId;
        Assert.Equal(DeferredCharges.ProvisionalMaxAttempts - 1, _stripe.ConfirmationsOf(paymentIntentId).Count);
        var beforeCancellation = await LoadBookingAsync(seeded.BookingId);
        Assert.Equal(BookingStatus.Confirmed, beforeCancellation.Status);
        Assert.Equal(DeferredCharges.ProvisionalMaxAttempts, beforeCancellation.DeferredChargeAttempts);
        Assert.Equal(PaymentStatus.Failed, Assert.Single(beforeCancellation.Payments).Status);
        Assert.Contains(seeded.CheckIn.ToString("yyyy-MM-dd"), await BookedDatesAsync(seeded));
        Assert.Equal(2, EmailsOf(seeded).Count); // one failure notice, not one per attempt

        await RunJobAsync(DeferredCharges.ProvisionalCancelAfterDays);
        await RunJobAsync(DeferredCharges.ProvisionalCancelAfterDays + 1);

        var booking = await LoadBookingAsync(seeded.BookingId);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(BookingCancellationReason.DeferredPaymentNotCompleted, booking.CancellationReason);
        Assert.Equal(PaymentStatus.Canceled, Assert.Single(booking.Payments).Status);
        var cancellation = Assert.Single(_stripe.CancellationsOf(paymentIntentId));
        Assert.Equal(seeded.Account, cancellation.Account);
        Assert.Equal($"deferred-charge-cancel:{seeded.BookingId:N}:{paymentIntentId}", cancellation.IdempotencyKey);
        Assert.Contains((seeded.PaymentMethodId, seeded.Account), _stripe.Detached);
        Assert.Equal(DeferredCharges.ProvisionalMaxAttempts - 1, _stripe.ConfirmationsOf(paymentIntentId).Count);

        var bookedDates = await BookedDatesAsync(seeded);
        for (var night = seeded.CheckIn; night < seeded.CheckOut; night = night.AddDays(1))
            Assert.DoesNotContain(night.ToString("yyyy-MM-dd"), bookedDates);

        var emails = EmailsOf(seeded);
        Assert.Equal(4, emails.Count);
        var guestCancellation = Assert.Single(emails, e => e.Template == EmailTemplates.Names.GuestBookingCancelled && e.To == seeded.GuestEmail);
        Assert.Contains("il pagamento non è stato completato entro la scadenza", guestCancellation.Content.HtmlBody);
        Assert.Single(emails, e => e.Template == EmailTemplates.Names.HostDeferredChargeCancelled && e.To == seeded.HostEmail);
    }

    [PostgresFact]
    public async Task RunDueCharges_GuestPaidFromTheLinkBeforeTheCancellation_IsCompletedNotCancelled()
    {
        var seeded = await SeedDeferredBookingAsync("authentication_required");
        await RunJobAsync(day: 0);
        var paymentIntentId = Assert.Single(_stripe.CreationsOf(seeded.CustomerId)).PaymentIntentId;

        // The guest confirmed the PaymentIntent on-session and the webhook has not arrived (yet).
        _stripe.SetStatus(paymentIntentId, "succeeded");
        await RunJobAsync(DeferredCharges.ProvisionalCancelAfterDays);

        var booking = await LoadBookingAsync(seeded.BookingId);
        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        Assert.Equal(PaymentStatus.Completed, Assert.Single(booking.Payments).Status);
        Assert.Empty(_stripe.CancellationsOf(paymentIntentId));
    }

    [PostgresFact]
    public async Task RunDueCharges_TwoConcurrentRuns_CreateOnePaymentIntent()
    {
        var seeded = await SeedDeferredBookingAsync("succeeded");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _stripe.HoldCreation(seeded.CustomerId, entered, release);

        var first = RunJobAsync(day: 0);
        var second = RunJobAsync(day: 0);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // Let the other run reach the booking's lock while the first one is inside Stripe.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        release.TrySetResult();
        await Task.WhenAll(first, second);

        // The second run waited for the first one's lock and then found the charge made: it never called Stripe (an
        // idempotent replay would hide a second call behind the same PaymentIntent).
        Assert.Equal(1, _stripe.ChargeCallsOf(seeded.CustomerId));
        Assert.Single(_stripe.CreationsOf(seeded.CustomerId));
        var booking = await LoadBookingAsync(seeded.BookingId);
        Assert.Equal(1, booking.DeferredChargeAttempts);
        Assert.Equal(PaymentStatus.Completed, Assert.Single(booking.Payments).Status);
    }

    [PostgresTheory]
    [InlineData(WebhookSource.Connected)]
    [InlineData(WebhookSource.Platform)]
    public async Task Webhook_DeferredChargeKindWithAccount_IsAppliedOnBothEndpoints(WebhookSource source)
    {
        var seeded = await SeedDeferredBookingAsync("processing");
        await RunJobAsync(day: 0);
        var paymentIntentId = Assert.Single(_stripe.CreationsOf(seeded.CustomerId)).PaymentIntentId;

        // A SEPA debit that fails days later: the guest and the host are told once.
        await HandleWebhookAsync(DeferredChargeEvent("payment_intent.payment_failed", paymentIntentId, seeded, seeded.Account), source);

        var failed = await LoadBookingAsync(seeded.BookingId);
        Assert.Equal(PaymentStatus.Failed, Assert.Single(failed.Payments).Status);
        Assert.NotNull(failed.DeferredChargeFailedAt);
        Assert.Single(EmailsOf(seeded), e => e.Template == EmailTemplates.Names.GuestDeferredChargeFailed);
        Assert.Single(EmailsOf(seeded), e => e.Template == EmailTemplates.Names.HostDeferredChargeFailed);

        // The guest pays from the link.
        await HandleWebhookAsync(DeferredChargeEvent("payment_intent.succeeded", paymentIntentId, seeded, seeded.Account), source);

        var paid = await LoadBookingAsync(seeded.BookingId);
        Assert.Equal(PaymentStatus.Completed, Assert.Single(paid.Payments).Status);
        Assert.Null(paid.DeferredChargeFailedAt);
        Assert.Equal(2, EmailsOf(seeded).Count);
    }

    [PostgresTheory]
    [InlineData(WebhookSource.Connected, "acct_someone_else")]
    [InlineData(WebhookSource.Platform, null)]
    public async Task Webhook_DeferredChargeFromAnotherAccount_IsIgnored(WebhookSource source, string? account)
    {
        var seeded = await SeedDeferredBookingAsync("processing");
        await RunJobAsync(day: 0);
        var paymentIntentId = Assert.Single(_stripe.CreationsOf(seeded.CustomerId)).PaymentIntentId;

        await HandleWebhookAsync(DeferredChargeEvent("payment_intent.succeeded", paymentIntentId, seeded, account), source);

        Assert.Equal(PaymentStatus.Processing, Assert.Single((await LoadBookingAsync(seeded.BookingId)).Payments).Status);
    }

    private IReadOnlyList<(string? To, EmailContent Content, string Template)> EmailsOf(SeededBooking seeded) =>
        _emails.Snapshot()
            .Where(e => e.To == seeded.GuestEmail || e.To == seeded.HostEmail)
            .ToList();

    private static Event DeferredChargeEvent(string type, string paymentIntentId, SeededBooking seeded, string? account) => new()
    {
        Id = $"evt_bk08_{Guid.NewGuid():N}",
        Type = type,
        Account = account,
        Data = new EventData
        {
            Object = new PaymentIntent
            {
                Id = paymentIntentId,
                Amount = 35_000,
                Status = type switch
                {
                    "payment_intent.succeeded" => "succeeded",
                    "payment_intent.processing" => "processing",
                    _ => "requires_payment_method",
                },
                Metadata = new Dictionary<string, string>
                {
                    ["kind"] = DeferredCharges.Kind,
                    ["bookingId"] = seeded.BookingId.ToString(),
                },
            },
        },
    };

    /// <summary>One run of the job at 08:00 in Rome of <see cref="Day0"/> + <paramref name="day"/>, in its own DI scope.</summary>
    private async Task RunJobAsync(int day)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = TestDeferredCharges.Create(
            db, _stripe, _emails, scope.ServiceProvider.GetRequiredService<IConfiguration>(), Clock(day));
        await new DirectBookingChargeJob(service, NullLogger<DirectBookingChargeJob>.Instance).ExecuteAsync();
    }

    private async Task HandleWebhookAsync(Event stripeEvent, WebhookSource source)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var handler = new StripeWebhookHandler(
            new PaymentRepository(db),
            new BookingRepository(db),
            Mock.Of<IConnectOnboardingService>(),
            db,
            new FakeStripeBillingService(configuration),
            new EntitlementService(db, configuration),
            new VatCalculationService(),
            Mock.Of<IOssRevenueTracker>(),
            Mock.Of<ISdiEInvoiceService>(),
            Mock.Of<IRentBillingService>(),
            new PaymentRefundService(db, _stripe, Mock.Of<IPaymentRefundRetryScheduler>(), _emails, NullLogger<PaymentRefundService>.Instance),
            TestCheckoutPaymentSettlement.Create(db, stripe: _stripe, emails: _emails, configuration: configuration),
            TestDeferredCharges.Create(db, _stripe, _emails, configuration, Clock(0)),
            NullLogger<StripeWebhookHandler>.Instance);
        await handler.HandleEventAsync(stripeEvent, source);
    }

    private static FixedTimeProvider Clock(int day) =>
        new(new DateTimeOffset(RomeCalendar.StartOfDayUtc(Day0.AddDays(day)).AddHours(8), TimeSpan.Zero));

    private async Task<List<string?>> BookedDatesAsync(SeededBooking seeded)
    {
        using var anonymous = _factory.CreateClient();
        var availability = await anonymous.GetFromJsonAsync<JsonElement>(
            $"/api/public/bookings/property/{seeded.PropertyId}/availability" +
            $"?startDate={seeded.CheckIn.AddDays(-2):yyyy-MM-dd}&endDate={seeded.CheckOut.AddDays(2):yyyy-MM-dd}");
        return availability.GetProperty("bookedDates").EnumerateArray().Select(d => d.GetString()).ToList();
    }

    private sealed record SeededBooking(
        Guid BookingId,
        Guid PropertyId,
        string Account,
        string CustomerId,
        string PaymentMethodId,
        string GuestEmail,
        string HostEmail,
        string CheckoutToken,
        DateTime CheckIn,
        DateTime CheckOut);

    /// <summary>
    /// A confirmed "Paga alla scadenza" booking of a Connect-ready host whose deadline is <see cref="Day0"/>, as the checkout
    /// and the <c>setup_intent.succeeded</c> webhook leave it, and the Stripe outcomes of its successive charge attempts.
    /// </summary>
    private async Task<SeededBooking> SeedDeferredBookingAsync(params string[] outcomes)
    {
        var hostId = $"auth0|bk08-host-{Guid.NewGuid():N}";
        var account = $"acct_bk08_{Guid.NewGuid():N}";
        var hostEmail = $"host.{Guid.NewGuid():N}@example.com";
        var seededProperty = await _factory.SeedPropertyAsync(hostId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.SingleAsync(o => o.Id == seededProperty.OrgId);
        // The org has onboarded again since: the card lives on the account stored on the payment.
        org.StripeConnectedAccountId = $"acct_bk08_new_{Guid.NewGuid():N}";
        org.ConnectChargesEnabled = true;
        org.ContactEmail = hostEmail;
        var property = await db.Properties.SingleAsync(p => p.Id == seededProperty.Id);
        property.CinCode = "IT058091C27G5FFZDZ";
        property.ComplianceStatus = PropertyComplianceStatus.Active;

        var checkIn = Day0.AddDays(20);
        var token = CheckoutOutcomes.NewToken();
        var guest = new Guest
        {
            OrgId = property.OrgId,
            FirstName = "Anna",
            LastName = "Verdi",
            Email = $"anna.{Guid.NewGuid():N}@example.com",
            DataProcessingPurpose = "Direct Booking Checkout",
        };
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(3),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            PaymentOption = PaymentOption.OnCancellationDeadline,
            TotalPrice = Total,
            FreeRefundDeadline = Day0,
            StripeSetupIntentId = $"seti_bk08_{Guid.NewGuid():N}",
            StripeCustomerId = $"cus_bk08_{Guid.NewGuid():N}",
            StripePaymentMethodId = $"pm_bk08_{Guid.NewGuid():N}",
            CheckoutTokenHash = CheckoutOutcomes.HashToken(token),
        };
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        db.Payments.Add(new Payment
        {
            BookingId = booking.Id,
            OrgId = property.OrgId,
            Amount = Total,
            Status = PaymentStatus.Pending,
            Method = Casazen.Core.Entities.PaymentMethod.CreditCard,
            TransactionId = booking.StripeSetupIntentId,
            StripeAccountId = account,
            Description = DeferredCharges.PaymentDescription,
        });
        await db.SaveChangesAsync();

        _stripe.Register(booking.StripeCustomerId, outcomes);
        return new SeededBooking(
            booking.Id,
            property.Id,
            account,
            booking.StripeCustomerId,
            booking.StripePaymentMethodId,
            guest.Email,
            hostEmail,
            token,
            checkIn,
            checkIn.AddDays(3));
    }

    private async Task<Booking> LoadBookingAsync(Guid bookingId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Bookings.AsNoTracking().Include(b => b.Payments).SingleAsync(b => b.Id == bookingId);
    }
}

/// <summary>
/// Stripe of <see cref="DeferredChargePostgresTests"/>: each registered customer (one per booking) answers its charge
/// attempts with the scripted outcomes (<c>succeeded</c>, <c>processing</c>, <c>requires_action</c>, or
/// <c>authentication_required</c>: the off-session 402 with the PaymentIntent left in <c>requires_payment_method</c>).
/// Creations honour the idempotency key like Stripe. Unknown customers get an API error.
/// </summary>
internal sealed class ScriptedDeferredChargeStripe : IStripeService
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _outcomes = new();
    private readonly ConcurrentDictionary<string, (TaskCompletionSource Entered, TaskCompletionSource Release)> _held = new();
    private readonly ConcurrentDictionary<string, PaymentIntent> _intents = new();
    private readonly ConcurrentDictionary<string, string> _customerOfIntent = new();
    private readonly ConcurrentDictionary<string, string> _byIdempotencyKey = new();
    private readonly ConcurrentQueue<(string CustomerId, string Account, string IdempotencyKey, long AmountCents, string PaymentIntentId)> _creations = new();
    private readonly ConcurrentQueue<(string PaymentIntentId, string IdempotencyKey)> _confirmations = new();
    private readonly ConcurrentQueue<(string PaymentIntentId, string? Account, string IdempotencyKey)> _cancellations = new();
    private readonly ConcurrentQueue<string> _chargeCalls = new();
    private readonly object _createLock = new();

    public ConcurrentQueue<(string PaymentMethodId, string Account)> Detached { get; } = new();

    public void Register(string customerId, IEnumerable<string> outcomes) =>
        _outcomes[customerId] = new ConcurrentQueue<string>(outcomes);

    /// <summary>The creation for <paramref name="customerId"/> signals <paramref name="entered"/> and waits for <paramref name="release"/>.</summary>
    public void HoldCreation(string customerId, TaskCompletionSource entered, TaskCompletionSource release) =>
        _held[customerId] = (entered, release);

    public void SetStatus(string paymentIntentId, string status) => _intents[paymentIntentId].Status = status;

    public IReadOnlyList<(string CustomerId, string Account, string IdempotencyKey, long AmountCents, string PaymentIntentId)> CreationsOf(string customerId) =>
        _creations.Where(c => c.CustomerId == customerId).ToList();

    /// <summary>Calls of <see cref="ChargePaymentMethodAsync"/> for the customer, idempotent replays included.</summary>
    public int ChargeCallsOf(string customerId) => _chargeCalls.Count(c => c == customerId);

    public IReadOnlyList<(string PaymentIntentId, string IdempotencyKey)> ConfirmationsOf(string paymentIntentId) =>
        _confirmations.Where(c => c.PaymentIntentId == paymentIntentId).ToList();

    public IReadOnlyList<(string PaymentIntentId, string? Account, string IdempotencyKey)> CancellationsOf(string paymentIntentId) =>
        _cancellations.Where(c => c.PaymentIntentId == paymentIntentId).ToList();

    public async Task<PaymentIntent> ChargePaymentMethodAsync(
        string connectedAccountId,
        string customerId,
        string paymentMethodId,
        long amountCents,
        string currency,
        Dictionary<string, string> metadata,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var outcomes = Known(customerId);
        _chargeCalls.Enqueue(customerId);
        if (_held.TryGetValue(customerId, out var held))
        {
            held.Entered.TrySetResult();
            await held.Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }

        PaymentIntent intent;
        lock (_createLock)
        {
            if (_byIdempotencyKey.TryGetValue(idempotencyKey, out var existingId))
                return Copy(_intents[existingId]);

            intent = new PaymentIntent
            {
                Id = $"pi_bk08_{Guid.NewGuid():N}",
                Amount = amountCents,
                Currency = currency,
                CustomerId = customerId,
                Metadata = new Dictionary<string, string>(metadata),
            };
            _intents[intent.Id] = intent;
            _customerOfIntent[intent.Id] = customerId;
            _byIdempotencyKey[idempotencyKey] = intent.Id;
            _creations.Enqueue((customerId, connectedAccountId, idempotencyKey, amountCents, intent.Id));
        }

        return Answer(intent, Next(outcomes));
    }

    public Task<PaymentIntent> ConfirmPaymentIntentOffSessionAsync(
        string paymentIntentId,
        string connectedAccountId,
        string paymentMethodId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        _confirmations.Enqueue((paymentIntentId, idempotencyKey));
        var intent = _intents[paymentIntentId];
        return Task.FromResult(Answer(intent, Next(Known(_customerOfIntent[paymentIntentId]))));
    }

    public Task<PaymentIntent> GetPaymentIntentAsync(string paymentIntentId, string? connectedAccountId, CancellationToken cancellationToken = default)
    {
        if (!_intents.TryGetValue(paymentIntentId, out var intent))
            throw new StripeException(HttpStatusCode.NotFound, new StripeError { Type = "invalid_request_error", Code = "resource_missing" }, "No such payment_intent");
        return Task.FromResult(Copy(intent));
    }

    public Task<IReadOnlyList<PaymentIntent>> ListCustomerPaymentIntentsAsync(
        string customerId,
        string connectedAccountId,
        CancellationToken cancellationToken = default)
    {
        Known(customerId);
        return Task.FromResult<IReadOnlyList<PaymentIntent>>(
            _intents.Values.Where(pi => pi.CustomerId == customerId).Select(Copy).ToList());
    }

    public Task<PaymentIntent> CancelPaymentIntentAsync(
        string paymentIntentId,
        string? connectedAccountId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        _cancellations.Enqueue((paymentIntentId, connectedAccountId, idempotencyKey));
        _intents[paymentIntentId].Status = "canceled";
        return Task.FromResult(Copy(_intents[paymentIntentId]));
    }

    public Task DetachPaymentMethodAsync(string paymentMethodId, string connectedAccountId, CancellationToken cancellationToken = default)
    {
        Detached.Enqueue((paymentMethodId, connectedAccountId));
        return Task.CompletedTask;
    }

    private ConcurrentQueue<string> Known(string customerId) =>
        _outcomes.TryGetValue(customerId, out var outcomes)
            ? outcomes
            : throw new StripeException(HttpStatusCode.BadRequest, new StripeError { Type = "invalid_request_error", Code = "resource_missing" }, "No such customer");

    private static string Next(ConcurrentQueue<string> outcomes) =>
        outcomes.TryDequeue(out var outcome) ? outcome : "requires_payment_method";

    private static PaymentIntent Answer(PaymentIntent intent, string outcome)
    {
        if (outcome == "authentication_required")
        {
            intent.Status = "requires_payment_method";
            throw new StripeException(HttpStatusCode.PaymentRequired, new StripeError
            {
                Type = "card_error",
                Code = "authentication_required",
                DeclineCode = "authentication_required",
                PaymentIntent = Copy(intent),
            }, "Your card was declined. This transaction requires authentication.");
        }

        intent.Status = outcome;
        return Copy(intent);
    }

    private static PaymentIntent Copy(PaymentIntent intent) => new()
    {
        Id = intent.Id,
        Status = intent.Status,
        Amount = intent.Amount,
        Currency = intent.Currency,
        CustomerId = intent.CustomerId,
        ClientSecret = $"{intent.Id}_secret_test",
        Metadata = new Dictionary<string, string>(intent.Metadata ?? []),
    };

    public Task<PaymentIntent> CreatePaymentIntentAsync(long amount, string currency, Dictionary<string, string> metadata) =>
        throw new NotSupportedException();

    public Task<PaymentIntent> CreateConnectedAccountPaymentIntentAsync(
        string connectedAccountId, long amountCents, string currency, Dictionary<string, string> metadata) =>
        throw new NotSupportedException();

    public Task<PaymentIntent> ConfirmPaymentAsync(string paymentIntentId) => throw new NotSupportedException();

    public Task<Refund> CreateRefundAsync(StripeRefundCreateRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<Refund>> ListRefundsAsync(string paymentIntentId, string? connectedAccountId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Refund>>([]);

    public Task<SetupIntent> GetSetupIntentAsync(string setupIntentId, string connectedAccountId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<SetupIntent> CancelSetupIntentAsync(
        string setupIntentId, string connectedAccountId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<SetupIntent> CreateConnectedAccountSetupIntentAsync(
        string connectedAccountId, Dictionary<string, string> metadata, string? customerEmail = null, string? customerName = null) =>
        throw new NotSupportedException();
}
