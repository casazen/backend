using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-15c-b (decision VT-D1): what a host pays for a completed job goes through CasaZen, on the real services over the in-memory database and a
/// <see cref="FakePlatformPaymentGateway"/> (Stripe is never reached). One test per rule where a mistake costs money: the payment is created on the
/// platform with the commission as the part not transferred; the host's card is charged on the platform at once, with no fee; the charge makes the
/// money held and the supplier is due the window after it; the supplier is paid once, the net, by the job or by the host's "all fine"; a lost event is
/// made good by the job; the supplier's offline declaration withdraws the PaymentIntent on the platform; the admin's refund keeps the commission when the
/// supplier was at fault and never touches money already released; the export lists the commission CasaZen retained.
/// </summary>
public class HostHeldPaymentTests
{
    private const int Total = ServiceRequestScenario.ServicePriceCents; // 6_000: the commission of 10 % is 600, the net 5_400
    private const int Commission = 600;
    private const int Net = 5_400;
    private const string Admin = "auth0|admin-test";

    private static Task<ServiceRequestScenario> StartAsync(bool held = true) =>
        ServiceRequestScenario.CreateAsync(paymentOptions: new SupplierPaymentsOptions { CommissionPercent = 10m, HeldForHosts = held });

    private static async Task<(ServiceRequest Request, ServiceRequestPayment Payment)> CompletedAsync(ServiceRequestScenario s)
    {
        var request = await s.CompletedOnlineAsync();
        return (request, Assert.Single(await s.PaymentsOfAsync(request.Id)));
    }

    /// <summary>The host opens the payment session, pays with a card, and Stripe tells the webhook: the money is held.</summary>
    private static async Task<ServiceRequestPayment> PaidAsync(ServiceRequestScenario s, ServiceRequestPayment payment)
    {
        await s.Payments.CreateHostSessionAsync(payment.ServiceRequestId, s.HostOrgId);
        var opened = Assert.Single(await s.PaymentsOfAsync(payment.ServiceRequestId));
        var intentId = opened.StripePaymentIntentId!;
        var charge = s.Kit.PlatformGateway.SetStatus(intentId, "succeeded");

        await s.Payments.ApplyPaymentIntentEventAsync(new ServicePaymentIntentEvent(
            "evt_paid",
            "payment_intent.succeeded",
            intentId,
            AccountId: null,
            PaymentId: payment.Id,
            charge.AmountCents,
            charge.AmountReceivedCents,
            charge.Currency,
            charge.ApplicationFeeCents,
            FailureCode: null,
            OccurredAt: s.Clock.GetUtcNow().UtcDateTime,
            AmountCapturableCents: 0,
            charge.LatestChargeId));
        return Assert.Single(await s.PaymentsOfAsync(payment.ServiceRequestId));
    }

    private static SupplierPaymentAdminService AdminOf(ServiceRequestScenario s) =>
        new(
            s.Db,
            Microsoft.Extensions.Options.Options.Create(new SupplierPaymentsOptions { CommissionPercent = 10m }),
            NullLogger<SupplierPaymentAdminService>.Instance,
            s.Clock);

    // ─── The payment is created for the platform ───

    [Fact]
    public async Task Completion_CreatesTheHostsPaymentForThePlatform_WithTheCommissionAsThePartNotTransferred()
    {
        using var s = await StartAsync();

        var (request, payment) = await CompletedAsync(s);

        Assert.Equal(ServicePaymentStatus.Requested, payment.Status);
        Assert.Equal(ServiceFundsModel.PlatformHeld, payment.FundsModel);
        Assert.Equal(ServicePayerKind.Host, payment.PayerKind);
        Assert.Equal(s.HostOrgId, payment.PayerOrgId);
        Assert.Equal(ServiceCharges.TransferGroup(request.Id), payment.TransferGroup);
        Assert.Null(payment.ConnectedAccountId);
        Assert.Equal(Total, payment.AmountCents);
        Assert.Equal(Commission, payment.ApplicationFeeCents); // snapshotted; never sent to Stripe as a fee
        Assert.Equal(Net, payment.NetCents);

        // The link goes to the host as in SP-15b.
        Assert.Contains(s.Emails.Snapshot(), e => e.Template == Casazen.Infrastructure.Email.Templates.EmailTemplates.Names.ServicePaymentRequest);
    }

