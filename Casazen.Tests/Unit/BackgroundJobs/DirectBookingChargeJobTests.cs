using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration;
using Casazen.Tests.Unit.Email;
using Casazen.Web.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;
using Xunit;
using PropertyEntity = Casazen.Core.Entities.Property;

namespace Casazen.Tests.Unit.BackgroundJobs;

/// <summary>
/// BK-08 (A3-14): the deferred charge job on EF InMemory with a mocked <see cref="IStripeService"/>. The payment is
/// Completed only for a <c>succeeded</c> PaymentIntent, the charge is off-session on the account of the saved card with an
/// idempotency key of booking, deadline and attempt, attempts are limited to one per day and
/// <see cref="DeferredCharges.ProvisionalMaxAttempts"/> in total. Concurrency and cancellation on PostgreSQL:
/// <c>DeferredChargePostgresTests</c>.
/// </summary>
public class DirectBookingChargeJobTests
{
    private const string Account = "acct_deferred_host";
    private const string OrgAccount = "acct_org_current";

    // 2026-10-01 07:00 UTC: 09:00 in Rome, the same calendar day.
    private static readonly DateTimeOffset Day0 = new(2026, 10, 1, 7, 0, 0, TimeSpan.Zero);

    private readonly Mock<IStripeService> _stripe = new();
    private readonly RecordingEmailQueue _emails = new();

