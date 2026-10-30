using System.Net;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Stripe;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-15c-a (decision VT-D1): the money CasaZen holds, on the real <see cref="SupplierPaymentService"/> over the in-memory database and a
/// <see cref="FakePlatformPaymentGateway"/> (Stripe is never reached). One test per rule where a mistake costs money: the commission is
/// snapshotted; the card is charged <b>once</b> and only while its block is usable; a booking is never accepted with no money behind it; the
/// supplier is paid <b>once</b>, the net of the commission, even when Stripe's answer is lost or the first try is refused; a refund and a release
/// never share the same euros; a request that closes gives the customer's money back in full; what Stripe reports must match the payment.
/// </summary>
public class HeldFundsServiceTests
{
    private const int Amount = 6_000; // 10 %: commission 600, net 5_400
    private const int Net = 5_400;
    private const string IntentId = "pi_held_test";

    private sealed class World(ServiceRequestScenario scenario, Guid requestId, Guid paymentId, string group) : IDisposable
    {
        public ServiceRequestScenario S { get; } = scenario;

        public Guid RequestId { get; } = requestId;

        public Guid PaymentId { get; } = paymentId;

        public string Group { get; } = group;

        public FakePlatformPaymentGateway Stripe => S.Kit.PlatformGateway;

        public SupplierPaymentService Payments => S.Payments;

        public DateTime Now => S.Clock.GetUtcNow().UtcDateTime;

        public async Task<ServiceRequestPayment> PaymentAsync() =>
            await S.Db.ServiceRequestPayments.AsNoTracking().SingleAsync(p => p.Id == PaymentId);

        public async Task<ServiceRequest> RequestAsync() => await S.ReadAsync(RequestId);

        public void Dispose() => S.Dispose();
    }

    /// <summary>A request of the supplier with a payment <c>Authorized</c> by a block of <paramref name="blockedCents"/> on the fake Stripe.</summary>
    private static async Task<World> StartAsync(
        ServiceRequestStatus status = ServiceRequestStatus.PresoInCarico,
        bool supplierReady = true,
        long blockedCents = Amount,
        bool showcase = false)
    {
        var s = await ServiceRequestScenario.CreateAsync();
        await s.EnablePaymentsAsync(supplierReady);
        var now = s.Clock.GetUtcNow().UtcDateTime;
        var request = await s.SeedAsync(status, r =>
        {
            r.PaymentMode = ServiceRequestPaymentMode.Online;
            r.FinalAmountCents = Amount;
            r.TakenAt = now;
            if (status is ServiceRequestStatus.Completato or ServiceRequestStatus.Pagato)
                r.CompletedAt = now.AddMinutes(-90);

            if (showcase)
            {
                r.RentalContext = ServiceRequestRentalContext.Showcase;
                r.Source = ServiceRequestSource.Showcase;
                r.PropertyId = null;
                r.BookingId = null;
                r.OrgId = s.SupplierOrgId;
            }
        });

        var group = ServiceCharges.HoldTransferGroup(Guid.NewGuid());
        s.Kit.PlatformGateway.AddBlock(IntentId, blockedCents, group);
        var payment = await s.Payments.AddAuthorizedPaymentAsync(
            request,
            new AuthorizedPaymentInput(
                IntentId, Amount, now, group, [new ServiceRequestPriceLine(ServiceRequestPriceLineKinds.Base, "Pulizia", Amount)]));
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        return new World(s, request.Id, payment.Id, group);
    }

    /// <summary>The card was charged, the work is done and the money is due: the next run of the job releases it.</summary>
    private static async Task<World> DueAsync(bool supplierReady = true)
    {
        var w = await StartAsync(ServiceRequestStatus.Completato, supplierReady);
        await w.Payments.CaptureForTakeAsync(await w.RequestAsync());

        var payment = await w.S.Db.ServiceRequestPayments.SingleAsync(p => p.Id == w.PaymentId);
        payment.ReleaseAt = w.Now.AddMinutes(-30);
        await w.S.Db.SaveChangesAsync();
        w.S.Db.ChangeTracker.Clear();
        return w;
    }

    private static StripeException Refusal(string code = "account_invalid") =>
        new(HttpStatusCode.BadRequest, new StripeError { Type = "invalid_request_error", Code = code }, "refused");

    // ─── The payment born with the request ───

    [Fact]
    public async Task AddAuthorizedPayment_SnapshotsTheCommission_TheBlockExpiry_AndTheHeldModel()
    {
        using var w = await StartAsync();

        var payment = await w.PaymentAsync();
        Assert.Equal(ServicePaymentStatus.Authorized, payment.Status);
        Assert.Equal(ServiceFundsModel.PlatformHeld, payment.FundsModel);
        Assert.Equal(Amount, payment.AmountCents);
        Assert.Equal(600, payment.ApplicationFeeCents); // the commission CasaZen keeps: nothing is sent to Stripe as a fee
        Assert.Equal(Net, payment.NetCents);
        Assert.Equal(10m, payment.CommissionPercent);
        Assert.Null(payment.ConnectedAccountId);
        Assert.Equal(IntentId, payment.StripePaymentIntentId);
        Assert.Equal(w.Group, payment.TransferGroup);
        Assert.Equal(w.Now.AddHours(48), payment.AuthorizationExpiresAt);
        Assert.Equal(ServicePayerKind.Host, payment.PayerKind); // a host request in this scenario; a showcase customer is Private
        Assert.Null(payment.CapturedAt);
        Assert.Null(payment.ReleaseAt);
    }