    [Fact]
    public async Task WithTheOptionOff_TheHostsPaymentIsTheDirectChargeOfSP15a()
    {
        using var s = await StartAsync(held: false);

        var (_, payment) = await CompletedAsync(s);

        Assert.Equal(ServiceFundsModel.DirectCharge, payment.FundsModel);
        Assert.Null(payment.TransferGroup);
    }

    // ─── The host pays: a PaymentIntent on the platform, charged at once, with no fee ───

    [Fact]
    public async Task HostSession_CreatesAPaymentIntentOnThePlatform_ChargedAtOnce_AndReusesItWhileItIsPayable()
    {
        using var s = await StartAsync();
        var (request, payment) = await CompletedAsync(s);

        var session = await s.Payments.CreateHostSessionAsync(request.Id, s.HostOrgId);
        var again = await s.Payments.CreateHostSessionAsync(request.Id, s.HostOrgId);

        Assert.Null(session.StripeAccountId); // Stripe.js is loaded with the publishable key alone
        Assert.False(string.IsNullOrEmpty(session.ClientSecret));
        Assert.Equal(Total, session.AmountCents);
        var created = Assert.Single(s.Kit.PlatformGateway.Created); // the second session reused the first PaymentIntent
        Assert.False(created.ManualCapture); // the work is done: charged when the host pays
        Assert.Equal(Total, created.AmountCents);
        Assert.Equal(ServiceCharges.TransferGroup(request.Id), created.TransferGroup);
        Assert.Equal(payment.Id.ToString(), created.Metadata[ServiceCharges.PaymentMetadataKey]);
        Assert.Equal(session.ClientSecret, again.ClientSecret);
    }

    [Fact]
    public async Task HostSession_OfAnotherOrg_IsNotFound_AndCreatesNothing()
    {
        using var s = await StartAsync();
        var (request, _) = await CompletedAsync(s);

        await Assert.ThrowsAsync<NotFoundException>(async () => await s.Payments.CreateHostSessionAsync(request.Id, Guid.NewGuid()));

        Assert.Empty(s.Kit.PlatformGateway.Created);
    }

    [Fact]
    public async Task HostSession_WhenTheSupplierCannotBePaid_CollectsNothing()
    {
        using var s = await StartAsync();
        var (request, _) = await CompletedAsync(s);
        await s.ConnectSupplierAsync(ready: false); // the supplier's account lost its payouts

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(async () => await s.Payments.CreateHostSessionAsync(request.Id, s.HostOrgId));

        Assert.Equal("service_payment_supplier_not_ready", refusal.Code);
        Assert.Empty(s.Kit.PlatformGateway.Created);
    }

    [Fact]
    public async Task ACardThatIsRefused_LeavesThePaymentPayableAgain_WithTheSamePaymentIntent()
    {
        using var s = await StartAsync();
        var (request, payment) = await CompletedAsync(s);
        await s.Payments.CreateHostSessionAsync(request.Id, s.HostOrgId);
        var intentId = Assert.Single(await s.PaymentsOfAsync(request.Id)).StripePaymentIntentId!;

        await s.Payments.ApplyPaymentIntentEventAsync(new ServicePaymentIntentEvent(
            "evt_declined", "payment_intent.payment_failed", intentId, null, payment.Id, Total, 0, "eur", null, "card_declined", s.Clock.GetUtcNow().UtcDateTime));

        var failed = Assert.Single(await s.PaymentsOfAsync(request.Id));
        Assert.Equal(ServicePaymentStatus.Failed, failed.Status);
        Assert.Equal("card_declined", failed.FailureCode);

        await s.Payments.CreateHostSessionAsync(request.Id, s.HostOrgId); // the payer tries another card on the same PaymentIntent
        Assert.Single(s.Kit.PlatformGateway.Created);
    }

    // ─── The charge: the money is held, the supplier is due the window after it ───

