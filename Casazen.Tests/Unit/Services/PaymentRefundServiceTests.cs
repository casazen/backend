using System.Net;
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

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// BK-02 (A3-05, A9-15): refunds are created on Stripe with the connected account of the direct charge, an idempotency
/// key and the chosen amount; the payment is Refunded / PartiallyRefunded only once Stripe confirms.
/// </summary>
public class PaymentRefundServiceTests : IDisposable
{
    private const string Account = "acct_host_1";

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"refunds-{Guid.NewGuid():N}")
        .Options);

    private readonly Mock<IStripeService> _stripe = new();
    private readonly Mock<IPaymentRefundRetryScheduler> _retry = new();
    private readonly Mock<IEmailQueue> _emails = new();
    private readonly List<StripeRefundCreateRequest> _refundRequests = [];
    private readonly List<(string? To, EmailContent Content, string Template)> _queued = [];
    private string _stripeStatus = "succeeded";

    public PaymentRefundServiceTests()
    {
        _stripe
            .Setup(s => s.CreateRefundAsync(It.IsAny<StripeRefundCreateRequest>(), It.IsAny<CancellationToken>()))
            .Callback<StripeRefundCreateRequest, CancellationToken>((r, _) => _refundRequests.Add(r))
            .ReturnsAsync((StripeRefundCreateRequest r, CancellationToken _) => new Refund
            {
                Id = $"re_{_refundRequests.Count}",
                PaymentIntentId = r.PaymentIntentId,
                Amount = r.AmountCents,
                Status = _stripeStatus,
                Metadata = new Dictionary<string, string>(r.Metadata),
            });
        _emails
            .Setup(q => q.Enqueue(It.IsAny<string?>(), It.IsAny<EmailContent>(), It.IsAny<string>()))
            .Callback<string?, EmailContent, string>((to, content, template) => _queued.Add((to, content, template)))
            .Returns(true);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task RefundAsync_ConnectPayment_SendsStoredAccountIdempotencyKeyAndAmount()
    {
        var payment = await SeedPaymentAsync(200m, paymentAccount: Account);

        var refund = await Service().RefundAsync(new PaymentRefundRequest(payment.Id, 50m, "Rottura caldaia", "auth0|host"));

        var request = Assert.Single(_refundRequests);
        Assert.Equal(Account, request.ConnectedAccountId);
        Assert.Equal(payment.StripePaymentIntentId, request.PaymentIntentId);
        Assert.Equal(5_000, request.AmountCents);
        Assert.Equal($"payment-refund:{refund.Id:N}", request.IdempotencyKey);
        Assert.Equal(refund.IdempotencyKey, request.IdempotencyKey);
        Assert.Equal(refund.Id.ToString(), request.Metadata["paymentRefundId"]);
        Assert.Equal("Rottura caldaia", refund.Reason);
        Assert.Equal("auth0|host", refund.RequestedByUserId);
    }

    [Fact]
    public async Task RefundAsync_PaymentWithoutStoredAccount_UsesOrgConnectedAccount()
    {
        var payment = await SeedPaymentAsync(100m, paymentAccount: null, orgAccount: "acct_org_current");

        await Service().RefundAsync(new PaymentRefundRequest(payment.Id, null, null, null));

        var request = Assert.Single(_refundRequests);
        Assert.Equal("acct_org_current", request.ConnectedAccountId);
        Assert.Equal(10_000, request.AmountCents);
    }

    [Fact]
    public async Task RefundAsync_StripeSucceededPartialAmount_MarksPartiallyRefundedAndEmailsGuestOnce()
    {
        var payment = await SeedPaymentAsync(200m, paymentAccount: Account);

        var refund = await Service().RefundAsync(new PaymentRefundRequest(payment.Id, 50m, null, null));

        Assert.Equal(PaymentRefundStatus.Succeeded, refund.Status);
        var stored = await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id);
        Assert.Equal(PaymentStatus.PartiallyRefunded, stored.Status);
        Assert.Equal(50m, stored.RefundedAmount);
        var email = Assert.Single(_queued);
        Assert.Equal("guest@example.com", email.To);
        Assert.Equal(EmailTemplates.Names.GuestRefundConfirmed, email.Template);
        Assert.Contains("50,00", email.Content.HtmlBody);

        // A webhook repeating the same refund does not email again.
        await Service().ApplyStripeRefundsAsync([Snapshot(refund.StripeRefundId!, payment, 5_000, "succeeded")], Account);
        foreach (var id in await Service().ApplyStripeRefundsAsync([Snapshot(refund.StripeRefundId!, payment, 5_000, "succeeded")], Account))
            await Service().NotifyGuestAsync(id);
        await Service().NotifyGuestAsync(refund.Id);
        Assert.Single(_queued);
    }

    [Fact]
    public async Task RefundAsync_StripeSucceededFullAmount_MarksRefunded()
    {
        var payment = await SeedPaymentAsync(120m, paymentAccount: Account);

        await Service().RefundAsync(new PaymentRefundRequest(payment.Id, null, null, null));

        var stored = await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id);
        Assert.Equal(PaymentStatus.Refunded, stored.Status);
        Assert.Equal(120m, stored.RefundedAmount);
    }

    [Fact]
    public async Task RefundAsync_StripePending_NoRefundedStatusBeforeConfirmation()
    {
        var payment = await SeedPaymentAsync(200m, paymentAccount: Account);
        _stripeStatus = "pending";

        var refund = await Service().RefundAsync(new PaymentRefundRequest(payment.Id, 200m, null, null));

        Assert.Equal(PaymentRefundStatus.Pending, refund.Status);
        var stored = await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id);
        Assert.Equal(PaymentStatus.Completed, stored.Status);
        Assert.Equal(0m, stored.RefundedAmount);
        Assert.Empty(_queued);

        // The pending refund keeps its amount reserved.
        var summary = await Service().GetSummaryAsync(payment.Id);
        Assert.Equal(200m, summary.PendingRefundAmount);
        Assert.Equal(0m, summary.RefundableAmount);

        // Stripe confirms through the webhook: only now Refunded, and the guest is emailed.
        var succeeded = await Service().ApplyStripeRefundsAsync(
            [Snapshot(refund.StripeRefundId!, payment, 20_000, "succeeded")], Account);
        foreach (var id in succeeded)
            await Service().NotifyGuestAsync(id);

        Assert.Equal([refund.Id], succeeded);
        stored = await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id);
        Assert.Equal(PaymentStatus.Refunded, stored.Status);
        Assert.Single(_queued);
    }

    [Fact]
    public async Task RefundAsync_AmountAboveWhatIsLeftAfterPendingRefund_ThrowsWithoutCallingStripe()
    {
        var payment = await SeedPaymentAsync(100m, paymentAccount: Account);
        _stripeStatus = "pending";
        await Service().RefundAsync(new PaymentRefundRequest(payment.Id, 70m, null, null));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Service().RefundAsync(new PaymentRefundRequest(payment.Id, 40m, null, null)));

        Assert.Equal("refund_amount_exceeds_refundable", ex.Code);
        Assert.Equal(30m, Assert.Single(ex.MessageArgs));
        Assert.Single(_refundRequests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10.005)]
    public async Task RefundAsync_InvalidAmount_ThrowsWithoutCallingStripe(double amount)
    {
        var payment = await SeedPaymentAsync(100m, paymentAccount: Account);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Service().RefundAsync(new PaymentRefundRequest(payment.Id, (decimal)amount, null, null)));

        Assert.Equal("refund_amount_invalid", ex.Code);
        Assert.Empty(_refundRequests);
    }

    [Fact]
    public async Task RefundAsync_StripeRejects_MarksFailedAndLeavesPaymentCompleted()
    {
        var payment = await SeedPaymentAsync(100m, paymentAccount: Account);
        _stripe
            .Setup(s => s.CreateRefundAsync(It.IsAny<StripeRefundCreateRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StripeException(HttpStatusCode.BadRequest, new StripeError { Code = "charge_disputed" }, "disputed"));

        var refund = await Service().RefundAsync(new PaymentRefundRequest(payment.Id, 100m, null, null));

        Assert.Equal(PaymentRefundStatus.Failed, refund.Status);
        Assert.Equal("charge_disputed", refund.FailureReason);
        var stored = await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id);
        Assert.Equal(PaymentStatus.Completed, stored.Status);
        Assert.Equal(0m, stored.RefundedAmount);
        Assert.Equal(100m, (await Service().GetSummaryAsync(payment.Id)).RefundableAmount);
        _retry.Verify(r => r.ScheduleSubmit(It.IsAny<Guid>()), Times.Never);
        Assert.Empty(_queued);
    }

    [Fact]
    public async Task RefundAsync_StripeTimeout_StaysPendingAndRetriesWithSameIdempotencyKey()
    {
        var payment = await SeedPaymentAsync(100m, paymentAccount: Account);
        var keys = new List<string>();
        _stripe
            .Setup(s => s.CreateRefundAsync(It.IsAny<StripeRefundCreateRequest>(), It.IsAny<CancellationToken>()))
            .Returns((StripeRefundCreateRequest r, CancellationToken _) =>
            {
                keys.Add(r.IdempotencyKey);
                return keys.Count == 1
                    ? Task.FromException<Refund>(new HttpRequestException("timeout"))
                    : Task.FromResult(new Refund
                    {
                        Id = "re_retry",
                        PaymentIntentId = r.PaymentIntentId,
                        Amount = r.AmountCents,
                        Status = "succeeded",
                    });
            });

        var refund = await Service().RefundAsync(new PaymentRefundRequest(payment.Id, 100m, null, null));

        Assert.Equal(PaymentRefundStatus.Pending, refund.Status);
        Assert.Null(refund.StripeRefundId);
        _retry.Verify(r => r.ScheduleSubmit(refund.Id), Times.Once);
        Assert.Equal(PaymentStatus.Completed, (await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id)).Status);

        await Service().SubmitPendingAsync(refund.Id);

        Assert.Equal(2, keys.Count);
        Assert.All(keys, key => Assert.Equal(refund.IdempotencyKey, key));
        var stored = await _db.PaymentRefunds.AsNoTracking().SingleAsync(r => r.Id == refund.Id);
        Assert.Equal(PaymentRefundStatus.Succeeded, stored.Status);
        Assert.Equal("re_retry", stored.StripeRefundId);
        Assert.Equal(PaymentStatus.Refunded, (await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id)).Status);
    }

    [Fact]
    public async Task SubmitPendingAsync_TransientFailureInRetryJob_Rethrows()
    {
        var payment = await SeedPaymentAsync(100m, paymentAccount: Account);
        _stripe
            .Setup(s => s.CreateRefundAsync(It.IsAny<StripeRefundCreateRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StripeException(HttpStatusCode.ServiceUnavailable, new StripeError { Type = "api_error" }, "down"));
        var refund = await Service().RefundAsync(new PaymentRefundRequest(payment.Id, 10m, null, null));

        await Assert.ThrowsAsync<StripeException>(() => Service().SubmitPendingAsync(refund.Id));

        Assert.Equal(PaymentRefundStatus.Pending, (await _db.PaymentRefunds.AsNoTracking().SingleAsync(r => r.Id == refund.Id)).Status);
    }

    [Fact]
    public async Task RefundAsync_PaymentNotThroughStripe_ThrowsOffline()
    {
        var payment = await SeedPaymentAsync(100m, paymentAccount: null, paymentIntentId: null);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Service().RefundAsync(new PaymentRefundRequest(payment.Id, 10m, null, null)));

        Assert.Equal("payment_refund_offline", ex.Code);
        Assert.Empty(_refundRequests);
    }

    [Fact]
    public async Task RefundAsync_PaymentNotCollected_ThrowsNotRefundable()
    {
        var payment = await SeedPaymentAsync(100m, paymentAccount: Account, status: PaymentStatus.Pending);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Service().RefundAsync(new PaymentRefundRequest(payment.Id, 10m, null, null)));

        Assert.Equal("payment_not_refundable", ex.Code);
    }

    [Fact]
    public async Task RefundAsync_MissingPayment_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            Service().RefundAsync(new PaymentRefundRequest(Guid.NewGuid(), 10m, null, null)));
    }

    [Fact]
    public async Task ApplyStripeRefundsAsync_DashboardRefund_RecordedAndReflectedOnPayment()
    {
        var payment = await SeedPaymentAsync(300m, paymentAccount: Account);

        var succeeded = await Service().ApplyStripeRefundsAsync([Snapshot("re_dash", payment, 10_000, "succeeded")], Account);

        var refund = await _db.PaymentRefunds.AsNoTracking().SingleAsync(r => r.PaymentId == payment.Id);
        Assert.Equal([refund.Id], succeeded);
        Assert.Equal(PaymentRefundOrigin.Stripe, refund.Origin);
        Assert.Equal("re_dash", refund.StripeRefundId);
        Assert.Equal(100m, refund.Amount);
        var stored = await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id);
        Assert.Equal(PaymentStatus.PartiallyRefunded, stored.Status);
        Assert.Equal(100m, stored.RefundedAmount);
    }

    [Fact]
    public async Task ApplyStripeRefundsAsync_EventFromAnotherConnectedAccount_IsIgnored()
    {
        var payment = await SeedPaymentAsync(300m, paymentAccount: Account);

        var succeeded = await Service().ApplyStripeRefundsAsync([Snapshot("re_other", payment, 30_000, "succeeded")], "acct_someone_else");

        Assert.Empty(succeeded);
        Assert.False(await _db.PaymentRefunds.AnyAsync());
        Assert.Equal(PaymentStatus.Completed, (await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id)).Status);
    }

    [Fact]
    public async Task ApplyStripeRefundsAsync_PlatformEventForConnectPayment_IsIgnored()
    {
        var payment = await SeedPaymentAsync(300m, paymentAccount: Account);

        var succeeded = await Service().ApplyStripeRefundsAsync([Snapshot("re_platform", payment, 30_000, "succeeded")], null);

        Assert.Empty(succeeded);
        Assert.False(await _db.PaymentRefunds.AnyAsync());
    }

    [Fact]
    public async Task ApplyStripeRefundsAsync_SucceededRefundLaterFails_RevertsPaymentStatus()
    {
        var payment = await SeedPaymentAsync(100m, paymentAccount: Account);
        await Service().ApplyStripeRefundsAsync([Snapshot("re_late", payment, 10_000, "succeeded")], Account);

        await Service().ApplyStripeRefundsAsync([Snapshot("re_late", payment, 10_000, "failed", "lost_or_stolen_card")], Account);

        var stored = await _db.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id);
        Assert.Equal(PaymentStatus.Completed, stored.Status);
        Assert.Equal(0m, stored.RefundedAmount);
        var refund = await _db.PaymentRefunds.AsNoTracking().SingleAsync(r => r.PaymentId == payment.Id);
        Assert.Equal(PaymentRefundStatus.Failed, refund.Status);
        Assert.Equal("lost_or_stolen_card", refund.FailureReason);
    }

    private PaymentRefundService Service() =>
        new(_db, _stripe.Object, _retry.Object, _emails.Object, NullLogger<PaymentRefundService>.Instance);

    private static StripeRefundSnapshot Snapshot(
        string refundId,
        Payment payment,
        long amountCents,
        string status,
        string? failureReason = null) =>
        new(refundId, payment.StripePaymentIntentId, amountCents, status, failureReason, null);

    private async Task<Payment> SeedPaymentAsync(
        decimal amount,
        string? paymentAccount,
        string? orgAccount = Account,
        PaymentStatus status = PaymentStatus.Completed,
        string? paymentIntentId = "generate")
    {
        var org = new OrgEntity
        {
            Name = "Refund Org",
            Slug = $"refund-{Guid.NewGuid():N}",
            DisplayName = "Refund Org",
            ContactEmail = "host@example.com",
            StripeConnectedAccountId = orgAccount,
            ConnectChargesEnabled = true,
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
            Status = BookingStatus.Confirmed,
            TotalPrice = amount,
        };
        var intent = paymentIntentId == "generate" ? $"pi_{Guid.NewGuid():N}" : paymentIntentId;
        var payment = new Payment
        {
            BookingId = booking.Id,
            OrgId = org.Id,
            Amount = amount,
            Status = status,
            Method = Casazen.Core.Entities.PaymentMethod.CreditCard,
            TransactionId = intent ?? "manual",
            StripePaymentIntentId = intent,
            StripeAccountId = paymentAccount,
        };

        _db.AddRange(org, property, guest, booking, payment);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return payment;
    }
}