    public DirectBookingChargeJobTests()
    {
        _stripe
            .Setup(s => s.ListCustomerPaymentIntentsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    [Fact]
    public async Task ExecuteAsync_PaymentIntentSucceeded_CompletesPaymentOffSessionOnTheCardAccountOnce()
    {
        await using var context = CreateContext();
        var booking = await SeedChargeableBookingAsync(context);
        SetupCharge("succeeded", "pi_deadline_1");

        await RunJobAsync(context, Day0);
        await RunJobAsync(context, Day0.AddHours(3));

        var expectedKey = $"direct-booking-deadline:{booking.Id:N}:20261001:1";
        _stripe.Verify(s => s.ChargePaymentMethodAsync(
            Account,
            "cus_123",
            "pm_123",
            12345,
            "eur",
            It.Is<Dictionary<string, string>>(m =>
                m["bookingId"] == booking.Id.ToString() && m["kind"] == DeferredCharges.Kind),
            expectedKey,
            It.IsAny<CancellationToken>()), Times.Once);
        var payment = Assert.Single(await context.Payments.Where(p => p.BookingId == booking.Id).ToListAsync());
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Equal("pi_deadline_1", payment.TransactionId);
        Assert.Equal("pi_deadline_1", payment.StripePaymentIntentId);
        Assert.NotNull(payment.ProcessedAt);
        var stored = await context.Bookings.SingleAsync(b => b.Id == booking.Id);
        Assert.Equal(1, stored.DeferredChargeAttempts);
        Assert.Null(stored.DeferredChargeFailedAt);
        Assert.Empty(_emails.Snapshot());
    }

    [Fact]
    public async Task ExecuteAsync_PaymentIntentProcessing_MarksProcessingNotCompleted()
    {
        await using var context = CreateContext();
        var booking = await SeedChargeableBookingAsync(context);
        SetupCharge("processing", "pi_sepa_1");

        await RunJobAsync(context, Day0);

        var payment = await context.Payments.SingleAsync(p => p.BookingId == booking.Id);
        Assert.Equal(PaymentStatus.Processing, payment.Status);
        Assert.Null(payment.ProcessedAt);
        Assert.Equal("pi_sepa_1", payment.StripePaymentIntentId);
        Assert.Empty(_emails.Snapshot());
    }

    [Theory]
    [InlineData("requires_action")]
    [InlineData("requires_payment_method")]
    public async Task ExecuteAsync_PaymentIntentNeedsTheGuest_MarksFailedAndEmailsGuestLinkAndHost(string status)
    {
        await using var context = CreateContext();
        var booking = await SeedChargeableBookingAsync(context);
        SetupCharge(status, "pi_3ds_1");

        await RunJobAsync(context, Day0);

        var payment = await context.Payments.SingleAsync(p => p.BookingId == booking.Id);
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Null(payment.ProcessedAt);
        var stored = await context.Bookings.SingleAsync(b => b.Id == booking.Id);
        Assert.Equal(Day0.UtcDateTime, stored.DeferredChargeFailedAt);
        Assert.Equal(BookingStatus.Confirmed, stored.Status);
        AssertFailureEmails(stored);
    }

    [Fact]
    public async Task ExecuteAsync_AuthenticationRequiredDecline_UsesThePaymentIntentOfTheError()
    {
        await using var context = CreateContext();
        var booking = await SeedChargeableBookingAsync(context);
        _stripe
            .Setup(s => s.ChargePaymentMethodAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<Dictionary<string, string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(AuthenticationRequired("pi_declined_1"));

        await RunJobAsync(context, Day0);

        var payment = await context.Payments.SingleAsync(p => p.BookingId == booking.Id);
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal("pi_declined_1", payment.StripePaymentIntentId);
        AssertFailureEmails(await context.Bookings.SingleAsync(b => b.Id == booking.Id));
    }

    [Fact]
    public async Task ExecuteAsync_FailedAttemptOnNextDay_ConfirmsTheSamePaymentIntentAgainOffSessionWithoutNewEmails()
    {
        await using var context = CreateContext();
        var booking = await SeedChargeableBookingAsync(context);
        SetupCharge("requires_payment_method", "pi_retry_1");
        await RunJobAsync(context, Day0);
        _emails.Queued.Clear();
        _stripe
            .Setup(s => s.GetPaymentIntentAsync("pi_retry_1", Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentIntent { Id = "pi_retry_1", Status = "requires_payment_method" });
        _stripe
            .Setup(s => s.ConfirmPaymentIntentOffSessionAsync("pi_retry_1", Account, "pm_123", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentIntent { Id = "pi_retry_1", Status = "succeeded", Amount = 12345 });

        await RunJobAsync(context, Day0.AddDays(1));

        _stripe.Verify(s => s.ConfirmPaymentIntentOffSessionAsync(
            "pi_retry_1", Account, "pm_123", "direct-booking-deadline-confirm:pi_retry_1:2", It.IsAny<CancellationToken>()), Times.Once);
        _stripe.Verify(s => s.ChargePaymentMethodAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<Dictionary<string, string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        var payment = await context.Payments.SingleAsync(p => p.BookingId == booking.Id);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        var stored = await context.Bookings.SingleAsync(b => b.Id == booking.Id);
        Assert.Null(stored.DeferredChargeFailedAt);
        Assert.Equal(2, stored.DeferredChargeAttempts);
        Assert.Empty(_emails.Snapshot());
    }

    [Fact]
    public async Task ExecuteAsync_StripeNeverAnswers_StopsAfterMaxAttemptsAndAlertsTheHostOnly()
    {
        await using var context = CreateContext();
        var booking = await SeedChargeableBookingAsync(context);
        _stripe
            .Setup(s => s.ChargePaymentMethodAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<Dictionary<string, string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StripeException(System.Net.HttpStatusCode.ServiceUnavailable, new StripeError { Type = "api_error" }, "down"));

        for (var day = 0; day < DeferredCharges.ProvisionalMaxAttempts + 2; day++)
            await RunJobAsync(context, Day0.AddDays(day));

        _stripe.Verify(s => s.ChargePaymentMethodAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<Dictionary<string, string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(DeferredCharges.ProvisionalMaxAttempts));
        // Each attempt looked for a PaymentIntent whose answer was lost before creating one.
        _stripe.Verify(s => s.ListCustomerPaymentIntentsAsync("cus_123", Account, It.IsAny<CancellationToken>()),
            Times.Exactly(DeferredCharges.ProvisionalMaxAttempts));
        var stored = await context.Bookings.Include(b => b.Payments).SingleAsync(b => b.Id == booking.Id);
        Assert.Equal(DeferredCharges.ProvisionalMaxAttempts, stored.DeferredChargeAttempts);
        Assert.Equal(PaymentStatus.Pending, Assert.Single(stored.Payments).Status);
        Assert.Equal(BookingStatus.Confirmed, stored.Status);
        var email = Assert.Single(_emails.Snapshot());
        Assert.Equal(EmailTemplates.Names.HostDeferredChargeFailed, email.Template);
        Assert.Equal("host@example.com", email.To);
        Assert.Contains("contatta l'ospite", email.Content.HtmlBody);
    }

    [Fact]
    public async Task ExecuteAsync_AnswerOfAnEarlierAttemptLost_AdoptsThatPaymentIntentInsteadOfChargingAgain()
    {
        await using var context = CreateContext();
        var booking = await SeedChargeableBookingAsync(context);
        _stripe
            .Setup(s => s.ListCustomerPaymentIntentsAsync("cus_123", Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new PaymentIntent { Id = "pi_other", Status = "succeeded", Metadata = new() { ["kind"] = "direct-booking" } },
                new PaymentIntent
                {
                    Id = "pi_lost",
                    Status = "succeeded",
                    Amount = 12345,
                    Metadata = new() { ["kind"] = DeferredCharges.Kind, ["bookingId"] = booking.Id.ToString() },
                },
            ]);

        await RunJobAsync(context, Day0);

        _stripe.Verify(s => s.ChargePaymentMethodAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<Dictionary<string, string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var payment = await context.Payments.SingleAsync(p => p.BookingId == booking.Id);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Equal("pi_lost", payment.StripePaymentIntentId);
    }

    [Theory]
    [InlineData(PaymentStatus.Completed, DeferredCharges.LegacyPaymentDescription)]
    [InlineData(PaymentStatus.Refunded, DeferredCharges.PaymentDescription)]
    [InlineData(PaymentStatus.PartiallyRefunded, DeferredCharges.PaymentDescription)]
    [InlineData(PaymentStatus.Canceled, DeferredCharges.PaymentDescription)]
    public async Task ExecuteAsync_DeferredChargeCollectedOrCanceled_NeverChargesAgain(PaymentStatus status, string description)
    {
        await using var context = CreateContext();
        var booking = await SeedChargeableBookingAsync(
            context, paymentStatus: status, paymentDescription: description, transactionId: "pi_existing_deadline");

        await RunJobAsync(context, Day0);

        _stripe.VerifyNoOtherCalls();
        var payment = Assert.Single(await context.Payments.Where(p => p.BookingId == booking.Id).ToListAsync());
        Assert.Equal(status, payment.Status);
    }

    [Theory]
    [InlineData(BookingStatus.CheckedIn)]
    [InlineData(BookingStatus.CheckedOut)]
    public async Task ExecuteAsync_DeferredBookingPastConfirmationWithoutCompletedDeadlineCharge_Charges(BookingStatus status)
    {
        await using var context = CreateContext();
        var booking = await SeedChargeableBookingAsync(context, bookingStatus: status);
        SetupCharge("succeeded", $"pi_deadline_{status}");

        await RunJobAsync(context, Day0);

        var payment = Assert.Single(await context.Payments.Where(p => p.BookingId == booking.Id).ToListAsync());
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Equal($"pi_deadline_{status}", payment.StripePaymentIntentId);
    }

    [Fact]
    public async Task ExecuteAsync_DeadlineDay_IsTheCalendarDayInRome()
    {
        await using var context = CreateContext();
        var booking = await SeedChargeableBookingAsync(context, deadline: new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc));
        SetupCharge("succeeded", "pi_rome_day");

        // 21:30 UTC on 1 October is 23:30 in Rome: the deadline (2 October) has not come yet.
        await RunJobAsync(context, new DateTimeOffset(2026, 10, 1, 21, 30, 0, TimeSpan.Zero));
        Assert.Equal(PaymentStatus.Pending, (await context.Payments.SingleAsync(p => p.BookingId == booking.Id)).Status);

        // 22:30 UTC is 00:30 of 2 October in Rome.
        await RunJobAsync(context, new DateTimeOffset(2026, 10, 1, 22, 30, 0, TimeSpan.Zero));
        Assert.Equal(PaymentStatus.Completed, (await context.Payments.SingleAsync(p => p.BookingId == booking.Id)).Status);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationDayMissedUntilTheCheckInDay_NeverCancelsTheStay()
    {
        await using var context = CreateContext();
        // Failure on 1 October, cancellation day 4 October, check-in 6 October; no run between 2 and 5 October.
        var booking = await SeedChargeableBookingAsync(context, checkIn: new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc));
        SetupCharge("requires_payment_method", "pi_missed_1");
        await RunJobAsync(context, Day0);
        _stripe
            .Setup(s => s.GetPaymentIntentAsync("pi_missed_1", Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentIntent { Id = "pi_missed_1", Status = "requires_payment_method" });
        _stripe
            .Setup(s => s.ConfirmPaymentIntentOffSessionAsync("pi_missed_1", Account, "pm_123", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentIntent { Id = "pi_missed_1", Status = "requires_payment_method" });

        await RunJobAsync(context, Day0.AddDays(5));

        _stripe.Verify(s => s.CancelPaymentIntentAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var stored = await context.Bookings.Include(b => b.Payments).SingleAsync(b => b.Id == booking.Id);
        Assert.Equal(BookingStatus.Confirmed, stored.Status);
        Assert.Equal(PaymentStatus.Failed, Assert.Single(stored.Payments).Status);
    }

    private void SetupCharge(string status, string paymentIntentId) =>
        _stripe
            .Setup(s => s.ChargePaymentMethodAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<Dictionary<string, string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentIntent { Id = paymentIntentId, Status = status, Amount = 12345 });

    private void AssertFailureEmails(Booking booking)
    {
        var emails = _emails.Snapshot();
        Assert.Equal(2, emails.Count);
        var guest = Assert.Single(emails, e => e.Template == EmailTemplates.Names.GuestDeferredChargeFailed);
        Assert.Equal("ada@example.com", guest.To);
        var host = Assert.Single(emails, e => e.Template == EmailTemplates.Names.HostDeferredChargeFailed);
        Assert.Equal("host@example.com", host.To);

        // The link opens the outcome page with a new checkout token whose hash is stored on the booking.
        var match = System.Text.RegularExpressions.Regex.Match(
            guest.Content.HtmlBody, $"href=\"https://casazen-app\\.test/book/[^/]+/booking/{booking.Id:D}\\?token=([A-Za-z0-9_-]+)\"");
        Assert.True(match.Success, guest.Content.HtmlBody);
        Assert.True(CheckoutOutcomes.TokenMatches(booking.CheckoutTokenHash, match.Groups[1].Value));
        // Check-in on 31 October, first failure on 1 October: cancelled on 4 October, so the guest pays by 3 October.
        Assert.Contains("entro il <strong>03/10/2026</strong>", guest.Content.HtmlBody);
        Assert.Contains("il <strong>04/10/2026</strong>", host.Content.HtmlBody);
    }

    private static StripeException AuthenticationRequired(string paymentIntentId) =>
        new(System.Net.HttpStatusCode.PaymentRequired, new StripeError
        {
            Type = "card_error",
            Code = "authentication_required",
            DeclineCode = "authentication_required",
            PaymentIntent = new PaymentIntent { Id = paymentIntentId, Status = "requires_payment_method" },
        }, "Your card was declined. This transaction requires authentication.");

    private async Task RunJobAsync(AppDbContext context, DateTimeOffset now)
    {
        var service = new DeferredChargeService(
            context,
            _stripe.Object,
            new BookingNotifier(context, _emails, EmailTestHelpers.Links(), Mock.Of<IPushNotificationService>(), NullLogger<BookingNotifier>.Instance),
            new ConfigurationBuilder().AddInMemoryCollection().Build(),
            NullLogger<DeferredChargeService>.Instance,
            new FixedTimeProvider(now));
        await new DirectBookingChargeJob(service, NullLogger<DirectBookingChargeJob>.Instance).ExecuteAsync();
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new AppDbContext(options);
    }

    private static async Task<Booking> SeedChargeableBookingAsync(
        AppDbContext context,
        BookingStatus bookingStatus = BookingStatus.Confirmed,
        PaymentStatus paymentStatus = PaymentStatus.Pending,
        string paymentDescription = DeferredCharges.PaymentDescription,
        string transactionId = "seti_pending",
        DateTime? deadline = null,
        DateTime? checkIn = null)
    {
        var org = new OrgEntity
        {
            Id = Guid.NewGuid(),
            Name = "CasaZen Host",
            DisplayName = "CasaZen Host",
            Slug = $"host-{Guid.NewGuid():N}",
            StripeConnectedAccountId = OrgAccount,
            ConnectChargesEnabled = true,
            ContactEmail = "host@example.com",
        };
        var property = new PropertyEntity
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            Org = org,
            OwnerId = "owner-1",
            Name = "Apartment",
            Address = "Via Roma 1",
            City = "Rome",
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 100m,
        };
        var guest = new Guest
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
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
            CheckInDate = checkIn ?? new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = (checkIn ?? new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc)).AddDays(3),
            NumberOfGuests = 2,
            Status = bookingStatus,
            Source = BookingSource.Direct,
            TotalPrice = 123.45m,
            PaymentOption = PaymentOption.OnCancellationDeadline,
            FreeRefundDeadline = deadline ?? RomeCalendar.TodayAt(Day0),
            StripeSetupIntentId = "seti_pending",
            StripeCustomerId = "cus_123",
            StripePaymentMethodId = "pm_123",
        };
        var collected = DeferredCharges.IsCollected(paymentStatus) || paymentStatus == PaymentStatus.Canceled;
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            BookingId = booking.Id,
            Booking = booking,
            OrgId = org.Id,
            Org = org,
            Amount = booking.TotalPrice,
            Status = paymentStatus,
            Method = Casazen.Core.Entities.PaymentMethod.CreditCard,
            TransactionId = transactionId,
            StripePaymentIntentId = collected ? transactionId : null,
            // The account the SetupIntent saved the card on, which differs from the org's current one.
            StripeAccountId = Account,
            Description = paymentDescription,
            ProcessedAt = collected ? DateTime.UtcNow : null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        context.AddRange(org, property, guest, booking, payment);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return booking;
    }
}
