using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-15a, decisions D2 and D5: how a request is paid is decided <b>once, when the supplier takes it</b>. <c>Online</c> only if the
/// flag <c>SupplierOnlinePayments</c> is on and the supplier's Stripe account can take charges <b>and</b> payouts (no payment
/// before the KYC), <c>Manual</c> otherwise; the requests that exist before the payments are <c>Manual</c>; and a request paid
/// inside CasaZen cannot be marked paid by hand by the host.
/// </summary>
public class SupplierPaymentModeTests
{
    [Fact]
    public async Task Take_PaymentsFlagOffEvenWithAReadyAccount_IsManual()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.ConnectSupplierAsync(ready: true);
        Assert.False(s.Flags.IsEnabled(FeatureFlags.SupplierOnlinePayments));

        var taken = await s.TakenAsync();

        Assert.Equal(ServiceRequestPaymentMode.Manual, taken.PaymentMode);
        Assert.Equal(ServiceRequestPaymentMode.Manual, (await s.ReadAsync(taken.Id)).PaymentMode);
    }

    [Fact]
    public async Task Take_FlagOnAndAccountReady_IsOnlineAndSavedOnTheRequest()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnablePaymentsAsync();

        var taken = await s.TakenAsync();

        Assert.Equal(ServiceRequestPaymentMode.Online, taken.PaymentMode);
        Assert.Equal(ServiceRequestPaymentMode.Online, (await s.ReadAsync(taken.Id)).PaymentMode);
        // Taking a request creates no payment and reaches no Stripe: the payment is born with the completion.
        Assert.Empty(await s.PaymentsOfAsync(taken.Id));
        Assert.Empty(s.Gateway.Created);
        Assert.Empty(s.Emails.PaymentEmails());
    }

    [Fact]
    public async Task Take_FlagOnButTheSupplierHasNoAccount_IsManual()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        s.Flags.Set(FeatureFlags.SupplierOnlinePayments, true);

        var taken = await s.TakenAsync();

        Assert.Equal(ServiceRequestPaymentMode.Manual, taken.PaymentMode);
    }

    [Theory]
    [InlineData(true, false)] // charges enabled, payouts not: the KYC is not done
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Take_FlagOnButTheAccountCannotTakeChargesAndPayouts_IsManual(bool charges, bool payouts)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        s.Flags.Set(FeatureFlags.SupplierOnlinePayments, true);
        await s.ConnectSupplierAsync(ready: true);
        var org = await s.Db.Orgs.SingleAsync(o => o.Id == s.SupplierOrgId);
        org.ConnectChargesEnabled = charges;
        org.ConnectPayoutsEnabled = payouts;
        await s.Db.SaveChangesAsync();

        var taken = await s.TakenAsync();

        Assert.Equal(ServiceRequestPaymentMode.Manual, taken.PaymentMode);
    }

    [Fact]
    public async Task Take_TheModeIsFixedThere_ALaterChangeOfTheAccountDoesNotChangeIt()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnablePaymentsAsync();
        var taken = await s.TakenAsync();

        // Stripe disables the account after the take: the request stays Online (its payment waits for the supplier to be ready).
        await s.ConnectSupplierAsync(ready: false);
        var started = await s.Service.StartAsync(taken.Id, s.SupplierOrgId);

        Assert.Equal(ServiceRequestPaymentMode.Online, started.PaymentMode);
        Assert.Equal(ServiceRequestPaymentMode.Online, (await s.ReadAsync(taken.Id)).PaymentMode);
    }

    [Fact]
    public async Task Take_OfAManualRequest_StaysManualWhenThePaymentsAreEnabledLater()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.ConnectSupplierAsync(ready: true);
        var taken = await s.TakenAsync();

        s.Flags.Set(FeatureFlags.SupplierOnlinePayments, true);
        var completed = await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId);

        Assert.Equal(ServiceRequestPaymentMode.Manual, completed.PaymentMode);
        Assert.Empty(await s.PaymentsOfAsync(taken.Id));
    }

    [Fact]
    public async Task AcceptProposal_TheHostTakesTheRequestForTheSupplier_SoItIsPaidTheWayTheSupplierCanBePaidNow()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnablePaymentsAsync();
        var request = await s.RequestAsync();
        await s.Service.ProposeTimeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt10));

        var accepted = await s.Service.AcceptProposalAsync(request.Id, s.HostOrgId);

        Assert.Equal(ServiceRequestStatus.PresoInCarico, accepted.Status);
        Assert.Equal(ServiceRequestPaymentMode.Online, accepted.PaymentMode);
        Assert.Equal(ServiceRequestPaymentMode.Online, (await s.ReadAsync(request.Id)).PaymentMode);
    }

    [Fact]
    public async Task AcceptProposal_WithTheAccountNotReady_IsManual()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnablePaymentsAsync(ready: false);
        var request = await s.RequestAsync();
        await s.Service.ProposeTimeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt10));

        var accepted = await s.Service.AcceptProposalAsync(request.Id, s.HostOrgId);

        Assert.Equal(ServiceRequestPaymentMode.Manual, accepted.PaymentMode);
    }

    [Fact]
    public async Task BatchAccept_EachRequestGetsTheModeOfTheMomentItIsTaken()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnablePaymentsAsync();
        var first = await s.RequestAsync();
        var second = await s.RequestAsync();

        var results = await s.Service.AcceptManyAsync(s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, [first.Id, second.Id]);

        Assert.All(results, r => Assert.True(r.Accepted));
        Assert.Equal(ServiceRequestPaymentMode.Online, (await s.ReadAsync(first.Id)).PaymentMode);
        Assert.Equal(ServiceRequestPaymentMode.Online, (await s.ReadAsync(second.Id)).PaymentMode);
    }

    [Fact]
    public async Task RequestsThatExistBeforeThePayments_AreManual_AndCanBeMarkedPaidByTheHost()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnablePaymentsAsync();
        // A completed request written straight to the table, without the new column: the database default (0) is Manual.
        var legacy = await s.SeedAsync(ServiceRequestStatus.Completato, r => r.CompletedAt = ServiceRequestScenario.Instant.UtcDateTime);

        Assert.Equal(ServiceRequestPaymentMode.Manual, (await s.ReadAsync(legacy.Id)).PaymentMode);
        var paid = await s.Service.MarkPaidAsync(legacy.Id, s.HostOrgId);

        Assert.Equal(ServiceRequestStatus.Pagato, paid.Status);
    }

    [Fact]
    public async Task MarkPaid_ARequestPaidInsideCasaZen_Is422AndNothingChanges()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var completed = await s.CompletedOnlineAsync();
        s.ForgetNotifications();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.MarkPaidAsync(completed.Id, s.HostOrgId));

        Assert.Equal(ServicePaymentErrors.OnlinePayment, ex.Code);
        Assert.Equal("service_request_online_payment", ex.Code);
        Assert.Equal(ServicePaymentErrors.OnlinePaymentMessageKey, ex.MessageKey);
        var saved = await s.ReadAsync(completed.Id);
        Assert.Equal(ServiceRequestStatus.Completato, saved.Status);
        Assert.Null(saved.PaidAt);
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task MarkPaid_AManualRequest_StillWorksAsBefore()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();
        await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId);

        var paid = await s.Service.MarkPaidAsync(taken.Id, s.HostOrgId);

        Assert.Equal(ServiceRequestStatus.Pagato, paid.Status);
        Assert.NotNull((await s.ReadAsync(taken.Id)).PaidAt);
    }

    [Fact]
    public async Task ResolveMode_ForAnUnknownOrg_IsManual()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        s.Flags.Set(FeatureFlags.SupplierOnlinePayments, true);

        Assert.Equal(ServiceRequestPaymentMode.Manual, await s.Payments.ResolveModeAsync(Guid.NewGuid()));
    }
}
