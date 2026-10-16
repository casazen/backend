using System.Collections.Concurrent;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;
using Xunit;
using PaymentMethod = Casazen.Core.Entities.PaymentMethod;

namespace Casazen.Tests.Integration;

/// <summary>
/// DB-08h (BK-02, BK-07) on real PostgreSQL, with Stripe simulated by a gateway double: a guest cancels a paid booking with
/// the signed link, the refund is the policy's percent of the base price (the tourist tax stays out), the amounts, the
/// <c>PaymentRefund</c> and the states are what Stripe was told, an error rolls back and leaves the link usable, and two
/// clicks at once refund once (advisory locks).
/// </summary>
public class GuestBookingCancellationPostgresTests : IAsyncLifetime
{
    private const string Account = "acct_guest_cancel_pg";

    private static readonly DateTime CheckIn = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);

    // 50 hours before the check-in day starts: under "Intermedia" the guest gets 50% of the base price.
    private static readonly DateTimeOffset Now = new(RomeCalendar.StartOfDayUtc(CheckIn).AddHours(-50), TimeSpan.Zero);

    private readonly Mock<IStripeService> _stripe = new();
    private readonly RecordingEmailQueue _emails = new();
    private readonly ConcurrentQueue<StripeRefundCreateRequest> _refundRequests = new();
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        await using var db = NewContext();
        await db.Database.MigrateAsync();

        _stripe
            .Setup(s => s.CreateRefundAsync(It.IsAny<StripeRefundCreateRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (StripeRefundCreateRequest r, CancellationToken _) =>
            {
                _refundRequests.Enqueue(r);
                await Task.Delay(50);
                return new Refund { Id = $"re_{Guid.NewGuid():N}", PaymentIntentId = r.PaymentIntentId, Amount = r.AmountCents, Status = "succeeded" };
            });
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task CancelByGuestLinkAsync_PaidBookingWithTouristTax_RefundsHalfOfTheBasePriceThroughStripe()
    {
        var seed = await SeedAsync(PaymentStatus.Completed, BookingStatus.Confirmed);

        GuestCancellationResult result;
        await using (var request = NewContext())
        {
            result = await GuestService(request).CancelByGuestLinkAsync(seed.BookingId, seed.Token);
        }

        // Paid 420 = 400 base + 20 tax: the policy gives back 50% of the 400.
        Assert.Equal(200m, result.RefundAmount);
        Assert.Equal(BookingStatus.Cancelled, result.Booking.Status);
        var sent = Assert.Single(_refundRequests);
        Assert.Equal(Account, sent.ConnectedAccountId);
        Assert.Equal(seed.PaymentIntentId, sent.PaymentIntentId);
        Assert.Equal(20_000, sent.AmountCents);

        await using var db = NewContext();
        var booking = await db.Bookings.SingleAsync(b => b.Id == seed.BookingId);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(GuestCancellationTokens.HashToken(seed.Token), booking.GuestCancelTokenHash);
        Assert.Equal(Now.UtcDateTime, booking.GuestCancelTokenExpiresAt); // spent by the cancellation, in its transaction
        var refund = await db.PaymentRefunds.SingleAsync();
        Assert.Equal(200m, refund.Amount);
        Assert.Equal(PaymentRefundOrigin.BookingCancellation, refund.Origin);
        Assert.Equal(PaymentRefundStatus.Succeeded, refund.Status);
        Assert.NotNull(refund.StripeRefundId);
        Assert.Equal(seed.PaymentId, refund.PaymentId);
        var payment = await db.Payments.SingleAsync(p => p.Id == seed.PaymentId);
        Assert.Equal(PaymentStatus.PartiallyRefunded, payment.Status);
        Assert.Equal(200m, payment.RefundedAmount);
    }

    [PostgresFact]
    public async Task CancelByGuestLinkAsync_ClickedTwiceInParallel_BothAnswerTheSameOutcomeAndRefundsOnce()
    {
        var seed = await SeedAsync(PaymentStatus.Completed, BookingStatus.Confirmed);

        await using var first = NewContext();
        await using var second = NewContext();
        var answers = await Task.WhenAll(
            GuestService(first).CancelByGuestLinkAsync(seed.BookingId, seed.Token),
            GuestService(second).CancelByGuestLinkAsync(seed.BookingId, seed.Token));

        // Whichever gets the lock first cancels; the other waits, finds the booking cancelled and answers with that
        // outcome (or, if it starts later, meets the cancelled booking at once): two 200s, one refund, one email.
        Assert.All(answers, a =>
        {
            Assert.Equal(BookingStatus.Cancelled, a.Booking.Status);
            Assert.Equal(200m, a.RefundAmount);
        });
        Assert.Single(_refundRequests);
        Assert.Single(_emails.Queued);

        await using var db = NewContext();
        Assert.Equal(BookingStatus.Cancelled, (await db.Bookings.SingleAsync(b => b.Id == seed.BookingId)).Status);
        var refund = await db.PaymentRefunds.SingleAsync();
        Assert.Equal(200m, refund.Amount);
        var payment = await db.Payments.SingleAsync(p => p.Id == seed.PaymentId);
        Assert.Equal(200m, payment.RefundedAmount);
    }

    [PostgresFact]
    public async Task CancelByGuestLinkAsync_PaymentBeingConfirmed_RollsBackAndKeepsTheLinkUsable()
    {
        var seed = await SeedAsync(PaymentStatus.Pending, BookingStatus.Pending);
        _stripe
            .Setup(s => s.GetPaymentIntentAsync(seed.PaymentIntentId, Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentIntent { Id = seed.PaymentIntentId, Status = "processing" });

        await using (var request = NewContext())
        {
            var ex = await Assert.ThrowsAsync<DomainConflictException>(() =>
                GuestService(request).CancelByGuestLinkAsync(seed.BookingId, seed.Token));
            Assert.Equal("booking_payment_in_progress", ex.Code);
        }

        await using var db = NewContext();
        var booking = await db.Bookings.SingleAsync(b => b.Id == seed.BookingId);
        Assert.Equal(BookingStatus.Pending, booking.Status);
        Assert.Equal(GuestCancellationTokens.HashToken(seed.Token), booking.GuestCancelTokenHash);
        Assert.NotNull(booking.GuestCancelTokenExpiresAt);
        Assert.Equal(PaymentStatus.Pending, (await db.Payments.SingleAsync(p => p.Id == seed.PaymentId)).Status);
        Assert.False(await db.PaymentRefunds.AnyAsync());
        Assert.Empty(_refundRequests);
    }

    private AppDbContext NewContext() => _database!.CreateContext();

    private GuestBookingCancellationService GuestService(AppDbContext db) =>
        new(
            db,
            CancellationService(db),
            _emails,
            EmailTestHelpers.Links(),
            new ConfigurationBuilder().AddInMemoryCollection().Build(),
            NullLogger<GuestBookingCancellationService>.Instance,
            new FixedTimeProvider(Now));

    private BookingCancellationService CancellationService(AppDbContext db) =>
        new(
            db,
            new PaymentRefundService(
                db, _stripe.Object, Mock.Of<IPaymentRefundRetryScheduler>(), _emails, NullLogger<PaymentRefundService>.Instance, new FixedTimeProvider(Now)),
            _stripe.Object,
            new BookingNotifier(db, _emails, EmailTestHelpers.Links(), Mock.Of<IPushNotificationService>(), NullLogger<BookingNotifier>.Instance),
            NullLogger<BookingCancellationService>.Instance,
            new FixedTimeProvider(Now));

    private sealed record Seed(Guid BookingId, Guid PaymentId, string PaymentIntentId, string Token);

    /// <summary>
    /// A booking of 400 (lodging and cleaning) + 20 of tourist tax, 420 paid through Stripe, under "Intermedia", with the
    /// cancel link the host sent.
    /// </summary>
    private async Task<Seed> SeedAsync(PaymentStatus paymentStatus, BookingStatus bookingStatus)
    {
        var org = new OrgEntity
        {
            Name = "Guest Cancel Org",
            Slug = $"guest-cancel-{Guid.NewGuid():N}",
            DisplayName = "Guest Cancel Org",
            ContactEmail = "host@example.com",
            StripeConnectedAccountId = Account,
            ConnectChargesEnabled = true,
            IsActive = true,
        };
        var property = new Property
        {
            OrgId = org.Id,
            OwnerId = "auth0|host",
            Name = "Villa Annullo",
            Address = $"Via Annullo {Guid.NewGuid():N}",
            City = "Rome",
            PostalCode = "00100",
            MaxGuests = 4,
            NightlyRate = 100m,
            IsActive = true,
            CancellationPolicy = CatalogPolicies.Get(CatalogPolicies.Intermedia),
        };
        var guest = new Guest
        {
            OrgId = org.Id,
            FirstName = "Anna",
            LastName = "Verdi",
            Email = $"guest.{Guid.NewGuid():N}@example.com",
        };
        var token = GuestCancellationTokens.NewToken();
        var booking = new Booking
        {
            OrgId = org.Id,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = CheckIn,
            CheckOutDate = CheckIn.AddDays(3),
            NumberOfGuests = 2,
            Status = bookingStatus,
            Source = BookingSource.Direct,
            BasePrice = 400m,
            TouristTax = 20m,
            TotalPrice = 420m,
            GuestCancelTokenHash = GuestCancellationTokens.HashToken(token),
            GuestCancelTokenExpiresAt = Now.UtcDateTime.AddDays(7),
        };
        var paymentIntentId = $"pi_{Guid.NewGuid():N}";
        var payment = new Payment
        {
            OrgId = org.Id,
            BookingId = booking.Id,
            Amount = 420m,
            Status = paymentStatus,
            Method = PaymentMethod.CreditCard,
            TransactionId = paymentIntentId,
            StripePaymentIntentId = paymentIntentId,
            StripeAccountId = Account,
        };

        await using var db = NewContext();
        db.AddRange(org, property, guest, booking, payment);
        await db.SaveChangesAsync();
        return new Seed(booking.Id, payment.Id, paymentIntentId, token);
    }
}
