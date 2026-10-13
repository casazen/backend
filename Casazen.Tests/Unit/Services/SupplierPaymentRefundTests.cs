using System.Net;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Tests.Unit.Logging;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Stripe;
using Xunit;
using static Casazen.Tests.Unit.Services.ServicePaymentFlows;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-15b, decisions D2 and D3: the admin refunds a payment that was paid online. The refund is created on the supplier's account
/// with <c>refund_application_fee=true</c> (CasaZen's commission goes back in full for a refund in full, in proportion for a
/// partial one), under the key <c>service-charge-refund:{payment}:{n}</c>; the amounts being refunded are reserved; the payment
/// becomes <c>PartiallyRefunded</c> or <c>Refunded</c> only from refunds Stripe says succeeded, however many times and in whatever
/// order the refund events arrive; the request stays <c>Pagato</c>. The races on PostgreSQL are in <c>ServicePaymentsWebhookPostgresTests</c>.
/// </summary>
public class SupplierPaymentRefundTests
{
    private const string Admin = "auth0|admin-refunds";

    private static async Task<(ServiceRequestScenario S, OpenedPayment Opened)> PaidAsync(
        int amountCents = ServiceRequestScenario.ServicePriceCents,
        ServiceRequestScenario? scenario = null)
    {
        var s = scenario ?? await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync(amountCents);
        await s.PayAsync(opened);
        s.ForgetNotifications();
        return (s, opened);
    }

    private static StripeException Rejection(string code) => new(
        HttpStatusCode.BadRequest,
        new StripeError { Type = "invalid_request_error", Code = code },
        $"Stripe refused the refund: {code}");

    private static StripeException Outage() => new(
        HttpStatusCode.InternalServerError,
        new StripeError { Type = "api_error" },
        "Stripe is down");

    // ─── A refund in full ───

    [Fact]
    public async Task Refund_InFull_ReturnsEverything_AndTheCommissionWithIt_AsAskedToStripe()
    {
        var (s, opened) = await PaidAsync();
        using var _ = s;

        var refund = await s.Payments.RefundAsync(opened.PaymentId, amountCents: null, "Servizio non svolto", Admin);

        Assert.Equal(ServicePaymentRefundStatus.Succeeded, refund.Status);
        Assert.Equal(6_000, refund.AmountCents);
        Assert.Equal(1, refund.Sequence);
        Assert.Equal(ServicePaymentRefundOrigin.Admin, refund.Origin);
        Assert.Equal($"service-charge-refund:{opened.PaymentId:N}:1", refund.IdempotencyKey);
        Assert.Equal("Servizio non svolto", refund.Reason);
        Assert.Equal(Admin, refund.RequestedByUserId);
        Assert.NotNull(refund.StripeRefundId);
        Assert.Equal(ServiceRequestScenario.Instant.UtcDateTime, refund.CompletedAt);
        Assert.Equal(600, refund.ApplicationFeeRefundedCents);

        // What Stripe was asked: a refund of the PaymentIntent on the supplier's account, the commission refunded, the key above.
        var request = Assert.Single(s.Gateway.RefundRequests);
        Assert.Equal(opened.IntentId, request.PaymentIntentId);
        Assert.Equal(ServiceRequestScenario.SupplierAccountId, request.ConnectedAccountId);
        Assert.Equal(6_000, request.AmountCents);
        Assert.True(request.RefundApplicationFee);
        Assert.Equal(refund.IdempotencyKey, request.IdempotencyKey);
        Assert.Equal(refund.Id.ToString(), request.Metadata[ServiceCharges.RefundMetadataKey]);
        Assert.Equal(opened.PaymentId.ToString(), request.Metadata[ServiceCharges.PaymentMetadataKey]);
        Assert.Equal(ServiceCharges.RefundKind, Assert.Single(s.Gateway.Refunds).Metadata!["kind"]);

        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Equal(ServicePaymentStatus.Refunded, payment.Status);
        Assert.Equal(6_000, payment.RefundedCents);
        // The request stays paid: it was paid, and money went back.
        Assert.Equal(ServiceRequestStatus.Pagato, (await s.ReadAsync(opened.Request.Id)).Status);
    }

