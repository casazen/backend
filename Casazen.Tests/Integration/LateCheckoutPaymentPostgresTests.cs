using System.Collections.Concurrent;
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
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Stripe;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// BK-04 (A3-04) on real PostgreSQL: a checkout payment that succeeds after its hold expired, or on a cancelled booking,
/// is confirmed again when the dates are still free, and otherwise refunded in full on the Stripe account the
/// PaymentIntent lives on (Connect: <c>Stripe-Account</c> of the host; platform endpoint without account: none), with an
/// idempotency key bound to the PaymentIntent and a guest email; a confirmed booking gets the standard confirmation
/// emails of BK-10 (guest and host), with the late-payment note when it was confirmed again. Duplicate events refund once, and a late payment racing
/// a new checkout of the same dates leaves exactly one booking on them.
/// </summary>
public class LateCheckoutPaymentPostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string ConsentVersion = "2026-06-direct-checkout-v1";
    private const decimal HoldAmount = 350m;

    /// <summary><c>Org.ContactEmail</c> of the orgs seeded by <see cref="CasazenWebApplicationFactory"/>: the host's emails.</summary>
    private const string SeededHostEmail = "owner@example.com";

    private readonly CasazenWebApplicationFactory _factory;
    private readonly Mock<IStripeService> _stripe = new();
    private readonly Mock<IEmailQueue> _emails = new();
    private readonly Mock<IPaymentRefundRetryScheduler> _refundRetries = new();
    private readonly ConcurrentQueue<(string? To, string Template)> _sentEmails = new();

    public LateCheckoutPaymentPostgresTests(CasazenWebApplicationFactory factory)
    {
        _factory = factory;
        _stripe
            .Setup(s => s.CreateRefundAsync(It.IsAny<StripeRefundCreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StripeRefundCreateRequest request, CancellationToken _) => new Refund
            {
                Id = $"re_{Guid.NewGuid():N}",
                PaymentIntentId = request.PaymentIntentId,
                Amount = request.AmountCents,
                Status = "succeeded",
                Metadata = new Dictionary<string, string>(request.Metadata),
            });
        _emails
            .Setup(q => q.Enqueue(It.IsAny<string?>(), It.IsAny<EmailContent>(), It.IsAny<string>()))
            .Callback<string?, EmailContent, string>((to, _, template) => _sentEmails.Enqueue((to, template)))
            .Returns(true);
    }

    [PostgresFact]
    public async Task HandleEventAsync_ConnectPaymentAfterHoldExpiredWithFreeDates_ReconfirmsBookingAndEmailsGuest()
    {
        var (property, account) = await SeedCheckoutReadyPropertyAsync();
        var late = await SeedExpiredHoldAsync(property, account, NextYear(10, 1), NextYear(10, 5));

        await HandleAsync(Succeeded(late.PaymentIntentId, account), WebhookSource.Connected);

        var booking = await LoadBookingAsync(late.BookingId);
        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        Assert.Null(booking.CancellationReason);
        var payment = Assert.Single(booking.Payments);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Equal(account, payment.StripeAccountId);
        Assert.False(payment.StripeIntentOnPlatform);
        NoRefundRequested();
        Assert.Equal([
            (late.GuestEmail, EmailTemplates.Names.GuestBookingConfirmed),
            (SeededHostEmail, EmailTemplates.Names.HostBookingConfirmed),
        ], _sentEmails.ToList());
    }

    [PostgresFact]
    public async Task HandleEventAsync_ConnectPaymentAfterDatesTakenByAnotherGuest_RefundsInFullOnConnectedAccountAndEmailsGuest()
    {
        var (property, account) = await SeedCheckoutReadyPropertyAsync();
        var late = await SeedExpiredHoldAsync(property, account, NextYear(10, 1), NextYear(10, 5));
        var other = await SeedBookingAsync(property, account, NextYear(10, 3), NextYear(10, 7), BookingStatus.Confirmed,
            minutesAgo: 5, paymentStatus: PaymentStatus.Completed);

        await HandleAsync(Succeeded(late.PaymentIntentId, account), WebhookSource.Connected);

        _stripe.Verify(s => s.CreateRefundAsync(
            It.Is<StripeRefundCreateRequest>(r =>
                r.PaymentIntentId == late.PaymentIntentId &&
                r.ConnectedAccountId == account &&
                r.AmountCents == 35_000 &&
                r.IdempotencyKey == $"late-payment-refund:{late.PaymentIntentId}"),
            It.IsAny<CancellationToken>()), Times.Once);
        _refundRetries.Verify(r => r.ScheduleSubmit(It.IsAny<Guid>()), Times.Once);

        var booking = await LoadBookingAsync(late.BookingId);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(BookingCancellationReason.CheckoutHoldExpired, booking.CancellationReason);
        var payment = Assert.Single(booking.Payments);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal(HoldAmount, payment.RefundedAmount);
        var refund = await SingleRefundAsync(payment.Id);
        Assert.Equal(PaymentRefundStatus.Succeeded, refund.Status);
        Assert.Equal(PaymentRefundOrigin.BookingCancellation, refund.Origin);
        Assert.Equal(HoldAmount, refund.Amount);
        Assert.NotNull(refund.GuestNotifiedAt);
        Assert.Equal([(late.GuestEmail, EmailTemplates.Names.GuestPaymentRefundedDatesUnavailable)], _sentEmails.ToList());
        Assert.Equal(BookingStatus.Confirmed, (await LoadBookingAsync(other.BookingId)).Status);
    }

    [PostgresFact]
    public async Task HandleEventAsync_PaymentOnExpiredPendingHoldOverlappingICalBlock_CancelsBookingAndRefunds()
    {
        var (property, account) = await SeedCheckoutReadyPropertyAsync();
        var late = await SeedBookingAsync(property, account, NextYear(10, 1), NextYear(10, 5), BookingStatus.Pending,
            minutesAgo: 40, paymentStatus: PaymentStatus.Pending);
        await SeedCalendarBlockAsync(property, NextYear(10, 4), NextYear(10, 8));

        await HandleAsync(Succeeded(late.PaymentIntentId, account), WebhookSource.Connected);

        var booking = await LoadBookingAsync(late.BookingId);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(BookingCancellationReason.DatesUnavailableAtPayment, booking.CancellationReason);
        Assert.Equal(PaymentStatus.Refunded, Assert.Single(booking.Payments).Status);
        _stripe.Verify(s => s.CreateRefundAsync(
            It.Is<StripeRefundCreateRequest>(r => r.PaymentIntentId == late.PaymentIntentId && r.ConnectedAccountId == account),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [PostgresFact]
    public async Task HandleEventAsync_PaymentOnHoldStillValid_ConfirmsWithTheStandardEmailsOnly()
    {
        // A hold within its TTL kept its dates in the iCal export: a block imported meanwhile does not undo the payment.
        var (property, account) = await SeedCheckoutReadyPropertyAsync();
        var hold = await SeedBookingAsync(property, account, NextYear(10, 1), NextYear(10, 5), BookingStatus.Pending,
            minutesAgo: 5, paymentStatus: PaymentStatus.Pending);
        await SeedCalendarBlockAsync(property, NextYear(10, 4), NextYear(10, 8));

        await HandleAsync(Succeeded(hold.PaymentIntentId, account), WebhookSource.Connected);

        var booking = await LoadBookingAsync(hold.BookingId);
        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        Assert.Equal(PaymentStatus.Completed, Assert.Single(booking.Payments).Status);
        NoRefundRequested();
        // BK-10: the standard confirmation (guest) and new booking (host); no refund email.
        Assert.Equal(
            [
                (hold.GuestEmail, EmailTemplates.Names.GuestBookingConfirmed),
                (SeededHostEmail, EmailTemplates.Names.HostBookingConfirmed),
            ],
            _sentEmails.ToList());
    }

    [PostgresFact]
    public async Task HandleEventAsync_SameLatePaymentDeliveredTwiceAndResent_RefundsOnce()
    {
        var (property, account) = await SeedCheckoutReadyPropertyAsync();
        var late = await SeedExpiredHoldAsync(property, account, NextYear(10, 1), NextYear(10, 5));
        await SeedBookingAsync(property, account, NextYear(10, 2), NextYear(10, 4), BookingStatus.Confirmed,
            minutesAgo: 5, paymentStatus: PaymentStatus.Completed);
        var stripeEvent = Succeeded(late.PaymentIntentId, account);

        // Two workers on the same delivery at once, the same event again later, then another event of the same payment.
        await Task.WhenAll(
            Task.Run(() => HandleAsync(stripeEvent, WebhookSource.Connected)),
            Task.Run(() => HandleAsync(stripeEvent, WebhookSource.Connected)));
        await HandleAsync(stripeEvent, WebhookSource.Connected);
        await HandleAsync(Succeeded(late.PaymentIntentId, account), WebhookSource.Connected);

        _stripe.Verify(s => s.CreateRefundAsync(It.IsAny<StripeRefundCreateRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        var booking = await LoadBookingAsync(late.BookingId);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        var payment = Assert.Single(booking.Payments);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal(HoldAmount, payment.RefundedAmount);
        await SingleRefundAsync(payment.Id);
        Assert.Single(_sentEmails);
    }

    [PostgresFact]
    public async Task HandleEventAsync_LatePaymentQueuedBeforeConcurrentCheckout_ReconfirmsAndCheckoutGets409()
    {
        var (property, account) = await SeedCheckoutReadyPropertyAsync();
        var late = await SeedExpiredHoldAsync(property, account, NextYear(11, 1), NextYear(11, 5));

        var (payment, checkout) = await RaceOnPropertyLockAsync(property.Id, late.PaymentIntentId, account, paymentFirst: true);

        await payment;
        var response = await checkout;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(BookingStatus.Confirmed, (await LoadBookingAsync(late.BookingId)).Status);
        NoRefundRequested();
        Assert.Equal(1, await CountBookingsTakingDatesAsync(property.Id, NextYear(11, 1), NextYear(11, 5)));
    }

    [PostgresFact]
    public async Task HandleEventAsync_ConcurrentCheckoutQueuedBeforeLatePayment_RefundsLatePayment()
    {
        var (property, account) = await SeedCheckoutReadyPropertyAsync();
        var late = await SeedExpiredHoldAsync(property, account, NextYear(11, 1), NextYear(11, 5));

        var (payment, checkout) = await RaceOnPropertyLockAsync(property.Id, late.PaymentIntentId, account, paymentFirst: false);

        var response = await checkout;
        await payment;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var booking = await LoadBookingAsync(late.BookingId);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(PaymentStatus.Refunded, Assert.Single(booking.Payments).Status);
        _stripe.Verify(s => s.CreateRefundAsync(
            It.Is<StripeRefundCreateRequest>(r => r.PaymentIntentId == late.PaymentIntentId && r.ConnectedAccountId == account),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(1, await CountBookingsTakingDatesAsync(property.Id, NextYear(11, 1), NextYear(11, 5)));
    }

    [PostgresFact]
    public async Task HandleEventAsync_PlatformPaymentWithoutAccountAfterDatesTaken_RefundsOnPlatformAccount()
    {
        var (property, account) = await SeedCheckoutReadyPropertyAsync();
        var late = await SeedExpiredHoldAsync(property, paymentAccount: null, NextYear(12, 1), NextYear(12, 5));
        await SeedBookingAsync(property, account, NextYear(12, 1), NextYear(12, 3), BookingStatus.Confirmed,
            minutesAgo: 5, paymentStatus: PaymentStatus.Completed);

        await HandleAsync(Succeeded(late.PaymentIntentId, account: null, kind: null), WebhookSource.Platform);

        // A PaymentIntent reported by the platform endpoint without account lives on the platform: no Stripe-Account.
        _stripe.Verify(s => s.CreateRefundAsync(
            It.Is<StripeRefundCreateRequest>(r =>
                r.PaymentIntentId == late.PaymentIntentId &&
                r.ConnectedAccountId == null &&
                r.AmountCents == 35_000 &&
                r.IdempotencyKey == $"late-payment-refund:{late.PaymentIntentId}"),
            It.IsAny<CancellationToken>()), Times.Once);
        var booking = await LoadBookingAsync(late.BookingId);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        var payment = Assert.Single(booking.Payments);
        Assert.True(payment.StripeIntentOnPlatform);
        Assert.Null(payment.StripeAccountId);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal([(late.GuestEmail, EmailTemplates.Names.GuestPaymentRefundedDatesUnavailable)], _sentEmails.ToList());
    }

    [PostgresFact]
    public async Task HandleEventAsync_PlatformPaymentWithoutAccountWithFreeDates_Reconfirms()
    {
        var (property, _) = await SeedCheckoutReadyPropertyAsync();
        var late = await SeedExpiredHoldAsync(property, paymentAccount: null, NextYear(12, 1), NextYear(12, 5));

        await HandleAsync(Succeeded(late.PaymentIntentId, account: null, kind: null), WebhookSource.Platform);

        var booking = await LoadBookingAsync(late.BookingId);
        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        var payment = Assert.Single(booking.Payments);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.True(payment.StripeIntentOnPlatform);
        NoRefundRequested();
        Assert.Equal([
            (late.GuestEmail, EmailTemplates.Names.GuestBookingConfirmed),
            (SeededHostEmail, EmailTemplates.Names.HostBookingConfirmed),
        ], _sentEmails.ToList());
    }

    [PostgresFact]
    public async Task HandleEventAsync_PlatformEndpointEventOfConnectedAccount_RefundsOnThatAccount()
    {
        // The platform endpoint also subscribed to connected-account events: the event carries the account.
        var (property, account) = await SeedCheckoutReadyPropertyAsync();
        var late = await SeedExpiredHoldAsync(property, account, NextYear(12, 10), NextYear(12, 14));
        await SeedBookingAsync(property, account, NextYear(12, 12), NextYear(12, 16), BookingStatus.Confirmed,
            minutesAgo: 5, paymentStatus: PaymentStatus.Completed);

        await HandleAsync(Succeeded(late.PaymentIntentId, account), WebhookSource.Platform);

        _stripe.Verify(s => s.CreateRefundAsync(
            It.Is<StripeRefundCreateRequest>(r => r.PaymentIntentId == late.PaymentIntentId && r.ConnectedAccountId == account),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(Assert.Single((await LoadBookingAsync(late.BookingId)).Payments).StripeIntentOnPlatform);
    }

    [PostgresFact]
    public async Task HandleEventAsync_PaymentReportedByAnotherConnectedAccount_IsIgnored()
    {
        var (property, account) = await SeedCheckoutReadyPropertyAsync();
        var late = await SeedExpiredHoldAsync(property, account, NextYear(12, 20), NextYear(12, 24));

        await HandleAsync(Succeeded(late.PaymentIntentId, "acct_someone_else"), WebhookSource.Connected);

        var booking = await LoadBookingAsync(late.BookingId);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(PaymentStatus.Canceled, Assert.Single(booking.Payments).Status);
        NoRefundRequested();
        Assert.Empty(_sentEmails);
    }

    /// <summary>
    /// Holds the property lock of the booking checks from another connection, queues the late payment and a new checkout
    /// of the same dates behind it in the given order, then releases it: PostgreSQL grants the waiters in order.
    /// </summary>
    private async Task<(Task Payment, Task<HttpResponseMessage> Checkout)> RaceOnPropertyLockAsync(
        Guid propertyId,
        string paymentIntentId,
        string account,
        bool paymentFirst)
    {
        await using var holder = new NpgsqlConnection(await ConnectionStringAsync());
        await holder.OpenAsync();
        await using var holderTransaction = await holder.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", holder, holderTransaction))
        {
            lockCommand.Parameters.AddWithValue("key", BitConverter.ToInt64(propertyId.ToByteArray(), 0));
            await lockCommand.ExecuteNonQueryAsync();
        }

        var guest = _factory.CreateClient();
        Task StartPayment() => Task.Run(() => HandleAsync(Succeeded(paymentIntentId, account), WebhookSource.Connected));
        Task<HttpResponseMessage> StartCheckout() => Task.Run(() =>
            guest.PostAsJsonAsync("/api/public/bookings", CheckoutPayload(propertyId, NextYear(11, 2), NextYear(11, 4))));

        Task payment;
        Task<HttpResponseMessage> checkout;
        if (paymentFirst)
        {
            payment = StartPayment();
            await WaitForAdvisoryLockWaitersAsync(1);
            checkout = StartCheckout();
        }
        else
        {
            checkout = StartCheckout();
            await WaitForAdvisoryLockWaitersAsync(1);
            payment = StartPayment();
        }

        await WaitForAdvisoryLockWaitersAsync(2);
        await holderTransaction.CommitAsync();
        await Task.WhenAll(payment, checkout);
        guest.Dispose();
        return (payment, checkout);
    }

    private async Task WaitForAdvisoryLockWaitersAsync(int count)
    {
        await using var connection = new NpgsqlConnection(await ConnectionStringAsync());
        await connection.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_locks l JOIN pg_database d ON d.oid = l.database " +
                "WHERE l.locktype = 'advisory' AND NOT l.granted AND d.datname = current_database()",
                connection);
            if ((long)(await command.ExecuteScalarAsync())! >= count)
                return;

            await Task.Delay(25);
        }

        throw new TimeoutException($"Fewer than {count} sessions waited on the property lock.");
    }

    private async Task<string> ConnectionStringAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetConnectionString()!;
    }

    /// <summary>One delivery of the event, in its own DI scope (own DbContext), as the Hangfire webhook job runs it.</summary>
    private async Task HandleAsync(Event stripeEvent, WebhookSource source)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AppDbContext>();
        var configuration = services.GetRequiredService<IConfiguration>();
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
            new PaymentRefundService(db, _stripe.Object, _refundRetries.Object, _emails.Object, NullLogger<PaymentRefundService>.Instance),
            TestCheckoutPaymentSettlement.Create(
                db,
                stripe: _stripe.Object,
                emails: _emails.Object,
                retryScheduler: _refundRetries.Object,
                configuration: configuration),
            TestDeferredCharges.Create(db),
            NullLogger<StripeWebhookHandler>.Instance);
        await handler.HandleEventAsync(stripeEvent, source);
    }

    private void NoRefundRequested() =>
        _stripe.Verify(s => s.CreateRefundAsync(It.IsAny<StripeRefundCreateRequest>(), It.IsAny<CancellationToken>()), Times.Never);

    private static Event Succeeded(string paymentIntentId, string? account, string? kind = "direct-booking") => new()
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
                Metadata = kind is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string> { ["kind"] = kind },
            },
        },
    };

    private static DateTime NextYear(int month, int day) =>
        new(TimeProvider.System.TodayInRome().Year + 1, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static object CheckoutPayload(Guid propertyId, DateTime checkIn, DateTime checkOut) => new
    {
        propertyId,
        checkInDate = checkIn.ToString("yyyy-MM-dd"),
        checkOutDate = checkOut.ToString("yyyy-MM-dd"),
        numberOfAdults = 2,
        numberOfChildren = 0,
        guest = new
        {
            firstName = "Giulia",
            lastName = "Bianchi",
            email = $"giulia.{Guid.NewGuid():N}@example.com",
            phone = "+393339876543",
            country = "IT",
        },
        consent = new { dataProcessing = true, consentVersion = ConsentVersion },
        paymentOption = nameof(PaymentOption.Immediate),
    };

    /// <summary>An org with its own connected account (Connect ready) and an active, bookable property.</summary>
    private async Task<(Property Property, string Account)> SeedCheckoutReadyPropertyAsync()
    {
        var account = $"acct_bk04_{Guid.NewGuid():N}";
        var seeded = await _factory.SeedPropertyAsync($"auth0|bk04-host-{Guid.NewGuid():N}");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.SingleAsync(o => o.Id == seeded.OrgId);
        org.StripeConnectedAccountId = account;
        org.ConnectChargesEnabled = true;
        var property = await db.Properties.SingleAsync(p => p.Id == seeded.Id);
        property.CinCode = "IT058091C27G5FFZDZ";
        property.ComplianceStatus = PropertyComplianceStatus.Active;
        await db.SaveChangesAsync();
        return (property, account);
    }

    private sealed record SeededBooking(Guid BookingId, string PaymentIntentId, string GuestEmail);

    /// <summary>A checkout hold the expiry job has already released (BK-21): cancelled, payment row canceled.</summary>
    private Task<SeededBooking> SeedExpiredHoldAsync(Property property, string? paymentAccount, DateTime checkIn, DateTime checkOut) =>
        SeedBookingAsync(property, paymentAccount, checkIn, checkOut, BookingStatus.Cancelled, minutesAgo: 40,
            paymentStatus: PaymentStatus.Canceled, BookingCancellationReason.CheckoutHoldExpired);

    private async Task<SeededBooking> SeedBookingAsync(
        Property property,
        string? paymentAccount,
        DateTime checkIn,
        DateTime checkOut,
        BookingStatus status,
        int minutesAgo,
        PaymentStatus paymentStatus,
        BookingCancellationReason? cancellationReason = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var createdAt = DateTime.UtcNow.AddMinutes(-minutesAgo);
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
            CheckOutDate = checkOut,
            NumberOfGuests = 2,
            Status = status,
            CancellationReason = cancellationReason,
            Source = BookingSource.Direct,
            PaymentOption = PaymentOption.Immediate,
            TotalPrice = HoldAmount,
            FreeRefundDeadline = checkIn.AddDays(-7),
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        var paymentIntentId = $"pi_bk04_{Guid.NewGuid():N}";
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        db.Payments.Add(new Payment
        {
            BookingId = booking.Id,
            OrgId = property.OrgId,
            Amount = booking.TotalPrice,
            Status = paymentStatus,
            TransactionId = paymentIntentId,
            StripePaymentIntentId = paymentIntentId,
            StripeAccountId = paymentAccount,
            Description = "Direct checkout - immediate payment",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        await db.SaveChangesAsync();
        return new SeededBooking(booking.Id, paymentIntentId, guest.Email);
    }

    private async Task SeedCalendarBlockAsync(Property property, DateTime start, DateTime end)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.CalendarBlocks.Add(new CalendarBlock
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            ExternalUid = $"airbnb-{Guid.NewGuid():N}",
            StartUtc = start,
            EndUtc = end,
            Summary = "Reserved",
        });
        await db.SaveChangesAsync();
    }

    private async Task<Booking> LoadBookingAsync(Guid bookingId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Bookings.AsNoTracking().Include(b => b.Payments).SingleAsync(b => b.Id == bookingId);
    }

    private async Task<PaymentRefund> SingleRefundAsync(Guid paymentId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PaymentRefunds.AsNoTracking().SingleAsync(r => r.PaymentId == paymentId);
    }

    private async Task<int> CountBookingsTakingDatesAsync(Guid propertyId, DateTime checkIn, DateTime checkOut)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Bookings.CountAsync(b =>
            b.PropertyId == propertyId &&
            b.Status != BookingStatus.Cancelled &&
            b.CheckInDate < checkOut &&
            b.CheckOutDate > checkIn);
    }
}
