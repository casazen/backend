using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;
using Xunit;
using PaymentMethod = Casazen.Core.Entities.PaymentMethod;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// BK-02 (#51, A3-05): the host cancels a booking and its money follows on Stripe. Unpaid intents are canceled on the
/// connected account, what was paid is refunded for the amount chosen within the rules of the model, and nothing is
/// shown as refunded before Stripe confirms.
/// </summary>
public class BookingCancellationServiceTests : IDisposable
{
    private const string Account = "acct_host_1";

    // 2026-09-24 10:00 in Rome.
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 8, 0, 0, TimeSpan.Zero);

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"cancellations-{Guid.NewGuid():N}")
        .Options);

    private readonly Mock<IStripeService> _stripe = new();
    private readonly Mock<IEmailQueue> _emails = new();
    private readonly List<StripeRefundCreateRequest> _refunds = [];
    private string _refundStatus = "succeeded";

    public BookingCancellationServiceTests()
    {
        _stripe
            .Setup(s => s.CreateRefundAsync(It.IsAny<StripeRefundCreateRequest>(), It.IsAny<CancellationToken>()))
            .Callback<StripeRefundCreateRequest, CancellationToken>((r, _) => _refunds.Add(r))
            .ReturnsAsync((StripeRefundCreateRequest r, CancellationToken _) => new Refund
            {
                Id = $"re_{_refunds.Count}",
                PaymentIntentId = r.PaymentIntentId,
                Amount = r.AmountCents,
                Status = _refundStatus,
            });
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CancelAsync_PendingBookingWithUncapturedPaymentIntent_CancelsIntentOnConnectedAccount()
    {
        var seed = await SeedAsync(BookingStatus.Pending, PaymentStatus.Pending);
        _stripe
            .Setup(s => s.GetPaymentIntentAsync(seed.PaymentIntentId, Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentIntent { Id = seed.PaymentIntentId, Status = "requires_payment_method" });

        var result = await Service().CancelAsync(new BookingCancellationRequest(seed.BookingId, null, null, "auth0|host"));

        Assert.Equal(BookingStatus.Cancelled, result.Booking.Status);
        Assert.Equal(1, result.CanceledIntents);
        Assert.Empty(result.Refunds);
        _stripe.Verify(s => s.CancelPaymentIntentAsync(
            seed.PaymentIntentId,
            Account,
            It.Is<string>(key => key.Contains(seed.BookingId.ToString("N"))),
            It.IsAny<CancellationToken>()), Times.Once);
        _stripe.Verify(s => s.CreateRefundAsync(It.IsAny<StripeRefundCreateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        var payment = await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == seed.PaymentId);
        Assert.Equal(PaymentStatus.Canceled, payment.Status);
    }

    [Fact]
    public async Task CancelAsync_PaymentIntentSucceededMeanwhile_ThrowsConflictAndKeepsBooking()
    {
        var seed = await SeedAsync(BookingStatus.Pending, PaymentStatus.Pending);
        _stripe
            .Setup(s => s.GetPaymentIntentAsync(seed.PaymentIntentId, Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentIntent { Id = seed.PaymentIntentId, Status = "succeeded" });

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() =>
            Service().CancelAsync(new BookingCancellationRequest(seed.BookingId, null, null, null)));

        Assert.Equal("booking_payment_in_progress", ex.Code);
        _stripe.Verify(s => s.CancelPaymentIntentAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(BookingStatus.Pending, (await _db.Bookings.AsNoTracking().SingleAsync(b => b.Id == seed.BookingId)).Status);
    }

    [Fact]
    public async Task CancelAsync_DeferredBookingWithUnconfirmedSetupIntent_CancelsSetupIntent()
    {
        var seed = await SeedAsync(BookingStatus.Pending, PaymentStatus.Pending, transactionId: "seti_test_1", paymentIntent: null);
        _stripe
            .Setup(s => s.GetSetupIntentAsync("seti_test_1", Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SetupIntent { Id = "seti_test_1", Status = "requires_payment_method" });

        var result = await Service().CancelAsync(new BookingCancellationRequest(seed.BookingId, null, null, null));

        Assert.Equal(1, result.CanceledIntents);
        _stripe.Verify(s => s.CancelSetupIntentAsync("seti_test_1", Account, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(PaymentStatus.Canceled, (await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == seed.PaymentId)).Status);
    }

    [Fact]
    public async Task CancelAsync_DeferredBookingWithSavedCard_DetachesCardSoItIsNeverCharged()
    {
        var seed = await SeedAsync(
            BookingStatus.Confirmed, PaymentStatus.Pending, transactionId: "seti_test_2", paymentIntent: null, savedCard: "pm_saved");
        _stripe
            .Setup(s => s.GetSetupIntentAsync("seti_test_2", Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SetupIntent { Id = "seti_test_2", Status = "succeeded", PaymentMethodId = "pm_saved" });

        await Service().CancelAsync(new BookingCancellationRequest(seed.BookingId, null, null, null));

        _stripe.Verify(s => s.DetachPaymentMethodAsync("pm_saved", Account, It.IsAny<CancellationToken>()), Times.Once);
        _stripe.Verify(s => s.CancelSetupIntentAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CancelAsync_PaidBookingWithoutRefundDecision_ThrowsAndKeepsBooking()
    {
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 400m);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Service().CancelAsync(new BookingCancellationRequest(seed.BookingId, null, null, null)));

        Assert.Equal("booking_cancel_refund_required", ex.Code);
        Assert.Equal(400m, Assert.Single(ex.MessageArgs));
        Assert.Equal(BookingStatus.Confirmed, (await _db.Bookings.AsNoTracking().SingleAsync(b => b.Id == seed.BookingId)).Status);
        Assert.Empty(_refunds);
    }

    [Fact]
    public async Task CancelAsync_BeforeFreeCancellationDeadline_RefusesPartialRefund()
    {
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 400m, freeRefundUntilDaysFromNow: 5);

        var quote = await Service().GetQuoteAsync(seed.BookingId);
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Service().CancelAsync(new BookingCancellationRequest(seed.BookingId, 100m, null, null)));

        Assert.Equal(CancellationRefundRule.FreeCancellationDeadline, quote.Rule);
        Assert.Equal(400m, quote.MinimumRefundAmount);
        Assert.Equal("booking_cancel_refund_below_minimum", ex.Code);
        Assert.Empty(_refunds);
    }

    [Fact]
    public async Task CancelAsync_AfterDeadlineHostChoosesPartialRefund_RefundsChosenAmountOnConnectedAccount()
    {
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 400m, freeRefundUntilDaysFromNow: -1);

        var quote = await Service().GetQuoteAsync(seed.BookingId);
        var result = await Service().CancelAsync(new BookingCancellationRequest(seed.BookingId, 150m, "Richiesta ospite", "auth0|host"));

        Assert.Equal(CancellationRefundRule.None, quote.Rule);
        Assert.Equal(0m, quote.MinimumRefundAmount);
        Assert.Equal(400m, quote.RefundableAmount);
        var request = Assert.Single(_refunds);
        Assert.Equal(Account, request.ConnectedAccountId);
        Assert.Equal(15_000, request.AmountCents);
        Assert.StartsWith("payment-refund:", request.IdempotencyKey);
        var refund = Assert.Single(result.Refunds);
        Assert.Equal(PaymentRefundOrigin.BookingCancellation, refund.Origin);
        Assert.Equal(PaymentRefundStatus.Succeeded, refund.Status);
        var payment = await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == seed.PaymentId);
        Assert.Equal(PaymentStatus.PartiallyRefunded, payment.Status);
        Assert.Equal(150m, payment.RefundedAmount);
    }

    [Fact]
    public async Task CancelAsync_PaidBookingWithReason_KeepsReasonForHostAndEmailsGuestTheRefundWithoutIt()
    {
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 400m, freeRefundUntilDaysFromNow: -1);
        var sent = new List<(string? To, EmailContent Content, string Template)>();
        _emails
            .Setup(q => q.Enqueue(It.IsAny<string?>(), It.IsAny<EmailContent>(), It.IsAny<string>()))
            .Callback<string?, EmailContent, string>((to, content, template) => sent.Add((to, content, template)))
            .Returns(true);

        await Service().CancelAsync(new BookingCancellationRequest(seed.BookingId, 150m, "  Caldaia guasta, nota interna  ", "auth0|host"));

        var booking = await _db.Bookings.AsNoTracking().SingleAsync(b => b.Id == seed.BookingId);
        Assert.Equal("Caldaia guasta, nota interna", booking.CancellationNote);
        var email = Assert.Single(sent, e => e.Template == EmailTemplates.Names.GuestBookingCancelled);
        Assert.Equal("guest@example.com", email.To);
        Assert.Contains("Villa Rosa", email.Content.Subject);
        Assert.Contains("150,00 €", email.Content.HtmlBody);
        Assert.DoesNotContain("Caldaia", email.Content.HtmlBody);
    }

    [Fact]
    public async Task CancelAsync_NothingPaid_EmailsGuestWithoutRefundLine()
    {
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 250m, transactionId: "", paymentIntent: null);
        var sent = new List<EmailContent>();
        _emails
            .Setup(q => q.Enqueue("guest@example.com", It.IsAny<EmailContent>(), EmailTemplates.Names.GuestBookingCancelled))
            .Callback<string?, EmailContent, string>((_, content, _) => sent.Add(content))
            .Returns(true);

        await Service().CancelAsync(new BookingCancellationRequest(seed.BookingId, null, null, "auth0|host"));

        var email = Assert.Single(sent);
        Assert.DoesNotContain("rimborso", email.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.Null((await _db.Bookings.AsNoTracking().SingleAsync(b => b.Id == seed.BookingId)).CancellationNote);
    }

    [Fact]
    public async Task CancelAsync_RefundPendingOnStripe_BookingCancelledButPaymentNotRefundedYet()
    {
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 400m, freeRefundUntilDaysFromNow: 10);
        _refundStatus = "pending";

        var result = await Service().CancelAsync(new BookingCancellationRequest(seed.BookingId, 400m, null, null));

        Assert.Equal(BookingStatus.Cancelled, result.Booking.Status);
        Assert.Equal(PaymentRefundStatus.Pending, Assert.Single(result.Refunds).Status);
        var payment = await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == seed.PaymentId);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Equal(0m, payment.RefundedAmount);
    }

    [Fact]
    public async Task CancelAsync_HostChoosesNoRefundAfterDeadline_CancelsWithoutStripeRefund()
    {
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 400m, freeRefundUntilDaysFromNow: -3);

        var result = await Service().CancelAsync(new BookingCancellationRequest(seed.BookingId, 0m, null, null));

        Assert.Equal(BookingStatus.Cancelled, result.Booking.Status);
        Assert.Empty(result.Refunds);
        Assert.Empty(_refunds);
        Assert.Equal(PaymentStatus.Completed, (await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == seed.PaymentId)).Status);
    }

    [Fact]
    public async Task CancelAsync_RefundAbovePaid_Throws()
    {
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 400m, freeRefundUntilDaysFromNow: -3);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Service().CancelAsync(new BookingCancellationRequest(seed.BookingId, 400.01m, null, null)));

        Assert.Equal("refund_amount_exceeds_refundable", ex.Code);
        Assert.Empty(_refunds);
    }

    [Fact]
    public async Task GetQuoteAsync_PropertyCancellationPolicy_ComputesMinimumFromIt()
    {
        var policy = new CancellationPolicy { Name = "Moderata", Description = "50% fino a 48 ore", FullRefundHours = 30 * 24, PartialRefundHours = 48, PartialRefundPercent = 50m };
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 300m, freeRefundUntilDaysFromNow: -1, policy: policy);

        var quote = await Service().GetQuoteAsync(seed.BookingId);

        Assert.Equal(CancellationRefundRule.PropertyCancellationPolicy, quote.Rule);
        Assert.Equal("Moderata", quote.CancellationPolicyName);
        Assert.Equal(150m, quote.MinimumRefundAmount);
        Assert.True(quote.RequiresRefundDecision);
    }

    [Fact]
    public async Task CancelAsync_AlreadyCancelled_ThrowsConflict()
    {
        var seed = await SeedAsync(BookingStatus.Cancelled, PaymentStatus.Canceled);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() =>
            Service().CancelAsync(new BookingCancellationRequest(seed.BookingId, null, null, null)));

        Assert.Equal("booking_already_cancelled", ex.Code);
    }

    [Fact]
    public async Task CancelAsync_StayEnded_ThrowsNotCancellable()
    {
        var seed = await SeedAsync(BookingStatus.CheckedOut, PaymentStatus.Completed);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Service().CancelAsync(new BookingCancellationRequest(seed.BookingId, 0m, null, null)));

        Assert.Equal("booking_not_cancellable", ex.Code);
    }

    [Fact]
    public async Task GetQuoteAsync_CashPayment_ReportsOfflineAmountAndNoOnlineRefund()
    {
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 250m, transactionId: "", paymentIntent: null);

        var quote = await Service().GetQuoteAsync(seed.BookingId);

        Assert.Equal(250m, quote.OfflinePaidAmount);
        Assert.Equal(0m, quote.RefundableAmount);
        Assert.False(quote.RequiresRefundDecision);
    }

    private BookingCancellationService Service()
    {
        var refunds = new PaymentRefundService(
            _db,
            _stripe.Object,
            Mock.Of<IPaymentRefundRetryScheduler>(),
            Mock.Of<IEmailQueue>(),
            NullLogger<PaymentRefundService>.Instance,
            new FixedTimeProvider(Now));
        return new BookingCancellationService(
            _db, refunds, _stripe.Object, _emails.Object, NullLogger<BookingCancellationService>.Instance, new FixedTimeProvider(Now));
    }

    private sealed record Seed(Guid BookingId, Guid PaymentId, string PaymentIntentId);

    private async Task<Seed> SeedAsync(
        BookingStatus bookingStatus,
        PaymentStatus paymentStatus,
        decimal amount = 200m,
        string? transactionId = null,
        string? paymentIntent = "generate",
        string? savedCard = null,
        int? freeRefundUntilDaysFromNow = null,
        CancellationPolicy? policy = null)
    {
        var today = new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);
        var org = new OrgEntity
        {
            Name = "Cancel Org",
            Slug = $"cancel-{Guid.NewGuid():N}",
            DisplayName = "Cancel Org",
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
            CancellationPolicy = policy,
        };
        var guest = new Guest { OrgId = org.Id, FirstName = "Anna", LastName = "Verdi", Email = "guest@example.com" };
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = org.Id,
            GuestId = guest.Id,
            CheckInDate = today.AddDays(20),
            CheckOutDate = today.AddDays(23),
            Status = bookingStatus,
            TotalPrice = amount,
            FreeRefundDeadline = freeRefundUntilDaysFromNow is { } days ? today.AddDays(days) : null,
            StripePaymentMethodId = savedCard,
        };
        var intentId = paymentIntent == "generate" ? $"pi_{Guid.NewGuid():N}" : paymentIntent;
        var payment = new Payment
        {
            BookingId = booking.Id,
            OrgId = org.Id,
            Amount = amount,
            Status = paymentStatus,
            Method = intentId is null && transactionId == "" ? PaymentMethod.CashOnArrival : PaymentMethod.CreditCard,
            TransactionId = transactionId ?? intentId ?? string.Empty,
            StripePaymentIntentId = intentId,
            StripeAccountId = Account,
        };

        _db.AddRange(org, property, guest, booking, payment);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return new Seed(booking.Id, payment.Id, intentId ?? string.Empty);
    }
}