    [Fact]
    public async Task Refund_TheDefaultAmount_IsEverythingStillRefundable()
    {
        var (s, opened) = await PaidAsync();
        using var _ = s;
        await s.Payments.RefundAsync(opened.PaymentId, 2_000, null, Admin);

        var rest = await s.Payments.RefundAsync(opened.PaymentId, null, null, Admin);

        Assert.Equal(4_000, rest.AmountCents);
        Assert.Equal(ServicePaymentStatus.Refunded, (await s.ReloadAsync(opened.PaymentId)).Status);
    }

    [Fact]
    public async Task Refund_TheKeyAndTheMetadataNeverCarryAnythingPersonal()
    {
        var (s, opened) = await PaidAsync();
        using var _ = s;

        await s.Payments.RefundAsync(opened.PaymentId, null, "Il signor Rossi (rossi@example.com) ha chiamato", Admin);

        var request = Assert.Single(s.Gateway.RefundRequests);
        Assert.All(request.Metadata.Values, value => Assert.DoesNotContain("rossi", value, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("rossi", request.IdempotencyKey, StringComparison.OrdinalIgnoreCase);
    }

    // ─── A partial refund reduces the commission in proportion ───

    [Fact]
    public async Task Refund_Partial_ReturnsTheCommissionInProportion_AndTheOtherPartsAddUpToTheWhole()
    {
        var (s, opened) = await PaidAsync();
        using var _ = s;

        var first = await s.Payments.RefundAsync(opened.PaymentId, 1_500, null, Admin);
        var afterFirst = await s.ReloadAsync(opened.PaymentId);
        s.Clock.Advance(TimeSpan.FromMinutes(1));
        var second = await s.Payments.RefundAsync(opened.PaymentId, 1_234, null, Admin);
        s.Clock.Advance(TimeSpan.FromMinutes(1));
        var third = await s.Payments.RefundAsync(opened.PaymentId, null, null, Admin);

        Assert.Equal(150, first.ApplicationFeeRefundedCents); // 25 % of the price, 25 % of the commission
        Assert.Equal(ServicePaymentStatus.PartiallyRefunded, afterFirst.Status);
        Assert.Equal(1_500, afterFirst.RefundedCents);
        var refunds = await s.RefundsOfAsync(opened.PaymentId);
        Assert.Equal([1, 2, 3], refunds.Select(r => r.Sequence).ToArray());
        Assert.Equal([1_500, 1_234, 3_266], refunds.Select(r => r.AmountCents).ToArray());
        // 273 (cumulative 2 734 of 6 000 → 273.4) and the rest: the parts add up to the whole commission.
        Assert.Equal(150, refunds[0].ApplicationFeeRefundedCents);
        Assert.Equal(123, refunds[1].ApplicationFeeRefundedCents);
        Assert.Equal(327, refunds[2].ApplicationFeeRefundedCents);
        Assert.Equal(600, refunds.Sum(r => r.ApplicationFeeRefundedCents));
        Assert.Equal(ServicePaymentStatus.Refunded, (await s.ReloadAsync(opened.PaymentId)).Status);
        Assert.NotEqual(second.IdempotencyKey, third.IdempotencyKey);
    }

    [Fact]
    public async Task Refund_OfAPaymentWithoutCommission_DoesNotAskStripeToRefundAFee()
    {
        using var scenario = await ServiceRequestScenario.CreateAsync(paymentOptions: new Core.Options.SupplierPaymentsOptions { CommissionPercent = 0m });
        var (s, opened) = await PaidAsync(scenario: scenario);

        var refund = await s.Payments.RefundAsync(opened.PaymentId, 1_000, null, Admin);

        Assert.False(Assert.Single(s.Gateway.RefundRequests).RefundApplicationFee);
        Assert.Equal(0, refund.ApplicationFeeRefundedCents);
        Assert.DoesNotContain(s.EmailsOf(EmailTemplates.Names.ServicePaymentRefundedSupplier), e => e.Content.HtmlBody.Contains("Insieme al rimborso", StringComparison.Ordinal));
    }

    // ─── What can be refunded ───

    [Fact]
    public async Task Refund_MoreThanWhatIsLeft_Is422_WithTheMostThatCanBeRefunded()
    {
        var (s, opened) = await PaidAsync();
        using var _ = s;
        await s.Payments.RefundAsync(opened.PaymentId, 2_000, null, Admin);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Payments.RefundAsync(opened.PaymentId, 4_001, null, Admin));

        Assert.Equal(ServicePaymentErrors.RefundAmountExceeds, ex.Code);
        Assert.Equal(40m, Assert.Single(ex.MessageArgs));
        Assert.Single(await s.RefundsOfAsync(opened.PaymentId));
        Assert.Single(s.Gateway.RefundRequests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Refund_AnAmountThatIsNotPositive_Is422(int amount)
    {
        var (s, opened) = await PaidAsync();
        using var _ = s;

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Payments.RefundAsync(opened.PaymentId, amount, null, Admin));

        Assert.Equal(ServicePaymentErrors.RefundAmountInvalid, ex.Code);
        Assert.Empty(s.Gateway.RefundRequests);
    }

    [Fact]
    public async Task Refund_OfAPaymentRefundedInFull_HasNothingLeft_Is422()
    {
        var (s, opened) = await PaidAsync();
        using var _ = s;
        await s.Payments.RefundAsync(opened.PaymentId, null, null, Admin);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Payments.RefundAsync(opened.PaymentId, null, null, Admin));

        Assert.Equal(ServicePaymentErrors.RefundNothing, ex.Code);
        Assert.Single(s.Gateway.RefundRequests);
    }

    [Theory]
    [InlineData(ServicePaymentStatus.Requested)]
    [InlineData(ServicePaymentStatus.Processing)]
    [InlineData(ServicePaymentStatus.Failed)]
    [InlineData(ServicePaymentStatus.Canceled)]
    [InlineData(ServicePaymentStatus.NeedsReview)]
    public async Task Refund_OfAPaymentThatIsNotPaid_Is422_AndStripeIsNeverCalled(ServicePaymentStatus status)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        await SupplierPaymentRequestTests.ChangePaymentAsync(s, opened.Request.Id, p => p.Status = status);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Payments.RefundAsync(opened.PaymentId, null, null, Admin));

