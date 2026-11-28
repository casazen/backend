using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;
using Xunit;
using PaymentMethod = Casazen.Core.Entities.PaymentMethod;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// DB-08h (BK-02, BK-07, PO 2026-10-08), DB-08a: a guest cancels a paid booking with the signed link, over the real cancellation
/// service and a fake Stripe. The refund is the policy's percent of the base price plus the whole tourist tax (capped at what is
/// refundable) and the answer is 200, whatever the policy refunds; an error leaves the link usable; a second click refunds
/// nothing again. The guest and the host get one email each.
/// </summary>
public class GuestBookingCancellationServiceTests : IDisposable
{
    private const string Account = "acct_guest_cancel";

    private static readonly DateTime CheckIn = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);

    private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"guest-cancellations-{Guid.NewGuid():N}")
        .Options;

    private readonly AppDbContext _db;
    private readonly Mock<IStripeService> _stripe = new();
    private readonly RecordingEmailQueue _emails = new();
    private readonly Mock<IPaymentRefundRetryScheduler> _retries = new();
    private readonly List<StripeRefundCreateRequest> _refunds = [];
    private DateTimeOffset _now = HoursBeforeCheckIn(100);

    public GuestBookingCancellationServiceTests()
    {
        _db = NewContext();
        _stripe
            .Setup(s => s.CreateRefundAsync(It.IsAny<StripeRefundCreateRequest>(), It.IsAny<CancellationToken>()))
            .Callback<StripeRefundCreateRequest, CancellationToken>((r, _) => _refunds.Add(r))
            .ReturnsAsync((StripeRefundCreateRequest r, CancellationToken _) => new Refund
            {
                Id = $"re_{_refunds.Count}",
                PaymentIntentId = r.PaymentIntentId,
                Amount = r.AmountCents,
                Status = "succeeded",
            });
    }

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData(CatalogPolicies.Ampia, 48, 20, 420)]
    [InlineData(CatalogPolicies.Ampia, 48, 0, 400)]
    [InlineData(CatalogPolicies.Ampia, 23, 20, 20)]
    [InlineData(CatalogPolicies.Intermedia, 130, 20, 420)]
    [InlineData(CatalogPolicies.Intermedia, 50, 20, 220)]
    [InlineData(CatalogPolicies.Intermedia, 50, 0, 200)]
    [InlineData(CatalogPolicies.Intermedia, -1, 20, 20)]
    [InlineData(CatalogPolicies.Contenuta, 400, 20, 420)]
    [InlineData(CatalogPolicies.Contenuta, 200, 0, 200)]
    [InlineData(CatalogPolicies.Contenuta, 100, 20, 20)]
    [InlineData(CatalogPolicies.Anticipata, 800, 0, 400)]
    [InlineData(CatalogPolicies.Anticipata, 400, 20, 220)]
    [InlineData(CatalogPolicies.Anticipata, 100, 0, 0)]
    [InlineData(CatalogPolicies.NonRimborsabile, 10, 20, 20)]
    [InlineData(CatalogPolicies.NonRimborsabile, -1, 20, 20)]
    public async Task CancelByGuestLinkAsync_PaidBooking_RefundsPolicyPercentOfBasePriceAndTheTouristTax(
        string policy, double hoursBeforeCheckIn, int touristTax, int expectedRefund)
    {
        _now = HoursBeforeCheckIn(hoursBeforeCheckIn);
        var seed = await SeedAsync(policy: policy, touristTax: touristTax);
        var paid = 400m + touristTax;

        var result = await ClickAsync(seed);

        Assert.Equal(expectedRefund, result.RefundAmount);
        Assert.Equal(BookingStatus.Cancelled, result.Booking.Status);
        var booking = await StoredBookingAsync(seed.BookingId);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        var payment = await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == seed.PaymentId);
        if (expectedRefund == 0)
        {
            Assert.Empty(_refunds);
            Assert.Empty(result.Refunds);
            Assert.False(await _db.PaymentRefunds.AnyAsync());
            Assert.Equal(PaymentStatus.Completed, payment.Status);
        }
        else
        {
            var request = Assert.Single(_refunds);
            Assert.Equal(Account, request.ConnectedAccountId);
            Assert.Equal(seed.PaymentIntentId, request.PaymentIntentId);
            Assert.Equal(expectedRefund * 100, request.AmountCents);
            var refund = Assert.Single(await _db.PaymentRefunds.AsNoTracking().ToListAsync());
            Assert.Equal(expectedRefund, refund.Amount);
            Assert.Equal(PaymentRefundOrigin.BookingCancellation, refund.Origin);
            Assert.Equal(PaymentRefundStatus.Succeeded, refund.Status);
            Assert.Equal(expectedRefund, payment.RefundedAmount);
            Assert.Equal(expectedRefund == paid ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded, payment.Status);
        }

        AssertLinkSpent(seed, booking);
        AssertTheGuestAndTheHostAreEmailedOnce();
        Assert.Equal(BookingCancellationReason.GuestRequested, booking.CancellationReason);
    }

    [Fact]
    public async Task CancelByGuestLinkAsync_PartOfThePaymentAlreadyRefunded_RefundsNoMoreThanWhatIsLeft()
    {
        _now = HoursBeforeCheckIn(500);
        var seed = await SeedAsync(policy: CatalogPolicies.Contenuta);
        _db.PaymentRefunds.Add(new PaymentRefund
        {
            PaymentId = seed.PaymentId,
            OrgId = seed.OrgId,
            Amount = 300m,
            Status = PaymentRefundStatus.Succeeded,
            Origin = PaymentRefundOrigin.Host,
            IdempotencyKey = $"payment-refund:{Guid.NewGuid():N}",
        });
        var stored = await _db.Payments.SingleAsync(p => p.Id == seed.PaymentId);
        stored.Status = PaymentStatus.PartiallyRefunded;
        stored.RefundedAmount = 300m;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await ClickAsync(seed);

        Assert.Equal(120m, result.RefundAmount);
        Assert.Equal(12_000, Assert.Single(_refunds).AmountCents);
        Assert.Equal(PaymentStatus.Refunded, (await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == seed.PaymentId)).Status);
    }

    [Fact]
    public async Task CancelByGuestLinkAsync_PropertyWithoutPolicy_CancelsAndRefundsOnlyTheTouristTax()
    {
        var seed = await SeedAsync(policy: null);

        var result = await ClickAsync(seed);

        Assert.Equal(20m, result.RefundAmount);
        Assert.Equal(BookingStatus.Cancelled, (await StoredBookingAsync(seed.BookingId)).Status);
        Assert.Equal(2_000, Assert.Single(_refunds).AmountCents);
    }

    [Fact]
    public async Task CancelByGuestLinkAsync_FreeCancellationDeadlineStillOpen_StillOnlyThePolicyCounts()
    {
        _now = HoursBeforeCheckIn(100);
        var seed = await SeedAsync(policy: CatalogPolicies.Contenuta);
        var booking = await _db.Bookings.SingleAsync(b => b.Id == seed.BookingId);
        booking.FreeRefundDeadline = CheckIn;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await ClickAsync(seed);

        Assert.Equal(20m, result.RefundAmount);
        Assert.Equal(2_000, Assert.Single(_refunds).AmountCents);
    }

    [Fact]
    public async Task CancelByGuestLinkAsync_PayAtDeadlineNothingCharged_CancelsTheSavedCardAndRefundsNothing()
    {
        _now = HoursBeforeCheckIn(500);
        var seed = await SeedAsync(PaymentKind.DeferredCard, policy: CatalogPolicies.Contenuta);
        _stripe
            .Setup(s => s.GetSetupIntentAsync(seed.SetupIntentId, Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SetupIntent { Id = seed.SetupIntentId, Status = "requires_payment_method" });

        var result = await ClickAsync(seed);

        Assert.Equal(0m, result.RefundAmount);
        Assert.Equal(BookingStatus.Cancelled, (await StoredBookingAsync(seed.BookingId)).Status);
        _stripe.Verify(s => s.CancelSetupIntentAsync(seed.SetupIntentId, Account, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _stripe.Verify(s => s.CreateRefundAsync(It.IsAny<StripeRefundCreateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(PaymentStatus.Canceled, (await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == seed.PaymentId)).Status);
        AssertLinkSpent(seed, await StoredBookingAsync(seed.BookingId));
    }

    [Fact]
    public async Task CancelByGuestLinkAsync_RequestToPayAtTheProperty_CancelsWithoutTouchingStripe()
    {
        _now = HoursBeforeCheckIn(500);
        var seed = await SeedAsync(PaymentKind.None, BookingStatus.Pending, CatalogPolicies.Contenuta);

        var result = await ClickAsync(seed);

        Assert.Equal(0m, result.RefundAmount);
        Assert.Equal(BookingStatus.Cancelled, (await StoredBookingAsync(seed.BookingId)).Status);
        Assert.Empty(_stripe.Invocations);
    }

    [Fact]
    public async Task CancelByGuestLinkAsync_WrongToken_Refuses422AndLeavesTheLinkAndTheBooking()
    {
        var seed = await SeedAsync();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => ClickAsync(seed, token: "not-the-token"));

        Assert.Equal("guest_cancel_token_invalid", ex.Code);
        await AssertUntouchedAsync(seed);
    }

    [Fact]
    public async Task CancelByGuestLinkAsync_ExpiredToken_Refuses422AndLeavesTheBooking()
    {
        var seed = await SeedAsync();
        var booking = await _db.Bookings.SingleAsync(b => b.Id == seed.BookingId);
        booking.GuestCancelTokenExpiresAt = _now.UtcDateTime.AddMinutes(-1);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => ClickAsync(seed));

        Assert.Equal("guest_cancel_token_expired", ex.Code);
        Assert.Equal(BookingStatus.Confirmed, (await StoredBookingAsync(seed.BookingId)).Status);
        Assert.Empty(_stripe.Invocations);
    }

    [Fact]
    public async Task CancelByGuestLinkAsync_StayAlreadyEnded_Refuses422AndKeepsTheLink()
    {
        var seed = await SeedAsync(status: BookingStatus.CheckedOut);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => ClickAsync(seed));

        Assert.Equal("booking_not_cancellable_by_guest", ex.Code);
        await AssertUntouchedAsync(seed, BookingStatus.CheckedOut);
    }

    [Fact]
    public async Task CancelByGuestLinkAsync_PaymentBeingConfirmed_Returns409KeepsTheLinkAndTheNextClickSucceeds()
    {
        _now = HoursBeforeCheckIn(50);
        var seed = await SeedAsync(PaymentKind.UncollectedIntent, BookingStatus.Pending, CatalogPolicies.Intermedia);
        _stripe
            .Setup(s => s.GetPaymentIntentAsync(seed.PaymentIntentId, Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentIntent { Id = seed.PaymentIntentId, Status = "processing" });

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => ClickAsync(seed));

        Assert.Equal("booking_payment_in_progress", ex.Code);
        await AssertUntouchedAsync(seed, BookingStatus.Pending, PaymentStatus.Pending);

        await using (var webhook = NewContext())
        {
            var payment = await webhook.Payments.SingleAsync(p => p.Id == seed.PaymentId);
            payment.Status = PaymentStatus.Completed;
            await webhook.SaveChangesAsync();
        }

        var result = await ClickAsync(seed);

        Assert.Equal(220m, result.RefundAmount);
        Assert.Equal(22_000, Assert.Single(_refunds).AmountCents);
        AssertLinkSpent(seed, await StoredBookingAsync(seed.BookingId));
    }

    [Fact]
    public async Task CancelByGuestLinkAsync_StripeUnreachableBeforeAnythingIsSaved_FailsAndKeepsTheLink()
    {
        _now = HoursBeforeCheckIn(50);
        var seed = await SeedAsync(PaymentKind.UncollectedIntent, BookingStatus.Pending, CatalogPolicies.Intermedia);
        _stripe
            .Setup(s => s.GetPaymentIntentAsync(seed.PaymentIntentId, Account, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network down"));

        await Assert.ThrowsAsync<HttpRequestException>(() => ClickAsync(seed));

        await AssertUntouchedAsync(seed, BookingStatus.Pending, PaymentStatus.Pending);
    }

    [Fact]
    public async Task CancelByGuestLinkAsync_StripeUnreachableWhenSendingTheRefund_CancelsKeepsTheRefundPendingAndSpendsTheLink()
    {
        _now = HoursBeforeCheckIn(50);
        var seed = await SeedAsync(policy: CatalogPolicies.Intermedia);
        _stripe
            .Setup(s => s.CreateRefundAsync(It.IsAny<StripeRefundCreateRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("timeout"));

        var result = await ClickAsync(seed);

        // The booking is cancelled and the refund is reserved: it is sent again with the same idempotency key.
        Assert.Equal(220m, result.RefundAmount);
        Assert.Equal(BookingStatus.Cancelled, (await StoredBookingAsync(seed.BookingId)).Status);
        var refund = Assert.Single(await _db.PaymentRefunds.AsNoTracking().ToListAsync());
        Assert.Equal(PaymentRefundStatus.Pending, refund.Status);
        Assert.Equal(220m, refund.Amount);
        _retries.Verify(r => r.ScheduleSubmit(refund.Id), Times.Once);
        AssertLinkSpent(seed, await StoredBookingAsync(seed.BookingId));
    }

    [Fact]
    public async Task CancelByGuestLinkAsync_ClickedAgainAfterTheCancellation_AnswersWithTheSameOutcomeAndRefundsOnce()
    {
        _now = HoursBeforeCheckIn(50);
        var seed = await SeedAsync(policy: CatalogPolicies.Intermedia);
        var first = await ClickAsync(seed);

        var again = await ClickAsync(seed);
        _now = _now.AddDays(30);
        var muchLater = await ClickAsync(seed);

        foreach (var answer in new[] { again, muchLater })
        {
            Assert.Equal(BookingStatus.Cancelled, answer.Booking.Status);
            Assert.Equal(first.RefundAmount, answer.RefundAmount);
            Assert.Equal(220m, answer.RefundAmount);
        }

        Assert.Single(_refunds);
        Assert.Single(await _db.PaymentRefunds.AsNoTracking().ToListAsync());
        AssertTheGuestAndTheHostAreEmailedOnce();
    }

    [Fact]
    public async Task CancelByGuestLinkAsync_BookingConfirmedAgainAfterTheCancellation_TheSpentLinkIsExpired()
    {
        _now = HoursBeforeCheckIn(50);
        var seed = await SeedAsync(policy: CatalogPolicies.Intermedia);
        await ClickAsync(seed);
        var booking = await _db.Bookings.SingleAsync(b => b.Id == seed.BookingId);
        booking.Status = BookingStatus.Confirmed;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        _now = _now.AddMinutes(1);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => ClickAsync(seed));

        Assert.Equal("guest_cancel_token_expired", ex.Code);
        Assert.Equal(BookingStatus.Confirmed, (await StoredBookingAsync(seed.BookingId)).Status);
        Assert.Single(_refunds);
    }

    [Fact]
    public async Task CancelByGuestLinkAsync_BookingAlreadyCancelledByTheHost_AnswersWithThatOutcomeAndTouchesNothing()
    {
        var seed = await SeedAsync(status: BookingStatus.Cancelled);
        _db.PaymentRefunds.Add(new PaymentRefund
        {
            PaymentId = seed.PaymentId,
            OrgId = seed.OrgId,
            Amount = 420m,
            Status = PaymentRefundStatus.Succeeded,
            Origin = PaymentRefundOrigin.BookingCancellation,
            IdempotencyKey = $"payment-refund:{Guid.NewGuid():N}",
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await ClickAsync(seed);

        Assert.Equal(BookingStatus.Cancelled, result.Booking.Status);
        Assert.Equal(420m, result.RefundAmount);
        Assert.Single(result.Refunds);
        Assert.Empty(_stripe.Invocations);
        Assert.Empty(_emails.Queued);
        Assert.Single(await _db.PaymentRefunds.AsNoTracking().ToListAsync());

        Assert.Equal(seed.TokenExpiresAt, (await StoredBookingAsync(seed.BookingId)).GuestCancelTokenExpiresAt);
    }

    [Fact]
    public async Task CancelByGuestLinkAsync_AnotherClickCancelsFirst_AnswersWithItsOutcomeAndRefundsOnce()
    {
        _now = HoursBeforeCheckIn(50);
        var seed = await SeedAsync(policy: CatalogPolicies.Intermedia);
        await using var winnerDb = NewContext();
        await using var loserDb = NewContext();
        var winner = GuestService(winnerDb);
        var loser = GuestService(
            loserDb,
            new RacingCancellationService(
                CancellationService(loserDb),
                () => winner.CancelByGuestLinkAsync(seed.BookingId, seed.Token)));

        var result = await loser.CancelByGuestLinkAsync(seed.BookingId, seed.Token);

        Assert.Equal(BookingStatus.Cancelled, result.Booking.Status);
        Assert.Equal(220m, result.RefundAmount);
        Assert.Equal(22_000, Assert.Single(_refunds).AmountCents);
        Assert.Single(await _db.PaymentRefunds.AsNoTracking().ToListAsync());
        AssertTheGuestAndTheHostAreEmailedOnce();
    }

    [Fact]
    public async Task PreviewAndCancelByGuestLink_HouseInSwissFrancs_SayFrancsAndRefundTheSameFigure()
    {
        _now = HoursBeforeCheckIn(500);
        var seed = await SeedAsync(policy: CatalogPolicies.Contenuta, currency: StayCurrency.SwissFranc);

        GuestCancellationPreview preview;
        BookingCancellationQuote quote;
        await using (var db = NewContext())
        {
            preview = await GuestService(db).PreviewByGuestLinkAsync(seed.BookingId, seed.Token);
            quote = await CancellationService(db).GetQuoteAsync(seed.BookingId);
        }

        var result = await ClickAsync(seed);

        Assert.Equal("CHF", preview.Currency);
        Assert.Equal("CHF", quote.Currency);
        Assert.Equal(420m, preview.RefundAmount);
        Assert.Equal(preview.RefundAmount, result.RefundAmount);
        Assert.Equal(42_000, Assert.Single(_refunds).AmountCents);
    }

    [Fact]
    public async Task PreviewByGuestLink_HouseInEuros_StaysInEuros()
    {
        _now = HoursBeforeCheckIn(500);
        var seed = await SeedAsync(policy: CatalogPolicies.Contenuta);

        await using var db = NewContext();
        var preview = await GuestService(db).PreviewByGuestLinkAsync(seed.BookingId, seed.Token);

        Assert.Equal("EUR", preview.Currency);
    }

    [Fact]
    public async Task SendCancelLinkAsync_ThenCancelWithTheEmailedToken_CancelsAndRefundsThePolicyShare()
    {
        _now = HoursBeforeCheckIn(50);
        var seed = await SeedAsync(policy: CatalogPolicies.Intermedia);
        await using (var host = NewContext())
        {
            await GuestService(host).SendCancelLinkAsync(seed.BookingId);
        }

        var link = Assert.Single(_emails.Queued, e => e.Template == EmailTemplates.Names.GuestCancelLink);
        var emailed = Regex.Match(link.Content.HtmlBody, @"/cancel\?token=([A-Za-z0-9_\-]+)").Groups[1].Value;
        Assert.NotEmpty(emailed);
        Assert.NotEqual(seed.Token, emailed);

        var result = await ClickAsync(seed, token: emailed);

        Assert.Equal(220m, result.RefundAmount);
        Assert.Equal(BookingStatus.Cancelled, (await StoredBookingAsync(seed.BookingId)).Status);
        Assert.Equal(22_000, Assert.Single(_refunds).AmountCents);
    }

    private async Task<GuestCancellationResult> ClickAsync(Seed seed, string? token = null)
    {
        await using var db = NewContext();
        return await GuestService(db).CancelByGuestLinkAsync(seed.BookingId, token ?? seed.Token);
    }

    private AppDbContext NewContext() => new(_options);

    private GuestBookingCancellationService GuestService(AppDbContext db, IBookingCancellationService? cancellation = null) =>
        new(
            db,
            cancellation ?? CancellationService(db),
            _emails,
            EmailTestHelpers.Links(),
            new ConfigurationBuilder().AddInMemoryCollection().Build(),
            NullLogger<GuestBookingCancellationService>.Instance,
            new FixedTimeProvider(_now));

    private BookingCancellationService CancellationService(AppDbContext db)
    {
        var notifier = new BookingNotifier(db, _emails, EmailTestHelpers.Links(), Mock.Of<IPushNotificationService>(), NullLogger<BookingNotifier>.Instance);
        var refunds = new PaymentRefundService(db, _stripe.Object, _retries.Object, _emails, NullLogger<PaymentRefundService>.Instance, new FixedTimeProvider(_now));
        return new BookingCancellationService(db, refunds, _stripe.Object, notifier, NullLogger<BookingCancellationService>.Instance, new FixedTimeProvider(_now));
    }

    private async Task<Booking> StoredBookingAsync(Guid bookingId) =>
        await _db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);

    private void AssertTheGuestAndTheHostAreEmailedOnce()
    {
        var guest = Assert.Single(_emails.Queued, e => e.Template == EmailTemplates.Names.GuestBookingCancelled);
        Assert.Equal("guest@example.com", guest.To);
        var host = Assert.Single(_emails.Queued, e => e.Template == EmailTemplates.Names.HostBookingCancelledByGuest);
        Assert.Equal("host@example.com", host.To);
        Assert.Equal(2, _emails.Queued.Count);
    }

    private async Task AssertUntouchedAsync(
        Seed seed,
        BookingStatus bookingStatus = BookingStatus.Confirmed,
        PaymentStatus paymentStatus = PaymentStatus.Completed)
    {
        var booking = await StoredBookingAsync(seed.BookingId);
        Assert.Equal(bookingStatus, booking.Status);
        Assert.Equal(GuestCancellationTokens.HashToken(seed.Token), booking.GuestCancelTokenHash);
        Assert.Equal(seed.TokenExpiresAt, booking.GuestCancelTokenExpiresAt);
        Assert.Equal(paymentStatus, (await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == seed.PaymentId)).Status);
        Assert.False(await _db.PaymentRefunds.AnyAsync());
        Assert.Empty(_refunds);
        Assert.Empty(_emails.Queued);
    }

    /// <summary>
    /// The cancellation spent the link in its own transaction: the expiry is the moment of the cancellation, so the link
    /// cannot cancel again, and the hash stays so that a second click is recognised and answered with the outcome.
    /// </summary>
    private void AssertLinkSpent(Seed seed, Booking booking)
    {
        Assert.Equal(GuestCancellationTokens.HashToken(seed.Token), booking.GuestCancelTokenHash);
        Assert.Equal(_now.UtcDateTime, booking.GuestCancelTokenExpiresAt);
    }

    private static DateTimeOffset HoursBeforeCheckIn(double hours) =>
        new(RomeCalendar.StartOfDayUtc(CheckIn).AddHours(-hours), TimeSpan.Zero);

    private enum PaymentKind
    {
        PaidOnline,

        DeferredCard,

        UncollectedIntent,

        None,
    }

    private sealed record Seed(
        Guid OrgId,
        Guid BookingId,
        Guid PaymentId,
        string Token,
        DateTime TokenExpiresAt,
        string PaymentIntentId,
        string SetupIntentId);

    private async Task<Seed> SeedAsync(
        PaymentKind kind = PaymentKind.PaidOnline,
        BookingStatus status = BookingStatus.Confirmed,
        string? policy = CatalogPolicies.Intermedia,
        decimal touristTax = 20m,
        string currency = StayCurrency.Euro)
    {
        const decimal basePrice = 400m;
        var total = basePrice + touristTax;

        var org = new OrgEntity
        {
            Name = "Guest Cancel Org",
            Slug = $"guest-cancel-{Guid.NewGuid():N}",
            DisplayName = "Guest Cancel Org",
            ContactEmail = "host@example.com",
            StripeConnectedAccountId = Account,
            ConnectChargesEnabled = true,
        };
        var property = new Property
        {
            OrgId = org.Id,
            OwnerId = "auth0|host",
            Name = "Villa Rosa",
            Address = "Via Roma 1",
            City = "Roma",
            Currency = currency,
            CancellationPolicy = policy is null ? null : CatalogPolicies.Get(policy),
        };
        var guest = new Guest { OrgId = org.Id, FirstName = "Anna", LastName = "Verdi", Email = "guest@example.com" };
        var token = GuestCancellationTokens.NewToken();
        var tokenExpiresAt = _now.UtcDateTime.AddDays(7);
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = org.Id,
            GuestId = guest.Id,
            CheckInDate = CheckIn,
            CheckOutDate = CheckIn.AddDays(3),
            Status = status,
            BasePrice = basePrice,
            TouristTax = touristTax,
            TotalPrice = total,
            PaymentOption = kind switch
            {
                PaymentKind.DeferredCard => PaymentOption.OnCancellationDeadline,
                PaymentKind.None => PaymentOption.OnSite,
                _ => PaymentOption.Immediate,
            },
            GuestCancelTokenHash = GuestCancellationTokens.HashToken(token),
            GuestCancelTokenExpiresAt = tokenExpiresAt,
        };

        var intentId = $"pi_{Guid.NewGuid():N}";
        var setupIntentId = $"seti_{Guid.NewGuid():N}";
        var payment = new Payment
        {
            BookingId = booking.Id,
            OrgId = org.Id,
            Amount = total,
            Method = PaymentMethod.CreditCard,
            StripeAccountId = Account,
        };
        switch (kind)
        {
            case PaymentKind.PaidOnline or PaymentKind.UncollectedIntent:
                payment.Status = kind == PaymentKind.PaidOnline ? PaymentStatus.Completed : PaymentStatus.Pending;
                payment.TransactionId = intentId;
                payment.StripePaymentIntentId = intentId;
                break;
            case PaymentKind.DeferredCard:
                payment.Status = PaymentStatus.Pending;
                payment.TransactionId = setupIntentId;
                break;
        }

        _db.AddRange(org, property, guest, booking);
        if (kind != PaymentKind.None)
            _db.Add(payment);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return new Seed(org.Id, booking.Id, payment.Id, token, tokenExpiresAt, intentId, setupIntentId);
    }

    private sealed class RacingCancellationService(IBookingCancellationService inner, Func<Task> beforeCancel) : IBookingCancellationService
    {
        public Task<BookingCancellationQuote> GetQuoteAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
            inner.GetQuoteAsync(bookingId, cancellationToken);

        public async Task<BookingCancellationResult> CancelAsync(BookingCancellationRequest request, CancellationToken cancellationToken = default)
        {
            await beforeCancel();
            return await inner.CancelAsync(request, cancellationToken);
        }
    }
}