    [Fact]
    public async Task TheCharge_MakesTheMoneyHeld_AndTheSupplierIsDueTheWindowAfterIt()
    {
        using var s = await StartAsync();
        var (request, payment) = await CompletedAsync(s);

        var held = await PaidAsync(s, payment);

        Assert.Equal(ServicePaymentStatus.Held, held.Status);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, held.CapturedAt);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime.AddMinutes(60), held.ReleaseAt); // R3: the charge plus the window
        Assert.Equal(ServicePaymentChannel.Stripe, held.PaidVia);
        Assert.NotNull(held.StripeChargeId);
        Assert.Equal(ServiceRequestStatus.Completato, (await s.ReadAsync(request.Id)).Status); // paid to the supplier only at the release
        Assert.Empty(s.Kit.PlatformGateway.Transfers);
    }

    [Fact]
    public async Task ThePaymentJob_RecordsACharge_WhoseEventWasLost()
    {
        using var s = await StartAsync();
        var (request, payment) = await CompletedAsync(s);
        await s.Payments.CreateHostSessionAsync(request.Id, s.HostOrgId);
        var intentId = Assert.Single(await s.PaymentsOfAsync(request.Id)).StripePaymentIntentId!;
        s.Kit.PlatformGateway.SetStatus(intentId, "succeeded"); // the host paid; the webhook never arrived
        s.Clock.Advance(TimeSpan.FromMinutes(11));

        var run = await s.Payments.SynchronizeAsync();

        Assert.Equal(1, run.PaymentsUpdated);
        var held = Assert.Single(await s.PaymentsOfAsync(request.Id));
        Assert.Equal(payment.Id, held.Id);
        Assert.Equal(ServicePaymentStatus.Held, held.Status);
        Assert.NotNull(held.ReleaseAt);
    }

    // ─── The supplier is paid once, the net ───

    [Fact]
    public async Task TheJob_PaysTheSupplierTheNet_OnceTheWindowHasPassed_AndTheRequestBecomesPaid()
    {
        using var s = await StartAsync();
        var (request, payment) = await CompletedAsync(s);
        await PaidAsync(s, payment);

        var early = await s.Payments.RunFundsAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(61));
        var due = await s.Payments.RunFundsAsync();
        var again = await s.Payments.RunFundsAsync();

        Assert.Equal(0, early.Released);
        Assert.Equal(1, due.Released);
        Assert.Equal(0, again.Released);
        var transfer = Assert.Single(s.Kit.PlatformGateway.Transfers); // one transfer, whatever the runs
        Assert.Equal(Net, transfer.AmountCents);
        Assert.Equal(ServiceRequestScenario.SupplierAccountId, transfer.DestinationAccountId);
        Assert.Equal(ServicePaymentStatus.Released, Assert.Single(await s.PaymentsOfAsync(request.Id)).Status);
        Assert.Equal(ServiceRequestStatus.Pagato, (await s.ReadAsync(request.Id)).Status);
    }

    [Fact]
    public async Task AllFine_OfTheHost_PaysTheSupplierAtOnce_AndOnlyOnce()
    {
        using var s = await StartAsync();
        var (request, payment) = await CompletedAsync(s);
        await PaidAsync(s, payment);

        var confirmed = await s.Service.ConfirmOkAsHostAsync(request.Id, s.HostOrgId);
        var again = await s.Service.ConfirmOkAsHostAsync(request.Id, s.HostOrgId); // a double click

        Assert.Equal(ServiceRequestStatus.Pagato, confirmed.Status);
        Assert.Equal(ServiceRequestStatus.Pagato, again.Status);
        var transfer = Assert.Single(s.Kit.PlatformGateway.Transfers);
        Assert.Equal(Net, transfer.AmountCents);
    }

    [Fact]
    public async Task AllFine_BeforeTheHostHasPaid_HasNothingToRelease()
    {
        using var s = await StartAsync();
        var (request, _) = await CompletedAsync(s);

        var refusal = await Assert.ThrowsAsync<DomainRuleException>(async () => await s.Service.ConfirmOkAsHostAsync(request.Id, s.HostOrgId));

        Assert.Equal("service_payment_not_confirmable", refusal.Code);
        Assert.Empty(s.Kit.PlatformGateway.Transfers);
    }

    [Fact]
    public async Task AllFine_OfAnotherOrg_IsNotFound_AndPaysNobody()
    {
        using var s = await StartAsync();
        var (request, payment) = await CompletedAsync(s);
        await PaidAsync(s, payment);

        await Assert.ThrowsAsync<NotFoundException>(async () => await s.Service.ConfirmOkAsHostAsync(request.Id, Guid.NewGuid()));

        Assert.Empty(s.Kit.PlatformGateway.Transfers);
    }

    // ─── The supplier declares the payment received outside CasaZen ───

    [Fact]
    public async Task TheSuppliersOfflineDeclaration_WithdrawsThePaymentIntentOfThePlatform_SoTheHostCannotPayTwice()
    {
        using var s = await StartAsync();
        var (request, _) = await CompletedAsync(s);
        await s.Payments.CreateHostSessionAsync(request.Id, s.HostOrgId);
        var intentId = Assert.Single(await s.PaymentsOfAsync(request.Id)).StripePaymentIntentId!;

        await s.Service.RecordOfflinePaymentAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, "Contanti");

        Assert.Equal("canceled", s.Kit.PlatformGateway.Intent(intentId).Status);
        var payments = await s.PaymentsOfAsync(request.Id);
        Assert.Contains(payments, p => p.StripePaymentIntentId == intentId && p.Status == ServicePaymentStatus.Canceled);
        Assert.Contains(payments, p => p.PaidVia == ServicePaymentChannel.Offline && p.Status == ServicePaymentStatus.Paid);
    }

    [Fact]
    public async Task TheSuppliersOfflineDeclaration_IsRefused_OnceTheHostHasPaid()
    {
        using var s = await StartAsync();
        var (request, payment) = await CompletedAsync(s);
        await PaidAsync(s, payment);

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(async () =>
            await s.Service.RecordOfflinePaymentAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, "Contanti"));

        Assert.Equal("service_payment_held", refusal.Code); // CasaZen holds the money: it reaches the supplier by the release
        Assert.Equal(ServicePaymentStatus.Held, Assert.Single(await s.PaymentsOfAsync(request.Id)).Status);
    }

    // ─── The admin's refund: R4 keeps the commission, R5 gives everything back, neither touches released money ───

    [Fact]
    public async Task AdminRefund_WhenTheSupplierWasAtFault_GivesBackTheNetOnly_AndNothingIsLeftToRelease()
    {
        using var s = await StartAsync();
        var (request, payment) = await CompletedAsync(s);
        await PaidAsync(s, payment);

        var refund = await s.Payments.RefundAsync(payment.Id, null, "Lavoro non eseguito", Admin, keepCommission: true);
        s.Clock.Advance(TimeSpan.FromMinutes(61));
        var run = await s.Payments.RunFundsAsync();

        Assert.Equal(Net, refund.AmountCents); // the price minus the commission: CasaZen keeps its commission
        Assert.Equal(ServicePaymentRefundStatus.Succeeded, refund.Status);
        var stored = Assert.Single(await s.PaymentsOfAsync(request.Id));
        Assert.Equal(ServicePaymentStatus.PartiallyRefunded, stored.Status);
        Assert.Equal(Net, stored.RefundedCents);
        Assert.Equal(0, run.Released);
        Assert.Empty(s.Kit.PlatformGateway.Transfers); // the supplier gets nothing
    }

    [Fact]
    public async Task AdminRefund_ByDefault_GivesEverythingBack_TheCommissionIncluded()
    {
        using var s = await StartAsync();
        var (request, payment) = await CompletedAsync(s);
        await PaidAsync(s, payment);

        var refund = await s.Payments.RefundAsync(payment.Id, null, "Annullato", Admin);

        Assert.Equal(Total, refund.AmountCents);
        Assert.Equal(ServicePaymentStatus.Refunded, Assert.Single(await s.PaymentsOfAsync(request.Id)).Status);
    }

    [Fact]
    public async Task AdminRefund_KeepingTheCommission_CannotAskForMoreThanTheSuppliersPart()
    {
        using var s = await StartAsync();
        var (_, payment) = await CompletedAsync(s);
        await PaidAsync(s, payment);

        var refusal = await Assert.ThrowsAsync<DomainRuleException>(
            async () => await s.Payments.RefundAsync(payment.Id, Net + 1, null, Admin, keepCommission: true));

        Assert.Equal("service_payment_refund_amount_exceeds", refusal.Code);
        Assert.Empty(s.Kit.PlatformGateway.RefundRequests);
    }

    [Fact]
    public async Task AdminRefund_AfterTheRelease_IsRefused_BecauseTheMoneyIsTheSuppliersNow()
    {
        using var s = await StartAsync();
        var (_, payment) = await CompletedAsync(s);
        await PaidAsync(s, payment);
        s.Clock.Advance(TimeSpan.FromMinutes(61));
        await s.Payments.RunFundsAsync();

        var refusal = await Assert.ThrowsAsync<DomainRuleException>(async () => await s.Payments.RefundAsync(payment.Id, null, null, Admin));

        Assert.Equal("service_payment_not_refundable", refusal.Code);
        Assert.Empty(s.Kit.PlatformGateway.RefundRequests);
    }

    [Fact]
    public async Task AdminRefund_KeepingTheCommission_OfADirectCharge_IsRefused()
    {
        using var s = await StartAsync(held: false);
        var (_, payment) = await CompletedAsync(s);

        var refusal = await Assert.ThrowsAsync<DomainRuleException>(
            async () => await s.Payments.RefundAsync(payment.Id, null, null, Admin, keepCommission: true));

        Assert.Equal("service_payment_refund_amount_invalid", refusal.Code);
    }

    // ─── The export lists the commission CasaZen retained ───

    [Fact]
    public async Task Export_ListsTheCommissionRetained_TheDayTheSupplierIsPaid()
    {
        using var s = await StartAsync();
        var (request, payment) = await CompletedAsync(s);
        await PaidAsync(s, payment);
        s.Clock.Advance(TimeSpan.FromMinutes(61));
        await s.Payments.RunFundsAsync();

        var export = await AdminOf(s).ExportCommissionsAsync(2026, 10);

        var row = Assert.Single(export.Rows);
        Assert.Equal(CommissionExportRowType.Retained, row.Type);
        Assert.Equal(payment.Id, row.PaymentId);
        Assert.Equal(request.Id, row.ServiceRequestId);
        Assert.Equal(Total, row.GrossCents);
        Assert.Equal(Commission, row.CommissionCents);
        Assert.Equal(Net, row.NetCents);
    }

    [Fact]
    public async Task Export_WhenTheSupplierWasAtFault_ListsTheCommissionAlone_AndWhenEverythingWentBack_ListsNothing()
    {
        using var s = await StartAsync();
        var (_, atFault) = await CompletedAsync(s);
        await PaidAsync(s, atFault);
        await s.Payments.RefundAsync(atFault.Id, null, "Lavoro non eseguito", Admin, keepCommission: true);

        var export = await AdminOf(s).ExportCommissionsAsync(2026, 10);

        var row = Assert.Single(export.Rows);
        Assert.Equal(CommissionExportRowType.Retained, row.Type);
        Assert.Equal(Commission, row.GrossCents); // what stayed with CasaZen of the payer's money
        Assert.Equal(Commission, row.CommissionCents);
        Assert.Equal(0, row.NetCents); // the supplier got nothing
    }

    [Fact]
    public async Task Export_OfAPaymentRefundedInFull_HasNoRow_CasaZenKeptNothing()
    {
        using var s = await StartAsync();
        var (_, payment) = await CompletedAsync(s);
        await PaidAsync(s, payment);
        await s.Payments.RefundAsync(payment.Id, null, "Annullato", Admin);

        var export = await AdminOf(s).ExportCommissionsAsync(2026, 10);

        Assert.Empty(export.Rows);
    }
}