        Assert.Equal(ServicePaymentErrors.RefundNotRefundable, ex.Code);
        Assert.Empty(s.Gateway.RefundRequests);
    }

    [Fact]
    public async Task Refund_OfAPaymentRecordedAsReceivedOutsideCasaZen_Is422_NoMoneyWentThroughCasaZen()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.CompletedOnlineAsync();
        await s.Service.RecordOfflinePaymentAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, "Contanti");
        var offline = (await s.PaymentsOfAsync(request.Id)).Single(p => p.PaidVia == ServicePaymentChannel.Offline);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Payments.RefundAsync(offline.Id, null, null, Admin));

        Assert.Equal(ServicePaymentErrors.RefundOffline, ex.Code);
        Assert.Empty(s.Gateway.RefundRequests);
    }

    [Fact]
    public async Task Refund_OfAPaymentThatDoesNotExist_Is404()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => s.Payments.RefundAsync(Guid.NewGuid(), null, null, Admin));

        Assert.Equal(ServicePaymentErrors.NotFound, ex.Code);
    }

    [Fact]
    public async Task Refund_ANoteLongerThanTheLimit_IsAProgrammingError_TheApiValidatesItFirst()
    {
        var (s, opened) = await PaidAsync();
        using var _ = s;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => s.Payments.RefundAsync(opened.PaymentId, null, new string('x', ServicePaymentLimits.OfflineNoteMaxLength + 1), Admin));
        await Assert.ThrowsAsync<ArgumentException>(() => s.Payments.RefundAsync(opened.PaymentId, null, null, " "));
    }

    // ─── Exactly once ───

    [Fact]
    public async Task Refund_StripeIsPending_TheAmountIsReserved_AndNothingIsRefundedYet()
    {
        var (s, opened) = await PaidAsync();
        using var _ = s;
        s.Gateway.NewRefundStatus = "pending";

        var first = await s.Payments.RefundAsync(opened.PaymentId, 4_000, null, Admin);
        var tooMuch = await Assert.ThrowsAsync<DomainRuleException>(() => s.Payments.RefundAsync(opened.PaymentId, 2_500, null, Admin));
        var rest = await s.Payments.RefundAsync(opened.PaymentId, null, null, Admin);

        Assert.Equal(ServicePaymentRefundStatus.Pending, first.Status);
        Assert.NotNull(first.StripeRefundId);
        Assert.Null(first.CompletedAt);
        Assert.Null(first.ApplicationFeeRefundedCents);
        // 4 000 are being refunded: 2 000 are left, not 6 000.
        Assert.Equal(ServicePaymentErrors.RefundAmountExceeds, tooMuch.Code);
        Assert.Equal(2_000, rest.AmountCents);
        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Equal(ServicePaymentStatus.Paid, payment.Status);
        Assert.Equal(0, payment.RefundedCents);
        Assert.Empty(s.Emails.PaymentEmails()); // nobody is told it was refunded before Stripe says so
    }

    [Fact]
    public async Task Refund_StripeRejectsIt_TheRefundFails_TheAmountIsFreeAgain_AndTheNextOneUsesTheNextKey()
    {
        var (s, opened) = await PaidAsync();
        using var _ = s;
        s.Gateway.FailNextRefund(Rejection("charge_disputed"));

        var failed = await s.Payments.RefundAsync(opened.PaymentId, null, null, Admin);
        s.Gateway.NewRefundStatus = "succeeded";
        var retried = await s.Payments.RefundAsync(opened.PaymentId, null, null, Admin);

        Assert.Equal(ServicePaymentRefundStatus.Failed, failed.Status);
        Assert.Equal("charge_disputed", failed.FailureCode);
        Assert.Null(failed.StripeRefundId);
        Assert.Equal(ServicePaymentRefundStatus.Succeeded, retried.Status);
        Assert.Equal(2, retried.Sequence);
        Assert.Equal(ServicePaymentStatus.Refunded, (await s.ReloadAsync(opened.PaymentId)).Status);
        Assert.Equal(6_000, (await s.ReloadAsync(opened.PaymentId)).RefundedCents);
    }

    [Fact]
    public async Task Refund_StripeDoesNotAnswer_StaysPending_AndTheSyncResendsItWithTheSameKey()
    {
        var (s, opened) = await PaidAsync();
        using var _ = s;
        s.Gateway.FailNextRefund(Outage());

        var pending = await s.Payments.RefundAsync(opened.PaymentId, null, null, Admin);

        Assert.Equal(ServicePaymentRefundStatus.Pending, pending.Status);
        Assert.Null(pending.StripeRefundId);
        Assert.Equal(ServicePaymentStatus.Paid, (await s.ReloadAsync(opened.PaymentId)).Status);

        // Younger than two minutes: the request that wrote it may still be running, so the sync leaves it alone.
        var tooSoon = await s.Payments.SynchronizeAsync();
        Assert.Equal(0, tooSoon.RefundsRead);

        s.Clock.Advance(TimeSpan.FromMinutes(3));
        var run = await s.Payments.SynchronizeAsync();

        Assert.Equal(1, run.RefundsRead);
        Assert.Equal(1, run.RefundsUpdated);
        Assert.Equal(0, run.Errors);
        Assert.Equal(2, s.Gateway.RefundRequests.Count);
        Assert.Equal(s.Gateway.RefundRequests[0].IdempotencyKey, s.Gateway.RefundRequests[1].IdempotencyKey);
        Assert.Single(s.Gateway.Refunds);
        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Equal(ServicePaymentStatus.Refunded, payment.Status);
        var refunds = await s.RefundsOfAsync(opened.PaymentId);
        Assert.Equal(ServicePaymentRefundStatus.Succeeded, Assert.Single(refunds).Status);
        Assert.Equal(2, s.EmailsOf(EmailTemplates.Names.ServicePaymentRefundedPayer).Count + s.EmailsOf(EmailTemplates.Names.ServicePaymentRefundedSupplier).Count);
    }

    [Fact]
    public async Task Refund_StripeCreatedItButTheAnswerWasLost_TheSyncFindsItAndNeverCreatesASecond()
    {
        var (s, opened) = await PaidAsync();
        using var _ = s;
        s.Gateway.FailNextRefund(Outage());
        var pending = await s.Payments.RefundAsync(opened.PaymentId, null, null, Admin);
        // Stripe did create it (the request reached it), but the answer never came back.
        var created = s.Gateway.CreateRefundSilently(s.Gateway.RefundRequests.Single());
        s.Clock.Advance(TimeSpan.FromMinutes(3));

        var run = await s.Payments.SynchronizeAsync();

        Assert.Equal(1, run.RefundsRead);
        Assert.Single(s.Gateway.RefundRequests); // no second creation, not even with the same key
        Assert.Single(s.Gateway.Refunds);
        var row = Assert.Single(await s.RefundsOfAsync(opened.PaymentId));
        Assert.Equal(pending.Id, row.Id);
        Assert.Equal(created.Id, row.StripeRefundId);
        Assert.Equal(ServicePaymentRefundStatus.Succeeded, row.Status);
        Assert.Equal(ServicePaymentStatus.Refunded, (await s.ReloadAsync(opened.PaymentId)).Status);
    }

    [Fact]
    public async Task Sync_ARefundStripeKeepsRefusingToAnswer_IsCountedAsAnError_AndStaysPending()
    {
        var (s, opened) = await PaidAsync();
        using var _ = s;
        s.Gateway.FailNextRefund(Outage());
        await s.Payments.RefundAsync(opened.PaymentId, null, null, Admin);
        s.Clock.Advance(TimeSpan.FromMinutes(3));
        s.Gateway.FailNextList(Outage());

        var run = await s.Payments.SynchronizeAsync();

        Assert.Equal(1, run.Errors);
        Assert.Equal(ServicePaymentRefundStatus.Pending, Assert.Single(await s.RefundsOfAsync(opened.PaymentId)).Status);
        // The next run goes through.
        Assert.Equal(0, (await s.Payments.SynchronizeAsync()).Errors);
        Assert.Equal(ServicePaymentRefundStatus.Succeeded, Assert.Single(await s.RefundsOfAsync(opened.PaymentId)).Status);
    }

    [Fact]
    public async Task Sync_APendingRefundStripeHasNotCompleted_IsReadAgain_AndCompletes()
    {
        var (s, opened) = await PaidAsync();
        using var _ = s;
        s.Gateway.NewRefundStatus = "pending";
        var pending = await s.Payments.RefundAsync(opened.PaymentId, 1_000, null, Admin);
        s.Gateway.SetRefundStatus(pending.StripeRefundId!, "succeeded");
        s.Clock.Advance(TimeSpan.FromMinutes(20));

        var run = await s.Payments.SynchronizeAsync();

        Assert.Equal(1, run.RefundsRead);
        Assert.Equal(1, run.RefundsUpdated);
        var row = Assert.Single(await s.RefundsOfAsync(opened.PaymentId));
        Assert.Equal(ServicePaymentRefundStatus.Succeeded, row.Status);
        Assert.Equal(100, row.ApplicationFeeRefundedCents);
        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Equal(ServicePaymentStatus.PartiallyRefunded, payment.Status);
        Assert.Equal(1_000, payment.RefundedCents);
        // Told once, when Stripe says so: a second run changes nothing and sends nothing.
        s.ForgetNotifications();
        await s.Payments.SynchronizeAsync();
        Assert.Empty(s.Emails.PaymentEmails());
    }

    // ─── Who is told ───

    [Fact]
    public async Task Refund_ThatSucceeded_TellsThePayerAndTheSupplier_OnceEach()
    {
        var (s, opened) = await PaidAsync();
        using var _ = s;

        await s.Payments.RefundAsync(opened.PaymentId, 1_500, null, Admin);

        var payer = Assert.Single(s.EmailsOf(EmailTemplates.Names.ServicePaymentRefundedPayer));
        Assert.Equal("host@test.com", payer.To);
        Assert.Contains("15,00 €", payer.Content.HtmlBody);
        Assert.DoesNotMatch(@"\d\s*%", payer.Content.HtmlBody); // the payer is never told about the commission
        Assert.DoesNotContain("commission", payer.Content.HtmlBody, StringComparison.OrdinalIgnoreCase);
        var supplier = Assert.Single(s.EmailsOf(EmailTemplates.Names.ServicePaymentRefundedSupplier));
        Assert.Equal("supplier@test.com", supplier.To);
        Assert.Contains("15,00 €", supplier.Content.HtmlBody);
        Assert.Contains("1,50 €", supplier.Content.HtmlBody); // the commission that came back with the refund
        Assert.Contains("saldo del tuo account Stripe", supplier.Content.HtmlBody);
    }

    // ─── What Stripe tells CasaZen: charge.refunded and refund.* ───

    [Fact]
    public async Task ChargeRefunded_ARefundMadeOnTheStripeDashboard_IsRecordedAndTheSupplierAndPayerAreTold()
    {
        var (s, opened) = await PaidAsync();
        using var _ = s;
        s.Gateway.AddExternalRefund(opened.IntentId, 2_000);

        var result = await s.Payments.ApplyChargeRefundedAsync(opened.IntentId, ServiceRequestScenario.SupplierAccountId);
        await s.Payments.CompleteAsync(result.Notices);

        Assert.True(result.Handled);
        var row = Assert.Single(await s.RefundsOfAsync(opened.PaymentId));
        Assert.Equal(ServicePaymentRefundOrigin.Stripe, row.Origin);
        Assert.Equal(ServicePaymentRefundStatus.Succeeded, row.Status);
        Assert.Null(row.IdempotencyKey);
        Assert.Null(row.RequestedByUserId);
        Assert.Equal(1, row.Sequence);
        Assert.Equal(200, row.ApplicationFeeRefundedCents);
        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Equal(ServicePaymentStatus.PartiallyRefunded, payment.Status);
        Assert.Equal(2_000, payment.RefundedCents);
        Assert.Single(s.EmailsOf(EmailTemplates.Names.ServicePaymentRefundedPayer));
        Assert.Single(s.EmailsOf(EmailTemplates.Names.ServicePaymentRefundedSupplier));
    }

    [Fact]
    public async Task ChargeRefunded_Twice_AndTheRefundEventsAfterIt_NeverCountTheSameEurosTwice()
    {
        var (s, opened) = await PaidAsync();
        using var _ = s;
        var created = await s.Payments.RefundAsync(opened.PaymentId, 1_500, null, Admin);
        s.ForgetNotifications();
        var snapshot = s.Gateway.Refunds.Single();

        var first = await s.Payments.ApplyChargeRefundedAsync(opened.IntentId, ServiceRequestScenario.SupplierAccountId);
        var second = await s.Payments.ApplyRefundChangedAsync(snapshot, ServiceRequestScenario.SupplierAccountId);
        var third = await s.Payments.ApplyRefundChangedAsync(snapshot, ServiceRequestScenario.SupplierAccountId);
        var fourth = await s.Payments.ApplyChargeRefundedAsync(opened.IntentId, ServiceRequestScenario.SupplierAccountId);

        Assert.All(new[] { first, second, third, fourth }, r => Assert.Empty(r.Notices));
        var row = Assert.Single(await s.RefundsOfAsync(opened.PaymentId));
        Assert.Equal(created.Id, row.Id);
        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Equal(1_500, payment.RefundedCents);
        Assert.Equal(ServicePaymentStatus.PartiallyRefunded, payment.Status);
        Assert.Empty(s.Emails.PaymentEmails());
    }

    [Fact]
    public async Task RefundEvents_InAnyOrder_ASucceededRefundIsNeverTakenBackToPendingByAnOlderEvent()
    {
        var (s, opened) = await PaidAsync();
        using var _ = s;
        await s.Payments.RefundAsync(opened.PaymentId, 1_500, null, Admin);
        var succeeded = s.Gateway.Refunds.Single();
        var olderEvent = succeeded with { Status = "pending" };

        await s.Payments.ApplyRefundChangedAsync(olderEvent, ServiceRequestScenario.SupplierAccountId);

        Assert.Equal(ServicePaymentRefundStatus.Succeeded, Assert.Single(await s.RefundsOfAsync(opened.PaymentId)).Status);
        Assert.Equal(1_500, (await s.ReloadAsync(opened.PaymentId)).RefundedCents);
    }

    [Fact]
    public async Task RefundEvents_ARefundPendingThatSucceedsLater_IsCountedWhenItSucceeds_Once()
    {
        var (s, opened) = await PaidAsync();
        using var _ = s;
        s.Gateway.NewRefundStatus = "pending";
        var created = await s.Payments.RefundAsync(opened.PaymentId, 1_500, null, Admin);
        Assert.Equal(0, (await s.ReloadAsync(opened.PaymentId)).RefundedCents);

        var done = s.Gateway.SetRefundStatus(created.StripeRefundId!, "succeeded");
        var result = await s.Payments.ApplyRefundChangedAsync(done, ServiceRequestScenario.SupplierAccountId);
        var again = await s.Payments.ApplyRefundChangedAsync(done, ServiceRequestScenario.SupplierAccountId);

        Assert.Equal(ServicePaymentNoticeKind.Refunded, Assert.Single(result.Notices).Kind);
        Assert.Empty(again.Notices);
        Assert.Equal(1_500, (await s.ReloadAsync(opened.PaymentId)).RefundedCents);
        Assert.Equal(ServicePaymentRefundStatus.Succeeded, Assert.Single(await s.RefundsOfAsync(opened.PaymentId)).Status);
    }

    [Fact]
    public async Task RefundEvents_ARefundThatFailsAfterItSucceeded_TakesThePaymentBackToPaid()
    {
        var (s, opened) = await PaidAsync();
        using var _ = s;
        await s.Payments.RefundAsync(opened.PaymentId, null, null, Admin);
        Assert.Equal(ServicePaymentStatus.Refunded, (await s.ReloadAsync(opened.PaymentId)).Status);

        var failed = s.Gateway.SetRefundStatus(s.Gateway.Refunds.Single().Id, "failed", "insufficient_funds");
        await s.Payments.ApplyRefundChangedAsync(failed, ServiceRequestScenario.SupplierAccountId);

        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Equal(ServicePaymentStatus.Paid, payment.Status);
        Assert.Equal(0, payment.RefundedCents);
        var row = Assert.Single(await s.RefundsOfAsync(opened.PaymentId));
        Assert.Equal(ServicePaymentRefundStatus.Failed, row.Status);
        Assert.Equal("insufficient_funds", row.FailureCode);
        Assert.Null(row.ApplicationFeeRefundedCents);
        Assert.Null(row.CompletedAt);
    }

    [Fact]
    public async Task RefundEvent_BeforeTheSuccessWasRecorded_RecordsThePaymentAsPaidFirst_ThenTheRefund()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        // The payment succeeded and was refunded on Stripe, but the success event is still waiting in the queue.
        s.Gateway.SetStatus(opened.IntentId, "succeeded");
        s.Gateway.AddExternalRefund(opened.IntentId, 6_000);
        Assert.Equal(ServicePaymentStatus.Requested, (await s.ReloadAsync(opened.PaymentId)).Status);

        var result = await s.Payments.ApplyChargeRefundedAsync(opened.IntentId, ServiceRequestScenario.SupplierAccountId);

        Assert.Equal([ServicePaymentNoticeKind.Received, ServicePaymentNoticeKind.Refunded], result.Notices.Select(n => n.Kind).ToArray());
        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Equal(ServicePaymentStatus.Refunded, payment.Status);
        Assert.Equal(ServicePaymentChannel.Stripe, payment.PaidVia);
        Assert.NotNull(payment.PaidAt);
        Assert.Equal(ServiceRequestStatus.Pagato, (await s.ReadAsync(opened.Request.Id)).Status);
        // When the success event finally arrives it finds it all done.
        var late = await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Succeeded));
        Assert.Empty(late);
        Assert.Equal(ServicePaymentStatus.Refunded, (await s.ReloadAsync(opened.PaymentId)).Status);
    }

    [Fact]
    public async Task RefundEvent_BeforeTheSuccessWasRecorded_ButStripeHasNotSucceeded_RecordsNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        s.Gateway.AddExternalRefund(opened.IntentId, 1_000);

        var result = await s.Payments.ApplyChargeRefundedAsync(opened.IntentId, ServiceRequestScenario.SupplierAccountId);

        Assert.True(result.Handled);
        Assert.Empty(result.Notices);
        Assert.Equal(ServicePaymentStatus.Requested, (await s.ReloadAsync(opened.PaymentId)).Status);
        Assert.Empty(await s.RefundsOfAsync(opened.PaymentId));
    }

    [Fact]
    public async Task RefundEvent_BeforeTheSuccessWasRecorded_ThatDoesNotMatchTheCharge_GoesToReview_NotToPaid()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        s.Gateway.UpdateIntent(opened.IntentId, i => i with { Status = "succeeded", ApplicationFeeCents = 5 });
        s.Gateway.AddExternalRefund(opened.IntentId, 6_000);

        var result = await s.Payments.ApplyChargeRefundedAsync(opened.IntentId, ServiceRequestScenario.SupplierAccountId);

        Assert.Equal(ServicePaymentNoticeKind.NeedsReview, Assert.Single(result.Notices).Kind);
        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Equal(ServicePaymentStatus.NeedsReview, payment.Status);
        Assert.Equal(0, payment.RefundedCents);
        Assert.Empty(await s.RefundsOfAsync(opened.PaymentId));
    }

    [Fact]
    public async Task RefundEvent_FromAnotherAccount_IsIgnored()
    {
        var (s, opened) = await PaidAsync();
        using var _ = s;
        s.Gateway.AddExternalRefund(opened.IntentId, 2_000);

        var charge = await s.Payments.ApplyChargeRefundedAsync(opened.IntentId, "acct_somebody_else");
        var refund = await s.Payments.ApplyRefundChangedAsync(s.Gateway.Refunds.Single(), null);

        Assert.True(charge.Handled);
        Assert.True(refund.Handled);
        Assert.Empty(charge.Notices);
        Assert.Empty(await s.RefundsOfAsync(opened.PaymentId));
        Assert.Equal(0, (await s.ReloadAsync(opened.PaymentId)).RefundedCents);
    }

    [Fact]
    public async Task RefundEvent_OfAPaymentThatNeedsAReview_IsNotRecorded()
    {
        var (s, opened) = await PaidAsync();
        using var _ = s;
        await SupplierPaymentRequestTests.ChangePaymentAsync(s, opened.Request.Id, p =>
        {
            p.Status = ServicePaymentStatus.NeedsReview;
            p.PaidAt = null;
            p.PaidVia = null;
        });
        s.Gateway.AddExternalRefund(opened.IntentId, 2_000);

        var result = await s.Payments.ApplyChargeRefundedAsync(opened.IntentId, ServiceRequestScenario.SupplierAccountId);

        Assert.True(result.Handled);
        Assert.Empty(await s.RefundsOfAsync(opened.PaymentId));
        Assert.Equal(ServicePaymentStatus.NeedsReview, (await s.ReloadAsync(opened.PaymentId)).Status);
    }

    [Fact]
    public async Task RefundEvent_OfAPaymentIntentThatIsNotAServicePaymentsOwn_IsNotOurs()
    {
        var (s, _) = await PaidAsync();
        using var _ = s;

        var charge = await s.Payments.ApplyChargeRefundedAsync("pi_of_a_booking", "acct_host");
        var refund = await s.Payments.ApplyRefundChangedAsync(new ServiceChargeRefund("re_1", "pi_of_a_booking", 100, "succeeded", null, null), "acct_host");
        var noIntent = await s.Payments.ApplyRefundChangedAsync(new ServiceChargeRefund("re_2", null, 100, "succeeded", null, null), "acct_host");

        Assert.False(charge.Handled);
        Assert.False(refund.Handled);
        Assert.False(noIntent.Handled);
    }

    [Fact]
    public async Task ChargeRefunded_MoreThanThePayment_IsRecordedAsRefundedInFull_AndLoggedAsAnError()
    {
        var logger = new CapturingLogger<SupplierPaymentService>();
        using var scenario = await ServiceRequestScenario.CreateAsync(paymentLogger: logger);
        var (s, opened) = await PaidAsync(scenario: scenario);
        s.Gateway.AddExternalRefund(opened.IntentId, 4_000);
        s.Gateway.AddExternalRefund(opened.IntentId, 3_000);

        await s.Payments.ApplyChargeRefundedAsync(opened.IntentId, ServiceRequestScenario.SupplierAccountId);

        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Equal(6_000, payment.RefundedCents);
        Assert.Equal(ServicePaymentStatus.Refunded, payment.Status);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("more than the", StringComparison.Ordinal));
        // The commission is never given back more than once.
        Assert.Equal(600, (await s.RefundsOfAsync(opened.PaymentId)).Sum(r => r.ApplicationFeeRefundedCents));
    }

    [Fact]
    public async Task Refund_AfterARefundMadeOutsideCasaZen_CountsItAndNumbersTheNextOneAfterIt()
    {
        var (s, opened) = await PaidAsync();
        using var _ = s;
        s.Gateway.AddExternalRefund(opened.IntentId, 2_000);
        await s.Payments.ApplyChargeRefundedAsync(opened.IntentId, ServiceRequestScenario.SupplierAccountId);

        var tooMuch = await Assert.ThrowsAsync<DomainRuleException>(() => s.Payments.RefundAsync(opened.PaymentId, 4_001, null, Admin));
        var mine = await s.Payments.RefundAsync(opened.PaymentId, 4_000, null, Admin);

        Assert.Equal(ServicePaymentErrors.RefundAmountExceeds, tooMuch.Code);
        Assert.Equal(2, mine.Sequence);
        Assert.Equal($"service-charge-refund:{opened.PaymentId:N}:2", mine.IdempotencyKey);
        Assert.Equal(ServicePaymentStatus.Refunded, (await s.ReloadAsync(opened.PaymentId)).Status);
    }
}