    [Fact]
    public async Task AddAuthorizedPayment_ASuppliersOwnFreePeriod_IsTheSnapshot()
    {
        var s = await ServiceRequestScenario.CreateAsync();
        using var _ = s;
        var profile = await s.Db.SupplierProfiles.SingleAsync(p => p.OrgId == s.SupplierOrgId);
        profile.CommissionPercentOverride = 0m;
        await s.Db.SaveChangesAsync();
        var request = await s.SeedAsync(ServiceRequestStatus.PresoInCarico);

        var payment = await s.Payments.AddAuthorizedPaymentAsync(
            request, new AuthorizedPaymentInput(IntentId, Amount, s.Clock.GetUtcNow().UtcDateTime, "g", []));

        Assert.Equal(0, payment.ApplicationFeeCents);
        Assert.Equal(Amount, payment.NetCents);
        Assert.Equal(0m, payment.CommissionPercent);
    }

    // ─── The card is charged once, and only while its block is usable ───

    [Fact]
    public async Task Capture_ChargesTheCardOnce_AndTheMoneyIsHeld()
    {
        using var w = await StartAsync();
        var request = await w.RequestAsync();

        await w.Payments.CaptureForTakeAsync(request);
        await w.Payments.CaptureForTakeAsync(request); // a retry of the take, a double click: nothing is charged twice

        var payment = await w.PaymentAsync();
        Assert.Equal(ServicePaymentStatus.Held, payment.Status);
        Assert.Equal(w.Now, payment.CapturedAt);
        Assert.Equal(w.Now, payment.PaidAt);
        Assert.Equal(ServicePaymentChannel.Stripe, payment.PaidVia);
        Assert.Equal($"ch_{IntentId}", payment.StripeChargeId);
        var capture = Assert.Single(w.Stripe.Captures);
        Assert.Equal(ServiceCharges.CaptureIdempotencyKey(w.PaymentId), capture.Key);
    }

    [Fact]
    public async Task Capture_ARequestWithNoHeldPayment_DoesNothing_AndCallsNoStripe()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedAsync(ServiceRequestStatus.Richiesto);

        await s.Payments.CaptureForTakeAsync(request);

