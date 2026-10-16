using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
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

    private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"cancellations-{Guid.NewGuid():N}")
        .Options;

    private readonly AppDbContext _db;
    private readonly Mock<IStripeService> _stripe = new();
    private readonly Mock<IEmailQueue> _emails = new();
    private readonly List<StripeRefundCreateRequest> _refunds = [];
    private string _refundStatus = "succeeded";

    public BookingCancellationServiceTests()
    {
        _db = new AppDbContext(_options);
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
    public async Task CancelAsync_AfterDeadlineHostChoosesPartialRefund_ThrowsBelowMinimum()
    {
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 400m, freeRefundUntilDaysFromNow: -1);

        var quote = await Service().GetQuoteAsync(seed.BookingId);
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Service().CancelAsync(new BookingCancellationRequest(seed.BookingId, 150m, "Richiesta ospite", "auth0|host")));

        Assert.Equal(CancellationRefundRule.None, quote.Rule);
        Assert.Equal(400m, quote.MinimumRefundAmount);
        Assert.Equal(400m, quote.RefundableAmount);
        Assert.Equal("booking_cancel_refund_below_minimum", ex.Code);
        Assert.Empty(_refunds);
    }

    [Fact]
    public async Task CancelAsync_AfterDeadlineHostRefundsFullAmount_RefundsOnConnectedAccount()
    {
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 400m, freeRefundUntilDaysFromNow: -1);

        var result = await Service().CancelAsync(new BookingCancellationRequest(seed.BookingId, 400m, "Richiesta ospite", "auth0|host"));

        var request = Assert.Single(_refunds);
        Assert.Equal(Account, request.ConnectedAccountId);
        Assert.Equal(40_000, request.AmountCents);
        Assert.StartsWith("payment-refund:", request.IdempotencyKey);
        var refund = Assert.Single(result.Refunds);
        Assert.Equal(PaymentRefundOrigin.BookingCancellation, refund.Origin);
        Assert.Equal(PaymentRefundStatus.Succeeded, refund.Status);
        var payment = await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == seed.PaymentId);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal(400m, payment.RefundedAmount);
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

        await Service().CancelAsync(new BookingCancellationRequest(seed.BookingId, 400m, "  Caldaia guasta, nota interna  ", "auth0|host"));

        var booking = await _db.Bookings.AsNoTracking().SingleAsync(b => b.Id == seed.BookingId);
        Assert.Equal("Caldaia guasta, nota interna", booking.CancellationNote);
        var email = Assert.Single(sent, e => e.Template == EmailTemplates.Names.GuestBookingCancelled);
        Assert.Equal("guest@example.com", email.To);
        Assert.Contains("Villa Rosa", email.Content.Subject);
        Assert.Contains("400,00 €", email.Content.HtmlBody);
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
    public async Task CancelAsync_RefundSucceededAtOnce_OneGuestEmailWithTheRefundAndNoSeparateRefundEmail()
    {
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 400m, freeRefundUntilDaysFromNow: 10);
        var sent = RecordEmails();

        var result = await Service().CancelAsync(new BookingCancellationRequest(seed.BookingId, 400m, null, "auth0|host"));

        var email = Assert.Single(sent);
        Assert.Equal(EmailTemplates.Names.GuestBookingCancelled, email.Template);
        Assert.Equal("guest@example.com", email.To);
        Assert.Contains("Ti abbiamo rimborsato <strong>400,00 €</strong>.", email.Content.HtmlBody);
        Assert.Contains("L'importo torna sul metodo di pagamento usato per la prenotazione.", email.Content.HtmlBody);
        Assert.DoesNotContain("È stato avviato un rimborso", email.Content.HtmlBody);
        Assert.Contains("Per qualsiasi domanda contatta <strong>Cancel Org</strong> all'indirizzo host@example.com.", email.Content.HtmlBody);
        var refundId = Assert.Single(result.Refunds).Id;
        var refund = await _db.PaymentRefunds.AsNoTracking().SingleAsync(r => r.Id == refundId);
        Assert.NotNull(refund.GuestNotifiedAt);

        // A later webhook for the same refund does not email the guest again.
        await RefundService().NotifyGuestAsync(refund.Id);
        Assert.Single(sent);
    }

    [Fact]
    public async Task CancelAsync_RefundPendingOnStripe_CancellationSaysStartedAndConfirmationFollowsOnce()
    {
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 400m, freeRefundUntilDaysFromNow: 10);
        _refundStatus = "pending";
        var sent = RecordEmails();

        var result = await Service().CancelAsync(new BookingCancellationRequest(seed.BookingId, 400m, null, null));

        var email = Assert.Single(sent);
        Assert.Equal(EmailTemplates.Names.GuestBookingCancelled, email.Template);
        Assert.Contains("È stato avviato un rimborso di <strong>400,00 €</strong>", email.Content.HtmlBody);
        Assert.DoesNotContain("Ti abbiamo rimborsato", email.Content.HtmlBody);
        var refundId = Assert.Single(result.Refunds).Id;
        var refund = await _db.PaymentRefunds.SingleAsync(r => r.Id == refundId);
        Assert.Null(refund.GuestNotifiedAt);

        // Stripe confirms it later (webhook): the "refund confirmed" email, once.
        refund.Status = PaymentRefundStatus.Succeeded;
        await _db.SaveChangesAsync();
        var refundService = RefundService();
        await refundService.NotifyGuestAsync(refund.Id);
        await refundService.NotifyGuestAsync(refund.Id);
        Assert.Equal(
            [EmailTemplates.Names.GuestBookingCancelled, EmailTemplates.Names.GuestRefundConfirmed],
            sent.Select(e => e.Template).ToList());
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
    public async Task CancelAsync_HostChoosesNoRefundAfterDeadline_ThrowsBelowMinimum()
    {
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 400m, freeRefundUntilDaysFromNow: -3);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Service().CancelAsync(new BookingCancellationRequest(seed.BookingId, 0m, null, null)));

        Assert.Equal("booking_cancel_refund_below_minimum", ex.Code);
        Assert.Empty(_refunds);
        Assert.Equal(BookingStatus.Confirmed, (await _db.Bookings.AsNoTracking().SingleAsync(b => b.Id == seed.BookingId)).Status);
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
        Assert.Equal(300m, quote.MinimumRefundAmount);
        Assert.Equal(300m, quote.RefundableAmount);
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

    // ── The guest cancels (DB-08h, BK-02, BK-07): the minimum is the policy's refund, not everything refundable ──
    // Moderata: 50% from 30 days to 48 hours before check-in. The check-in is 470 hours after Now, so 50% applies.
    // Base price 280 + tourist tax 20 = 300 paid.

    [Fact]
    public async Task CancelAsync_GuestInThePartialRefundWindow_RefundsThePolicyShareWhereTheHostWouldBeRefused()
    {
        var guestSeed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 300m, policy: Moderata(), basePrice: 280m);
        var hostSeed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 300m, policy: Moderata(), basePrice: 280m);

        var hostRefused = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Service().CancelAsync(new BookingCancellationRequest(hostSeed.BookingId, 140m, null, "auth0|host")));
        var result = await Service().CancelAsync(
            new BookingCancellationRequest(guestSeed.BookingId, 140m, "Annullamento autonomo", null, BookingCancellationInitiator.Guest));

        Assert.Equal("booking_cancel_refund_below_minimum", hostRefused.Code);
        Assert.Equal(300m, Assert.Single(hostRefused.MessageArgs));
        Assert.Equal(BookingStatus.Cancelled, result.Booking.Status);
        var request = Assert.Single(_refunds);
        Assert.Equal(14_000, request.AmountCents);
        Assert.Equal(PaymentRefundOrigin.BookingCancellation, Assert.Single(result.Refunds).Origin);
        Assert.Equal(
            PaymentStatus.PartiallyRefunded,
            (await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == guestSeed.PaymentId)).Status);
    }

    [Fact]
    public async Task GetQuoteAsync_PartialPolicyWindow_StillAsksTheHostForEverythingRefundable()
    {
        // The quote is the host's: the guest's minimum is never shown in the host's confirmation dialog.
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 300m, policy: Moderata(), basePrice: 280m);

        var quote = await Service().GetQuoteAsync(seed.BookingId);

        Assert.Equal(300m, quote.MinimumRefundAmount);
        Assert.Equal(quote.RefundableAmount, quote.MinimumRefundAmount);
    }

    [Fact]
    public async Task CancelAsync_GuestAmountBelowThePolicyRefund_ThrowsBelowMinimumAndKeepsTheBooking()
    {
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 300m, policy: Moderata(), basePrice: 280m);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Service().CancelAsync(new BookingCancellationRequest(seed.BookingId, 139.99m, null, null, BookingCancellationInitiator.Guest)));

        Assert.Equal("booking_cancel_refund_below_minimum", ex.Code);
        Assert.Equal(140m, Assert.Single(ex.MessageArgs));
        Assert.Equal(BookingStatus.Confirmed, (await _db.Bookings.AsNoTracking().SingleAsync(b => b.Id == seed.BookingId)).Status);
        Assert.Empty(_refunds);
    }

    [Fact]
    public async Task CancelAsync_GuestAmountAboveWhatIsRefundable_StillThrows()
    {
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 300m, policy: Moderata(), basePrice: 280m);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Service().CancelAsync(new BookingCancellationRequest(seed.BookingId, 300.01m, null, null, BookingCancellationInitiator.Guest)));

        Assert.Equal("refund_amount_exceeds_refundable", ex.Code);
        Assert.Empty(_refunds);
    }

    [Fact]
    public async Task CancelAsync_GuestPolicyBasePriceAbovePaid_MinimumIsNeverAboveWhatIsRefundable()
    {
        // 100% policy and a base price of 400, but only 300 was collected: the minimum is capped at the 300 refundable.
        var policy = CatalogPolicies.Get(CatalogPolicies.Ampia);
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 300m, policy: policy, basePrice: 400m);

        var result = await Service().CancelAsync(
            new BookingCancellationRequest(seed.BookingId, 300m, null, null, BookingCancellationInitiator.Guest));

        Assert.Equal(30_000, Assert.Single(_refunds).AmountCents);
        Assert.Equal(PaymentStatus.Refunded, (await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == seed.PaymentId)).Status);
        Assert.Equal(BookingStatus.Cancelled, result.Booking.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task CancelAsync_GuestWhosePolicyRefundsNothing_CancelsWithoutAnAmountOrWithZero(int? amount)
    {
        // 0% from 25 days to 2 days before check-in.
        var policy = new CancellationPolicy { Name = "Rigida", FullRefundHours = 1000, PartialRefundHours = 600, PartialRefundPercent = 50m };
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 300m, policy: policy, basePrice: 280m);

        var result = await Service().CancelAsync(
            new BookingCancellationRequest(seed.BookingId, amount, null, null, BookingCancellationInitiator.Guest));

        Assert.Equal(BookingStatus.Cancelled, result.Booking.Status);
        Assert.Empty(result.Refunds);
        Assert.Empty(_refunds);
        Assert.Equal(PaymentStatus.Completed, (await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == seed.PaymentId)).Status);
    }

    [Fact]
    public async Task CancelAsync_GuestWithoutAnAmountWhenThePolicyRefundsSomething_ThrowsRefundRequired()
    {
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 300m, policy: Moderata(), basePrice: 280m);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Service().CancelAsync(new BookingCancellationRequest(seed.BookingId, null, null, null, BookingCancellationInitiator.Guest)));

        Assert.Equal("booking_cancel_refund_required", ex.Code);
        Assert.Equal(BookingStatus.Confirmed, (await _db.Bookings.AsNoTracking().SingleAsync(b => b.Id == seed.BookingId)).Status);
    }

    [Fact]
    public async Task CancelAsync_HostWithoutAnAmountOnAPartialPolicy_StillThrowsRefundRequired()
    {
        // Same booking as the guest's above: the host owes everything refundable, so a missing amount is still refused.
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 300m, policy: Moderata(), basePrice: 280m);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Service().CancelAsync(new BookingCancellationRequest(seed.BookingId, null, null, "auth0|host")));

        Assert.Equal("booking_cancel_refund_required", ex.Code);
        Assert.Equal(300m, Assert.Single(ex.MessageArgs));
    }

    [Fact]
    public async Task CancelAsync_AnotherClickCancelledAndRefundedAfterTheCallerReadTheBooking_ThrowsConflictInsteadOfRefundingTheRest()
    {
        // The caller (a guest's request) has read the booking and its payment; another click then cancels and refunds 140 of
        // the 300 paid. The 160 left is still refundable: a cancellation that went on from what it read before the lock
        // would refund 140 once more. It reads again under the lock, finds the booking cancelled and stops.
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 300m, policy: Moderata(), basePrice: 280m);
        await using var late = new AppDbContext(_options);
        var staleQuote = await Service(late).GetQuoteAsync(seed.BookingId);
        Assert.Equal(300m, staleQuote.RefundableAmount);

        await Service().CancelAsync(new BookingCancellationRequest(seed.BookingId, 140m, null, null, BookingCancellationInitiator.Guest));
        var ex = await Assert.ThrowsAsync<DomainConflictException>(() =>
            Service(late).CancelAsync(new BookingCancellationRequest(seed.BookingId, 140m, null, null, BookingCancellationInitiator.Guest)));

        Assert.Equal("booking_already_cancelled", ex.Code);
        Assert.Single(_refunds);
        Assert.Single(await _db.PaymentRefunds.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CancelAsync_GuestSpendsTheCancelLinkInTheCancellationWhereTheHostLeavesItAlone()
    {
        var guestSeed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 300m, policy: Moderata(), basePrice: 280m);
        var hostSeed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 300m, policy: Moderata(), basePrice: 280m);
        var expiresAt = Now.UtcDateTime.AddDays(5);
        await GiveCancelLinkAsync(guestSeed.BookingId, "guest-hash", expiresAt);
        await GiveCancelLinkAsync(hostSeed.BookingId, "host-hash", expiresAt);

        await Service().CancelAsync(new BookingCancellationRequest(guestSeed.BookingId, 140m, null, null, BookingCancellationInitiator.Guest));
        await Service().CancelAsync(new BookingCancellationRequest(hostSeed.BookingId, 300m, null, "auth0|host"));

        var spent = await _db.Bookings.AsNoTracking().SingleAsync(b => b.Id == guestSeed.BookingId);
        Assert.Equal("guest-hash", spent.GuestCancelTokenHash);
        Assert.Equal(Now.UtcDateTime, spent.GuestCancelTokenExpiresAt);
        var untouched = await _db.Bookings.AsNoTracking().SingleAsync(b => b.Id == hostSeed.BookingId);
        Assert.Equal("host-hash", untouched.GuestCancelTokenHash);
        Assert.Equal(expiresAt, untouched.GuestCancelTokenExpiresAt);
    }

    [Fact]
    public async Task CancelAsync_GuestWhoseLinkAlreadyExpired_DoesNotMoveTheExpiryForward()
    {
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 300m, policy: Moderata(), basePrice: 280m);
        var expiredAt = Now.UtcDateTime.AddDays(-2);
        await GiveCancelLinkAsync(seed.BookingId, "old-hash", expiredAt);

        await Service().CancelAsync(new BookingCancellationRequest(seed.BookingId, 140m, null, null, BookingCancellationInitiator.Guest));

        Assert.Equal(expiredAt, (await _db.Bookings.AsNoTracking().SingleAsync(b => b.Id == seed.BookingId)).GuestCancelTokenExpiresAt);
    }

    [Fact]
    public async Task CancelAsync_GuestRefusedByARule_LeavesTheCancelLinkUsable()
    {
        var seed = await SeedAsync(BookingStatus.Confirmed, PaymentStatus.Completed, amount: 300m, policy: Moderata(), basePrice: 280m);
        var expiresAt = Now.UtcDateTime.AddDays(5);
        await GiveCancelLinkAsync(seed.BookingId, "guest-hash", expiresAt);

        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Service().CancelAsync(new BookingCancellationRequest(seed.BookingId, 100m, null, null, BookingCancellationInitiator.Guest)));

        var booking = await _db.Bookings.AsNoTracking().SingleAsync(b => b.Id == seed.BookingId);
        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        Assert.Equal(expiresAt, booking.GuestCancelTokenExpiresAt);
    }

    private async Task GiveCancelLinkAsync(Guid bookingId, string hash, DateTime expiresAt)
    {
        var booking = await _db.Bookings.SingleAsync(b => b.Id == bookingId);
        booking.GuestCancelTokenHash = hash;
        booking.GuestCancelTokenExpiresAt = expiresAt;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private static CancellationPolicy Moderata() =>
        new() { Name = "Moderata", Description = "50% fino a 48 ore", FullRefundHours = 30 * 24, PartialRefundHours = 48, PartialRefundPercent = 50m };

    private BookingCancellationService Service(AppDbContext? db = null)
    {
        db ??= _db;
        var notifier = new BookingNotifier(db, _emails.Object, EmailTestHelpers.Links(), Mock.Of<IPushNotificationService>(), NullLogger<BookingNotifier>.Instance);
        return new BookingCancellationService(
            db, RefundService(db), _stripe.Object, notifier, NullLogger<BookingCancellationService>.Instance, new FixedTimeProvider(Now));
    }

    // One queue for the refund emails and the cancellation email: BK-10 checks the guest gets one email, not two.
    private PaymentRefundService RefundService(AppDbContext? db = null) =>
        new(
            db ?? _db,
            _stripe.Object,
            Mock.Of<IPaymentRefundRetryScheduler>(),
            _emails.Object,
            NullLogger<PaymentRefundService>.Instance,
            new FixedTimeProvider(Now));

    private List<(string? To, EmailContent Content, string Template)> RecordEmails()
    {
        var sent = new List<(string? To, EmailContent Content, string Template)>();
        _emails
            .Setup(q => q.Enqueue(It.IsAny<string?>(), It.IsAny<EmailContent>(), It.IsAny<string>()))
            .Callback<string?, EmailContent, string>((to, content, template) => sent.Add((to, content, template)))
            .Returns(true);
        return sent;
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
        CancellationPolicy? policy = null,
        decimal basePrice = 0m)
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
            BasePrice = basePrice,
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
