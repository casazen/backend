using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-15a on top of the booking from a supplier's public showcase (SP-10). A showcase request has no host: its <c>OrgId</c> is the
/// supplier's own and its customer is a private person with no account. So it is never paid inside CasaZen (a payment request
/// would reach the supplier itself), and the payment the supplier records as received outside CasaZen has a private payer, not a
/// host org. The history credits the payment to who recorded it, and to the party that asked when nobody else did.
/// </summary>
public class SupplierPaymentShowcaseTests
{
    private const string UserId = ServiceRequestScenario.SupplierUserId;

    private static readonly DateTime Created = new(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);

    // ─── The mode and the payment of a showcase request ───

    [Fact]
    public async Task Take_AShowcaseRequest_IsManualEvenWithTheFlagOnAndTheAccountReady()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        await s.EnablePaymentsAsync();
        var (request, _) = await s.BookedAsync();

        var taken = await s.Service.TakeAsync(request.Id, s.SupplierOrgId, UserId);

        Assert.Equal(ServiceRequestPaymentMode.Manual, taken.PaymentMode);
        Assert.Equal(ServiceRequestPaymentMode.Manual, (await s.ReadAsync(request.Id)).PaymentMode);
        // The same supplier, flag and account: a request of a host is paid inside CasaZen.
        Assert.Equal(ServiceRequestPaymentMode.Online, (await s.TakenAsync()).PaymentMode);
    }

    [Fact]
    public async Task AcceptProposal_AsTheCustomerOfAShowcaseRequest_TakesItAsAManualOneEvenWithTheFlagOnAndTheAccountReady()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        await s.EnablePaymentsAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(20));
        await s.ProposeAsSupplierAsync(request.Id, ServiceRequestScenario.FridayAt14);
        s.Clock.Advance(TimeSpan.FromMinutes(40));

        await s.Kit.Manager.AcceptProposalAsync(credentials);

        // The customer's acceptance takes the request on the supplier's behalf (SP-11) without deciding how it is paid.
        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, saved.Status);
        Assert.Equal(ServiceRequestPaymentMode.Manual, saved.PaymentMode);
    }

    [Fact]
    public async Task Complete_AShowcaseRequest_CreatesNoPaymentAndSendsNoLink()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        await s.EnablePaymentsAsync();
        var (request, _) = await s.BookedAsync();
        await s.Service.TakeAsync(request.Id, s.SupplierOrgId, UserId);
        s.ForgetNotifications();

        var completed = await s.Service.CompleteAsync(
            request.Id, s.SupplierOrgId, new CompleteServiceRequestCommand(FinalAmountCents: ServiceRequestScenario.ServicePriceCents));

        Assert.Equal(ServiceRequestStatus.Completato, completed.Status);
        Assert.Equal(ServiceRequestPaymentMode.Manual, completed.PaymentMode);
        Assert.Empty(await s.PaymentsOfAsync(request.Id));
        Assert.Empty(s.Gateway.Created);
        Assert.Empty(s.Emails.PaymentEmails());
    }

    [Fact]
    public async Task Complete_AShowcaseRequestMarkedOnlineByMistake_StillCreatesNoPaymentAndFallsBackToManual()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        await s.EnablePaymentsAsync();
        var (request, _) = await s.BookedAsync();
        await s.Service.TakeAsync(request.Id, s.SupplierOrgId, UserId);
        await s.ChangeRequestAsync(request.Id, r => r.PaymentMode = ServiceRequestPaymentMode.Online);
        s.ForgetNotifications();

        var completed = await s.Service.CompleteAsync(
            request.Id, s.SupplierOrgId, new CompleteServiceRequestCommand(FinalAmountCents: ServiceRequestScenario.ServicePriceCents));

        // Nothing is addressed to the supplier's own mailbox as if it were the host: the request completes as a manual one.
        Assert.Equal(ServiceRequestPaymentMode.Manual, completed.PaymentMode);
        Assert.Empty(await s.PaymentsOfAsync(request.Id));
        Assert.Empty(s.Emails.PaymentEmails());
    }

    [Fact]
    public async Task RecordOffline_AShowcaseRequest_HasAPrivatePayerWithNoOrg_AndNoOneIsToldBecauseThereIsNoHost()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync();
        await s.Service.TakeAsync(request.Id, s.SupplierOrgId, UserId);
        await s.Service.CompleteAsync(
            request.Id, s.SupplierOrgId, new CompleteServiceRequestCommand(FinalAmountCents: ServiceRequestScenario.ServicePriceCents));
        s.ForgetNotifications();

        var payment = await s.Service.RecordOfflinePaymentAsync(request.Id, s.SupplierOrgId, UserId, reason: null);

        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Pagato, saved.Status);
        Assert.Equal(ServiceRequestActorParty.Supplier, saved.PaidBy);
        Assert.Equal(ServicePayerKind.Private, payment.PayerKind);
        Assert.Null(payment.PayerOrgId);
        Assert.Equal(s.SupplierOrgId, payment.SupplierOrgId);
        Assert.Equal(ServicePaymentChannel.Offline, payment.PaidVia);
        Assert.Equal(ServicePaymentStatus.Paid, payment.Status);
        // No money went through CasaZen: no commission, as for any payment received outside it.
        Assert.Equal(0, payment.ApplicationFeeCents);
        Assert.Equal(payment.AmountCents, payment.NetCents);
        Assert.Empty(s.Emails.PaymentEmails());
    }

    [Fact]
    public async Task History_OfAShowcaseRequestRecordedAsPaid_CreditsThePaymentToTheSupplierAndTheFirstStepToTheCustomer()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync();
        await s.Service.TakeAsync(request.Id, s.SupplierOrgId, UserId);
        await s.Service.CompleteAsync(
            request.Id, s.SupplierOrgId, new CompleteServiceRequestCommand(FinalAmountCents: ServiceRequestScenario.ServicePriceCents));
        await s.Service.RecordOfflinePaymentAsync(request.Id, s.SupplierOrgId, UserId, reason: null);

        var view = await s.Reader.GetAsync(request.Id, s.SupplierOrgId);

        var history = Assert.IsAssignableFrom<IReadOnlyList<ServiceRequestHistoryEntry>>(view!.History);
        Assert.Equal(ServiceRequestActorParty.Customer, history[0].Actor);
        Assert.Equal(ServiceRequestStatus.Pagato, history[^1].Status);
        Assert.Equal(ServiceRequestActorParty.Supplier, history[^1].Actor);
    }

    // ─── Who paid, in the history (the two records the two tasks added) ───

    [Theory]
    [InlineData(ServiceRequestActorParty.Host, null, ServiceRequestActorParty.Host)]
    [InlineData(ServiceRequestActorParty.Host, ServiceRequestActorParty.Supplier, ServiceRequestActorParty.Supplier)]
    [InlineData(ServiceRequestActorParty.Customer, null, ServiceRequestActorParty.Customer)]
    [InlineData(ServiceRequestActorParty.Customer, ServiceRequestActorParty.Supplier, ServiceRequestActorParty.Supplier)]
    public void History_ThePaymentIsCreditedToWhoRecordedIt_ElseToThePartyThatAsked(
        ServiceRequestActorParty requester,
        ServiceRequestActorParty? paidBy,
        ServiceRequestActorParty expectedPaymentActor)
    {
        var paid = Created.AddDays(3);

        var steps = ServiceRequestHistory.Build(
            new ServiceRequestMilestones(
                ServiceRequestStatus.Pagato,
                Created,
                paid,
                Created.AddHours(1),
                Created.AddDays(2),
                paid,
                null,
                PaidBy: paidBy,
                Requester: requester),
            takenByName: null);

        Assert.Equal(requester, steps[0].Actor);
        Assert.Equal(ServiceRequestStatus.Pagato, steps[^1].Status);
        Assert.Equal(expectedPaymentActor, steps[^1].Actor);
    }
}