        Assert.Empty(s.Kit.PlatformGateway.Captures);
    }

    [Fact]
    public async Task Capture_ABlockThatLapsed_IsReleased_AndTheTakeIsRefused()
    {
        using var w = await StartAsync();
        w.S.Clock.Advance(TimeSpan.FromHours(49)); // AuthorizationValidityHours = 48

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(
            async () => await w.Payments.CaptureForTakeAsync(await w.RequestAsync()));

        Assert.Equal("service_payment_authorization_expired", refusal.Code);
        Assert.Empty(w.Stripe.Captures); // the card is never charged for a block that lapsed
        Assert.Equal("canceled", w.Stripe.Intent(IntentId).Status);
        Assert.Equal(ServicePaymentStatus.Canceled, (await w.PaymentAsync()).Status);
    }

    [Fact]
    public async Task Capture_ABlockThatWasDropped_LeavesTheBookingUnacceptable_NotAManualOne()
    {
        using var w = await StartAsync();
        var payment = await w.S.Db.ServiceRequestPayments.SingleAsync(p => p.Id == w.PaymentId);
        payment.Status = ServicePaymentStatus.Canceled;
        await w.S.Db.SaveChangesAsync();
        w.S.Db.ChangeTracker.Clear();

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(
            async () => await w.Payments.CaptureForTakeAsync(await w.RequestAsync()));

        Assert.Equal("service_payment_authorization_expired", refusal.Code);
        Assert.True(await w.Payments.HasHeldPaymentAsync(w.RequestId));
    }

    [Fact]
    public async Task Capture_WhenTheBankRefuses_TheTakeIsRefused_AndTheBlockIsReleased()
    {
        using var w = await StartAsync();
        w.Stripe.FailNextCapture(new StripeException(HttpStatusCode.PaymentRequired, new StripeError { Type = "card_error", Code = "card_declined" }, "declined"));

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(
            async () => await w.Payments.CaptureForTakeAsync(await w.RequestAsync()));

        Assert.Equal("service_payment_authorization_expired", refusal.Code);
        Assert.Equal("canceled", w.Stripe.Intent(IntentId).Status);
        Assert.Equal(ServicePaymentStatus.Canceled, (await w.PaymentAsync()).Status);
    }

    [Fact]
    public async Task Capture_WhenStripeIsDown_TheTakeIsRefused_AndItCanBeTriedAgainWithTheSameKey()
    {
        using var w = await StartAsync();
        w.Stripe.FailNextCapture(new StripeException(HttpStatusCode.ServiceUnavailable, new StripeError { Type = "api_error" }, "down"));

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(
            async () => await w.Payments.CaptureForTakeAsync(await w.RequestAsync()));
        Assert.Equal("service_payment_capture_failed", refusal.Code);
        Assert.Equal(ServicePaymentStatus.Authorized, (await w.PaymentAsync()).Status);

        await w.Payments.CaptureForTakeAsync(await w.RequestAsync());

        Assert.Equal(ServicePaymentStatus.Held, (await w.PaymentAsync()).Status);
        Assert.Equal(2, w.Stripe.Captures.Count);
        Assert.Single(w.Stripe.Captures.Select(c => c.Key).Distinct()); // the same key: Stripe charges once
    }

    [Fact]
    public async Task Capture_ForAnotherAmountThanThePrice_GoesToReview_AndTheTakeIsRefused()
    {
        using var w = await StartAsync(blockedCents: 5_000); // the block on Stripe is not the price of the payment

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(
            async () => await w.Payments.CaptureForTakeAsync(await w.RequestAsync()));

        Assert.Equal("service_payment_capture_failed", refusal.Code);
        var payment = await w.PaymentAsync();
        Assert.Equal(ServicePaymentStatus.NeedsReview, payment.Status);
        Assert.StartsWith("review:", payment.FailureCode, StringComparison.Ordinal);
    }

    // ─── The supplier is paid once, the net of the commission ───

    [Fact]
    public async Task Release_PaysTheSupplierOnceTheNet_AndTheRequestBecomesPaid()
    {
        using var w = await DueAsync();

        var first = await w.Payments.RunFundsAsync();
        var second = await w.Payments.RunFundsAsync();

        Assert.Equal(1, first.Released);
        Assert.Equal(0, second.Released);
        var transfer = Assert.Single(w.Stripe.TransferRequests);
        Assert.Equal(Net, transfer.AmountCents); // the commission stays with CasaZen
        Assert.Equal(ServiceRequestScenario.SupplierAccountId, transfer.DestinationAccountId);
        Assert.Equal($"ch_{IntentId}", transfer.SourceChargeId);
        Assert.Equal(w.Group, transfer.TransferGroup);
        Assert.Equal(ServiceCharges.TransferIdempotencyKey(w.PaymentId, 0), transfer.IdempotencyKey);

        var payment = await w.PaymentAsync();
        Assert.Equal(ServicePaymentStatus.Released, payment.Status);
        Assert.Equal(w.Stripe.Transfers[0].Id, payment.StripeTransferId);
        Assert.Equal(w.Now, payment.ReleasedAt);
        var request = await w.RequestAsync();
        Assert.Equal(ServiceRequestStatus.Pagato, request.Status);
        Assert.Equal(ServiceRequestActorParty.System, request.PaidBy);
        Assert.Single(w.S.EmailsOf("service-payment-received", "supplier@test.com"));
    }

    [Fact]
    public async Task Release_NothingMovesBeforeTheMoneyIsDue()
    {
        using var w = await DueAsync();
        var payment = await w.S.Db.ServiceRequestPayments.SingleAsync(p => p.Id == w.PaymentId);
        payment.ReleaseAt = w.Now.AddMinutes(30);
        await w.S.Db.SaveChangesAsync();
        w.S.Db.ChangeTracker.Clear();

        var run = await w.Payments.RunFundsAsync();

        Assert.Equal(0, run.Released);
        Assert.Empty(w.Stripe.TransferRequests);
        Assert.Equal(ServicePaymentStatus.Held, (await w.PaymentAsync()).Status);
    }

    [Fact]
    public async Task Release_WhenTheAnswerOfStripeIsLost_TheTransferIsFoundNotRepeated()
    {
        using var w = await DueAsync();
        w.Stripe.LoseNextTransferAnswer(); // the transfer exists on Stripe, the answer never came

        var first = await w.Payments.RunFundsAsync();
        Assert.Equal(0, first.Released);
        Assert.Equal(ServicePaymentStatus.Held, (await w.PaymentAsync()).Status);
        Assert.Single(w.Stripe.Transfers);

        w.S.Clock.Advance(TimeSpan.FromMinutes(16)); // past the first delay
        var second = await w.Payments.RunFundsAsync();

        Assert.Equal(1, second.Released);
        Assert.Single(w.Stripe.TransferRequests); // no second transfer was asked for
        Assert.Single(w.Stripe.Transfers);
        var payment = await w.PaymentAsync();
        Assert.Equal(ServicePaymentStatus.Released, payment.Status);
        Assert.Equal(w.Stripe.Transfers[0].Id, payment.StripeTransferId);
    }

    [Fact]
    public async Task Release_WhenStripeRefuses_ItIsTriedAgainLaterWithAnotherKey_AndStillOneTransfer()
    {
        using var w = await DueAsync();
        w.Stripe.FailNextTransfer(Refusal());

        var first = await w.Payments.RunFundsAsync();

        Assert.Equal(1, first.ReleaseFailed);
        var failed = await w.PaymentAsync();
        Assert.Equal(ServicePaymentStatus.TransferFailed, failed.Status);
        Assert.Equal(1, failed.TransferAttempts);
        Assert.Equal("account_invalid", failed.TransferFailureCode);
        Assert.Equal(w.Now.AddMinutes(15), failed.TransferNextAttemptAt);

        // Before the delay nothing is asked of Stripe again.
        await w.Payments.RunFundsAsync();
        Assert.Single(w.Stripe.TransferRequests);

        w.S.Clock.Advance(TimeSpan.FromMinutes(16));
        var third = await w.Payments.RunFundsAsync();

        Assert.Equal(1, third.Released);
        Assert.Equal(2, w.Stripe.TransferRequests.Count);
        Assert.Equal(ServiceCharges.TransferIdempotencyKey(w.PaymentId, 0), w.Stripe.TransferRequests[0].IdempotencyKey);
        Assert.Equal(ServiceCharges.TransferIdempotencyKey(w.PaymentId, 1), w.Stripe.TransferRequests[1].IdempotencyKey);
        Assert.Single(w.Stripe.Transfers); // the refused one never existed
        Assert.Equal(ServicePaymentStatus.Released, (await w.PaymentAsync()).Status);
    }

    [Fact]
    public async Task Release_AfterManyRefusals_TheAdminsAreToldOnce()
    {
        using var w = await DueAsync();

        for (var attempt = 1; attempt <= 6; attempt++)
        {
            w.Stripe.FailNextTransfer(Refusal());
            await w.Payments.RunFundsAsync();
            w.S.Clock.Advance(TimeSpan.FromHours(13)); // past the longest delay
        }

        Assert.Equal(ServicePaymentStatus.TransferFailed, (await w.PaymentAsync()).Status);
        Assert.Equal(6, (await w.PaymentAsync()).TransferAttempts);
        Assert.NotNull((await w.PaymentAsync()).TransferAlertedAt);
    }

    [Fact]
    public async Task Release_WhenTheSuppliersAccountIsNotReady_Waits_AndAsksOnce()
    {
        using var w = await DueAsync();
        await w.S.ConnectSupplierAsync(ready: false);

        await w.Payments.RunFundsAsync();
        w.S.Clock.Advance(TimeSpan.FromMinutes(16));
        await w.Payments.RunFundsAsync();

        Assert.Empty(w.Stripe.TransferRequests); // nothing is sent to Stripe for an account that cannot receive it
        var payment = await w.PaymentAsync();
        Assert.Equal(ServicePaymentStatus.Held, payment.Status);
        Assert.Equal("account_not_ready", payment.TransferFailureCode);
        Assert.Equal(0, payment.TransferAttempts); // no refusal: the key of the first real try is still :0
        Assert.Single(w.S.EmailsOf("service-funds-account-not-ready", "supplier@test.com"));
    }

    [Fact]
    public async Task Release_NeverForARequestThatIsNotCompleted()
    {
        using var w = await DueAsync();
        await w.S.ChangeRequestAsync(w.RequestId, r => r.Status = ServiceRequestStatus.InCorso);

        var run = await w.Payments.RunFundsAsync();

        Assert.Equal(0, run.Released);
        Assert.Empty(w.Stripe.TransferRequests);
    }

    // ─── A refund and a release never share the same euros ───

    [Fact]
    public async Task APartialRefund_BeforeTheRelease_ReducesWhatTheSupplierGets()
    {
        using var w = await DueAsync();
        await w.Payments.RefundAsync(w.PaymentId, 1_000, "goodwill", "admin-1");
        var held = await w.PaymentAsync();
        Assert.Equal(ServicePaymentStatus.Held, held.Status); // the supplier is still owed the rest
        Assert.Equal(1_000, held.RefundedCents);

        await w.Payments.RunFundsAsync();

        Assert.Equal(Net - 1_000, Assert.Single(w.Stripe.TransferRequests).AmountCents);
        Assert.Equal(ServicePaymentStatus.Released, (await w.PaymentAsync()).Status);
    }

    [Fact]
    public async Task ARefundInProgress_StopsTheRelease()
    {
        using var w = await DueAsync();
        w.S.Db.ServiceRequestPaymentRefunds.Add(new ServiceRequestPaymentRefund
        {
            ServiceRequestPaymentId = w.PaymentId,
            Sequence = 1,
            AmountCents = 1_000,
            Status = ServicePaymentRefundStatus.Pending,
            Origin = ServicePaymentRefundOrigin.Admin,
            IdempotencyKey = "service-charge-refund:test:1",
        });
        await w.S.Db.SaveChangesAsync();
        w.S.Db.ChangeTracker.Clear();

        var run = await w.Payments.RunFundsAsync();

        Assert.Equal(0, run.Released);
        Assert.Empty(w.Stripe.TransferRequests);
    }

    [Fact]
    public async Task AfterTheRelease_NothingCanBeRefundedFromHere()
    {
        using var w = await DueAsync();
        await w.Payments.RunFundsAsync();

        var refusal = await Assert.ThrowsAsync<DomainRuleException>(
            async () => await w.Payments.RefundAsync(w.PaymentId, 1_000, "too late", "admin-1"));

        Assert.Equal("service_payment_not_refundable", refusal.Code);
        Assert.Empty(w.Stripe.RefundRequests);
    }

    [Fact]
    public async Task TheWholePriceCanBeRefundedOnlyOnce_BeforeTheRelease()
    {
        using var w = await DueAsync();
        await w.Payments.RefundAsync(w.PaymentId, null, "everything", "admin-1");
        Assert.Equal(Amount, Assert.Single(w.Stripe.RefundRequests).AmountCents);
        Assert.Equal(ServicePaymentStatus.Refunded, (await w.PaymentAsync()).Status);

        await Assert.ThrowsAsync<DomainRuleException>(async () => await w.Payments.RefundAsync(w.PaymentId, 100, "again", "admin-1"));
        var run = await w.Payments.RunFundsAsync();

        Assert.Equal(0, run.Released);
        Assert.Empty(w.Stripe.TransferRequests);
    }

    // ─── The payer says "all fine" ───

    [Fact]
    public async Task ConfirmOk_ReleasesAtOnce_EvenBeforeTheTimeIsDue_AndAskingAgainIsNotAnError()
    {
        using var w = await DueAsync();
        var payment = await w.S.Db.ServiceRequestPayments.SingleAsync(p => p.Id == w.PaymentId);
        payment.ReleaseAt = w.Now.AddMinutes(45);
        await w.S.Db.SaveChangesAsync();
        w.S.Db.ChangeTracker.Clear();

        var released = await w.Payments.ConfirmOkAsync(w.RequestId);
        var again = await w.Payments.ConfirmOkAsync(w.RequestId);

        Assert.Equal(ServicePaymentStatus.Released, released.Status);
        Assert.Equal(ServicePaymentStatus.Released, again.Status);
        Assert.Single(w.Stripe.TransferRequests);
        Assert.NotNull(released.ReleaseConfirmedAt);
        Assert.Equal(ServiceRequestActorParty.Customer, (await w.RequestAsync()).PaidBy);
    }

    [Fact]
    public async Task ConfirmOk_BeforeTheWorkIsCompleted_IsRefused()
    {
        using var w = await StartAsync(ServiceRequestStatus.InCorso);
        await w.Payments.CaptureForTakeAsync(await w.RequestAsync());

        var refusal = await Assert.ThrowsAsync<DomainRuleException>(async () => await w.Payments.ConfirmOkAsync(w.RequestId));

        Assert.Equal("service_payment_not_confirmable", refusal.Code);
        Assert.Empty(w.Stripe.TransferRequests);
    }

    [Fact]
    public async Task ConfirmOk_WhenTheTransferCannotBeMadeNow_TheWordOfThePayerStands_AndTheJobPays()
    {
        using var w = await DueAsync();
        w.Stripe.FailNextTransfer(Refusal());

        var payment = await w.Payments.ConfirmOkAsync(w.RequestId);

        Assert.Equal(ServicePaymentStatus.TransferFailed, payment.Status);
        Assert.NotNull(payment.ReleaseConfirmedAt);

        w.S.Clock.Advance(TimeSpan.FromMinutes(16));
        var run = await w.Payments.RunFundsAsync();

        Assert.Equal(1, run.Released);
        Assert.Equal(ServicePaymentStatus.Released, (await w.PaymentAsync()).Status);
    }

    // ─── A request that closes gives the customer's money back ───

    [Fact]
    public async Task ARefusedRequest_ReleasesTheBlock_NothingIsRefundedBecauseNothingWasCharged()
    {
        using var w = await StartAsync(ServiceRequestStatus.Rifiutato);

        await w.Payments.SettleClosedRequestAsync(w.RequestId);

        Assert.Equal(ServicePaymentStatus.Canceled, (await w.PaymentAsync()).Status);
        Assert.Equal("canceled", w.Stripe.Intent(IntentId).Status);
        Assert.Empty(w.Stripe.RefundRequests);
        Assert.Empty(w.Stripe.Captures);
    }

    [Fact]
    public async Task ACancelledRequest_AfterTheCharge_IsRefundedInFull_TheCommissionIncluded()
    {
        using var w = await StartAsync();
        await w.Payments.CaptureForTakeAsync(await w.RequestAsync());
        await w.S.ChangeRequestAsync(w.RequestId, r => r.Status = ServiceRequestStatus.Annullato);

        await w.Payments.SettleClosedRequestAsync(w.RequestId);

        var refund = Assert.Single(w.Stripe.RefundRequests);
        Assert.Equal(Amount, refund.AmountCents); // the whole price, not the net (decision D6)
        var payment = await w.PaymentAsync();
        Assert.Equal(ServicePaymentStatus.Refunded, payment.Status);
        Assert.Equal(Amount, payment.RefundedCents);
        Assert.Equal(ServicePaymentRefundOrigin.System, (await w.S.RefundsOfAsync(w.PaymentId)).Single().Origin);
        Assert.Single(w.S.EmailsOf("service-payment-refunded-payer"));
        Assert.Empty(w.S.EmailsOf("service-payment-refunded-supplier")); // the supplier cancelled or lost it: not news
    }

    [Fact]
    public async Task ACardChargedJustBeforeTheCancellation_IsRefunded_NotDropped()
    {
        using var w = await StartAsync(ServiceRequestStatus.Annullato);
        w.Stripe.ChargeOnNextCancel(); // the supplier's capture came a moment before

        await w.Payments.SettleClosedRequestAsync(w.RequestId);

        Assert.Equal(Amount, Assert.Single(w.Stripe.RefundRequests).AmountCents);
        Assert.Equal(ServicePaymentStatus.Refunded, (await w.PaymentAsync()).Status);
    }

    [Fact]
    public async Task SettlingTwice_GivesTheMoneyBackOnce()
    {
        using var w = await StartAsync();
        await w.Payments.CaptureForTakeAsync(await w.RequestAsync());
        await w.S.ChangeRequestAsync(w.RequestId, r => r.Status = ServiceRequestStatus.Annullato);

        await w.Payments.SettleClosedRequestAsync(w.RequestId);
        await w.Payments.SettleClosedRequestAsync(w.RequestId);
        var run = await w.Payments.RunFundsAsync();

        Assert.Single(w.Stripe.RefundRequests);
        Assert.Equal(0, run.ClosedSettled);
        Assert.Equal(Amount, (await w.PaymentAsync()).RefundedCents);
    }

    [Fact]
    public async Task ARequestThatIsStillAlive_IsNeverSettled()
    {
        using var w = await StartAsync(ServiceRequestStatus.Richiesto);

        await w.Payments.SettleClosedRequestAsync(w.RequestId);

        Assert.Equal(ServicePaymentStatus.Authorized, (await w.PaymentAsync()).Status);
        Assert.Empty(w.Stripe.Cancellations);
    }

    [Fact]
    public async Task MoneyAlreadyReleased_IsLeftAlone_ByTheSettlement()
    {
        using var w = await DueAsync();
        await w.Payments.RunFundsAsync();
        await w.S.ChangeRequestAsync(w.RequestId, r => r.Status = ServiceRequestStatus.Annullato);

        await w.Payments.SettleClosedRequestAsync(w.RequestId);

        Assert.Empty(w.Stripe.RefundRequests);
        Assert.Equal(ServicePaymentStatus.Released, (await w.PaymentAsync()).Status);
    }

    [Fact]
    public async Task TheJob_SettlesTheMoneyOfRequestsThatClosedWithoutTheirHook()
    {
        using var w = await StartAsync();
        await w.Payments.CaptureForTakeAsync(await w.RequestAsync());
        await w.S.ChangeRequestAsync(w.RequestId, r => r.Status = ServiceRequestStatus.Annullato);

        var run = await w.Payments.RunFundsAsync();

        Assert.Equal(1, run.ClosedSettled);
        Assert.Equal(ServicePaymentStatus.Refunded, (await w.PaymentAsync()).Status);
    }

    [Fact]
    public async Task TheJob_ReleasesTheBlocksThatOutlivedTheirUsableTime()
    {
        using var w = await StartAsync(ServiceRequestStatus.Richiesto);
        w.S.Clock.Advance(TimeSpan.FromHours(49));

        var run = await w.Payments.RunFundsAsync();

        Assert.Equal(1, run.AuthorizationsLapsed);
        Assert.Equal("canceled", w.Stripe.Intent(IntentId).Status);
        Assert.Equal(ServicePaymentStatus.Canceled, (await w.PaymentAsync()).Status);
    }

    [Fact]
    public async Task TheJob_WarnsTheAdminsOnce_WhenMoneyHasBeenHeldAlmostTheMaximum()
    {
        using var w = await DueAsync();
        await w.S.AddAdminAsync();
        var payment = await w.S.Db.ServiceRequestPayments.SingleAsync(p => p.Id == w.PaymentId);
        payment.ReleaseAt = null; // not due: the job only warns
        payment.CapturedAt = w.Now.AddDays(-54);
        await w.S.Db.SaveChangesAsync();
        w.S.Db.ChangeTracker.Clear();

        var first = await w.Payments.RunFundsAsync();
        var second = await w.Payments.RunFundsAsync();

        Assert.Equal(1, first.HoldWarnings);
        Assert.Equal(0, second.HoldWarnings);
        Assert.Single(w.S.EmailsOf("service-funds-alert"));
    }

    // ─── What Stripe reports must match the payment ───

    [Fact]
    public async Task Webhook_TheChargeIsRecorded_OnceAndInAnyOrder()
    {
        using var w = await StartAsync();
        var charged = Event(w, "payment_intent.succeeded", received: Amount, chargeId: "ch_event");

        var first = await w.Payments.ApplyPaymentIntentEventAsync(charged);
        var duplicate = await w.Payments.ApplyPaymentIntentEventAsync(charged);
        var late = await w.Payments.ApplyPaymentIntentEventAsync(Event(w, "payment_intent.canceled"));

        Assert.Empty(first);
        Assert.Empty(duplicate);
        Assert.Empty(late); // a canceled event after the charge changes nothing
        var payment = await w.PaymentAsync();
        Assert.Equal(ServicePaymentStatus.Held, payment.Status);
        Assert.Equal("ch_event", payment.StripeChargeId);
        Assert.NotNull(payment.CapturedAt);
    }

    [Theory]
    [InlineData(5_000, null, null)] // another amount received
    [InlineData(6_000, "acct_other", null)] // an event of a connected account
    [InlineData(6_000, null, 600L)] // an application fee: the commission is never a fee
    public async Task Webhook_AChargeThatDiffers_NeedsAReview_AndIsNeverHeld(long received, string? account, long? fee)
    {
        using var w = await StartAsync();

        var notices = await w.Payments.ApplyPaymentIntentEventAsync(Event(w, "payment_intent.succeeded", received: received, account: account, fee: fee));

        var notice = Assert.Single(notices);
        Assert.Equal(ServicePaymentNoticeKind.FundsAlert, notice.Kind);
        Assert.Equal(ServicePaymentStatus.NeedsReview, (await w.PaymentAsync()).Status);
    }

    [Fact]
    public async Task Webhook_ABlockThatEndedWithoutACharge_DropsThePayment()
    {
        using var w = await StartAsync();

        await w.Payments.ApplyPaymentIntentEventAsync(Event(w, "payment_intent.canceled"));

        var payment = await w.PaymentAsync();
        Assert.Equal(ServicePaymentStatus.Canceled, payment.Status);
        Assert.NotNull(payment.CanceledAt);
    }

    [Fact]
    public async Task Webhook_ATransferWhoseAnswerWasLost_ReleasesThePayment_AndASecondOneIsAnAlert()
    {
        using var w = await DueAsync();
        var created = Transfer(w, "tr_lost", Net);

        var first = await w.Payments.ApplyTransferEventAsync(created);
        var same = await w.Payments.ApplyTransferEventAsync(created);
        var other = await w.Payments.ApplyTransferEventAsync(Transfer(w, "tr_other", Net));

        Assert.True(first.Handled);
        Assert.Contains(first.Notices, n => n.Kind == ServicePaymentNoticeKind.Released);
        Assert.Empty(same.Notices);
        Assert.Equal("second_transfer", Assert.Single(other.Notices).Detail);
        var payment = await w.PaymentAsync();
        Assert.Equal(ServicePaymentStatus.Released, payment.Status);
        Assert.Equal("tr_lost", payment.StripeTransferId);
        Assert.Equal(ServiceRequestStatus.Pagato, (await w.RequestAsync()).Status);
    }

    [Fact]
    public async Task Webhook_ATransferForAnotherAmount_NeedsAReview_AndNobodyIsMarkedPaid()
    {
        using var w = await DueAsync();

        var result = await w.Payments.ApplyTransferEventAsync(Transfer(w, "tr_wrong", Amount)); // the commission would go to the supplier

        Assert.Equal(ServicePaymentNoticeKind.FundsAlert, Assert.Single(result.Notices).Kind);
        Assert.Equal(ServicePaymentStatus.NeedsReview, (await w.PaymentAsync()).Status);
        Assert.Equal(ServiceRequestStatus.Completato, (await w.RequestAsync()).Status);
    }

    [Fact]
    public async Task Webhook_ACardDispute_IsRecordedOnThePayment_AndLostMoneyIsAnAlert()
    {
        using var w = await DueAsync();
        await w.Payments.RunFundsAsync();

        var opened = await w.Payments.ApplyDisputeCreatedAsync(new ServiceDisputeEvent("evt_dp", "dp_1", IntentId, null, Amount, "eur", "fraudulent", "needs_response"));
        var lost = await w.Payments.ApplyDisputeChangedAsync(
            new ServiceDisputeEvent("evt_dp2", "dp_1", IntentId, null, Amount, "eur", "fraudulent", "lost", "charge.dispute.closed"));

        Assert.Equal(ServicePaymentNoticeKind.Disputed, Assert.Single(opened.Notices).Kind);
        var alert = Assert.Single(lost.Notices);
        Assert.Equal(ServicePaymentNoticeKind.FundsAlert, alert.Kind);
        Assert.Contains("after_release", alert.Detail, StringComparison.Ordinal);
        var payment = await w.PaymentAsync();
        Assert.Equal("dp_1", payment.CardDisputeId);
        Assert.Equal("lost", payment.CardDisputeStatus);
        Assert.Equal(ServicePaymentStatus.Released, payment.Status); // nothing is recovered from the supplier automatically
    }

    // ─── Completing the work ───

    [Fact]
    public async Task Complete_FixesWhenTheMoneyIsDue_AndRefusesAnotherAmount()
    {
        using var w = await StartAsync(ServiceRequestStatus.InCorso, showcase: true);
        await w.Payments.CaptureForTakeAsync(await w.RequestAsync());
        var request = await w.RequestAsync();
        var lines = new[] { new ServiceRequestPriceLine(ServiceRequestPriceLineKinds.Base, "Pulizia", Amount) };

        // A different total (or an extra) is money nobody agreed to.
        var refusal = await Assert.ThrowsAsync<DomainRuleException>(async () => await w.Payments.PlanAsync(request, 7_000, lines, false));
        Assert.Equal("service_payment_amount_locked", refusal.Code);

        var plan = await w.Payments.PlanAsync(request, Amount, lines, false);
        await w.S.Db.SaveChangesAsync();

        Assert.Equal(ServiceRequestPaymentMode.Online, plan.Mode);
        Assert.Null(plan.Payment); // the payment exists since the booking
        Assert.Null(plan.Token); // no link: the customer already paid
        Assert.Equal(w.Now.AddMinutes(60), (await w.PaymentAsync()).ReleaseAt);
    }

    [Fact]
    public async Task Complete_TheMomentTheMoneyIsDueIsFixedOnce()
    {
        using var w = await StartAsync(ServiceRequestStatus.InCorso, showcase: true);
        await w.Payments.CaptureForTakeAsync(await w.RequestAsync());
        var request = await w.RequestAsync();
        var lines = new[] { new ServiceRequestPriceLine(ServiceRequestPriceLineKinds.Base, "Pulizia", Amount) };

        await w.Payments.PlanAsync(request, Amount, lines, false);
        await w.S.Db.SaveChangesAsync();
        w.S.Clock.Advance(TimeSpan.FromMinutes(20));
        await w.Payments.PlanAsync(request, Amount, lines, false);
        await w.S.Db.SaveChangesAsync();

        Assert.Equal(w.Now.AddMinutes(-20).AddMinutes(60), (await w.PaymentAsync()).ReleaseAt);
    }

    // ─── Whether a booking is paid inside CasaZen ───

    [Theory]
    [InlineData(true, true, 6_000, true)]
    [InlineData(false, true, 6_000, false)] // the flag is off: nothing changes for anybody
    [InlineData(true, false, 6_000, false)] // the supplier's Stripe account cannot take charges and payouts
    [InlineData(true, true, 49, false)] // below the minimum Stripe charges
    [InlineData(true, true, 50, true)]
    public async Task CanAuthorize_OnlyWithTheFlag_AReadyAccount_AndAnAmountStripeAccepts(bool flag, bool ready, int amount, bool expected)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnablePaymentsAsync(ready);
        s.Flags.Set(Casazen.Core.Features.FeatureFlags.SupplierOnlinePayments, flag);

        Assert.Equal(expected, await s.Payments.CanAuthorizeAsync(s.SupplierOrgId, amount));
    }

    [Fact]
    public async Task AuthorizeHold_BlocksTheCardOnThePlatform_AndTheSameHoldIsTheSameIntent()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var hold = Guid.NewGuid();
        var request = new HoldAuthorizationRequest(hold, s.SupplierOrgId, Amount, "Booking");

        var first = await s.Payments.AuthorizeHoldAsync(request);
        var again = await s.Payments.AuthorizeHoldAsync(request);

        Assert.Equal(first.PaymentIntentId, again.PaymentIntentId);
        Assert.False(string.IsNullOrWhiteSpace(first.ClientSecret));
        Assert.Equal(ServiceRequestTestKit.PublishableKey, first.PublishableKey);
        Assert.Equal("EUR", first.Currency);
        var created = s.Kit.PlatformGateway.Created[0];
        Assert.True(created.ManualCapture);
        Assert.Equal(ServiceCharges.HoldTransferGroup(hold), created.TransferGroup);
        Assert.Equal(ServiceCharges.HoldAuthorizationIdempotencyKey(hold), created.IdempotencyKey);
        Assert.Equal(hold.ToString(), created.Metadata[ServiceCharges.HoldMetadataKey]);

        await s.Payments.CancelHoldAuthorizationAsync(hold, first.PaymentIntentId);
        Assert.Equal("canceled", s.Kit.PlatformGateway.Intent(first.PaymentIntentId).Status);
        Assert.Null(await s.Payments.ReadHoldAuthorizationAsync(first.PaymentIntentId)); // nothing left to pay
    }

    // ─── helpers ───

    private static ServicePaymentIntentEvent Event(
        World w,
        string type,
        long received = 0,
        string? account = null,
        long? fee = null,
        string? chargeId = null) =>
        new(
            $"evt_{Guid.NewGuid():N}",
            type,
            IntentId,
            account,
            w.PaymentId,
            Amount,
            received,
            "eur",
            fee,
            null,
            w.Now,
            AmountCapturableCents: 0,
            chargeId);

    private static ServiceTransferEvent Transfer(World w, string id, long amount) =>
        new(
            $"evt_{Guid.NewGuid():N}",
            "transfer.created",
            id,
            amount,
            "eur",
            ServiceRequestScenario.SupplierAccountId,
            $"ch_{IntentId}",
            w.Group,
            false,
            w.PaymentId);
}
