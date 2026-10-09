using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-15a, decision D5: the supplier records that it was paid outside CasaZen (<c>POST api/supplier/requests/{id}/payment/offline</c>).
/// No money went through CasaZen, so there is no commission. On a request that was to be paid inside CasaZen it is a traced
/// exception: the reason is required and kept, the host is told, and a payment still waiting is withdrawn (its PaymentIntent
/// canceled). A payment that is paid or in progress on Stripe always wins.
/// </summary>
public class SupplierPaymentOfflineTests
{
    private const string UserId = ServiceRequestScenario.SupplierUserId;

    // ─── A request paid by hand ───

    [Fact]
    public async Task RecordOffline_AManualRequest_NeedsNoReason_AndKeepsTheOfflinePaymentWithoutCommission()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();
        await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId);
        s.ForgetNotifications();
        s.Clock.Advance(TimeSpan.FromHours(2));

        var payment = await s.Service.RecordOfflinePaymentAsync(taken.Id, s.SupplierOrgId, UserId, reason: null);

        var request = await s.ReadAsync(taken.Id);
        Assert.Equal(ServiceRequestStatus.Pagato, request.Status);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, request.PaidAt);
        // The history credits the payment to who recorded it.
        Assert.Equal(ServiceRequestActorParty.Supplier, request.PaidBy);
        Assert.Equal(ServicePaymentStatus.Paid, payment.Status);
        Assert.Equal(ServicePaymentChannel.Offline, payment.PaidVia);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, payment.PaidAt);
        Assert.Equal(ServiceRequestScenario.ServicePriceCents, payment.AmountCents);
        // No money went through CasaZen: no commission, the net is the whole amount.
        Assert.Equal(0m, payment.CommissionPercent);
        Assert.Equal(0, payment.ApplicationFeeCents);
        Assert.Equal(payment.AmountCents, payment.NetCents);
        Assert.Null(payment.StripePaymentIntentId);
        Assert.Null(payment.OfflineNote);
        Assert.Equal(UserId, payment.MarkedPaidByUserId);
        Assert.Equal(s.HostOrgId, payment.PayerOrgId);
        Assert.Equal(s.SupplierOrgId, payment.SupplierOrgId);
        Assert.Equal(request.Id, payment.ServiceRequestId);
        Assert.Empty(s.Gateway.Created);

        // The host is told, and only the host.
        var email = Assert.Single(s.Emails.PaymentEmails());
        Assert.Equal(EmailTemplates.Names.ServicePaymentOfflineRecorded, email.Template);
        Assert.Equal("host@test.com", email.To);
        Assert.Contains("60,00 €", email.Content.HtmlBody);
        Assert.DoesNotContain("Motivo indicato dal fornitore", email.Content.HtmlBody);
    }

    [Fact]
    public async Task RecordOffline_AManualRequestWithAReason_KeepsAndShowsIt()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();
        await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId);

        var payment = await s.Service.RecordOfflinePaymentAsync(taken.Id, s.SupplierOrgId, UserId, "  Bonifico del 3 ottobre  ");

        Assert.Equal("Bonifico del 3 ottobre", payment.OfflineNote);
        var email = Assert.Single(s.Emails.PaymentEmails());
        Assert.Contains("Bonifico del 3 ottobre", email.Content.HtmlBody);
    }

    // ─── A request paid inside CasaZen: a traced exception ───

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RecordOffline_ARequestPaidInsideCasaZen_NeedsTheReason(string? reason)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var completed = await s.CompletedOnlineAsync();
        s.ForgetNotifications();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => s.Service.RecordOfflinePaymentAsync(completed.Id, s.SupplierOrgId, UserId, reason));

        Assert.Equal(ServicePaymentErrors.OfflineReasonRequired, ex.Code);
        Assert.Equal("service_payment_offline_reason_required", ex.Code);
        Assert.Equal(ServiceRequestStatus.Completato, (await s.ReadAsync(completed.Id)).Status);
        var payment = await s.OnlyPaymentOfAsync(completed.Id);
        Assert.Equal(ServicePaymentStatus.Requested, payment.Status);
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task RecordOffline_ARequestPaidInsideCasaZen_WithdrawsTheWaitingPayment_AndKeepsTheTrace()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var completed = await s.CompletedOnlineAsync();
        var waiting = await s.OnlyPaymentOfAsync(completed.Id);
        s.ForgetNotifications();
        s.Clock.Advance(TimeSpan.FromHours(5));

        var recorded = await s.Service.RecordOfflinePaymentAsync(completed.Id, s.SupplierOrgId, UserId, "Il cliente ha pagato in contanti");

        var request = await s.ReadAsync(completed.Id);
        Assert.Equal(ServiceRequestStatus.Pagato, request.Status);
        // The request keeps its mode: it was to be paid inside CasaZen, and the exception is what the payment row records.
        Assert.Equal(ServiceRequestPaymentMode.Online, request.PaymentMode);
        var all = await s.PaymentsOfAsync(completed.Id);
        Assert.Equal(2, all.Count);
        var withdrawn = Assert.Single(all, p => p.Id == waiting.Id);
        Assert.Equal(ServicePaymentStatus.Canceled, withdrawn.Status);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, withdrawn.CanceledAt);
        var offline = Assert.Single(all, p => p.Id == recorded.Id);
        Assert.Equal(ServicePaymentStatus.Paid, offline.Status);
        Assert.Equal(ServicePaymentChannel.Offline, offline.PaidVia);
        Assert.Equal("Il cliente ha pagato in contanti", offline.OfflineNote);
        Assert.Equal(0, offline.ApplicationFeeCents);
        Assert.Equal(UserId, offline.MarkedPaidByUserId);
        // Only one live payment is left: the offline one (the unique index of PostgreSQL allows exactly that).
        Assert.Single(all, p => p.Status != ServicePaymentStatus.Canceled);
        var email = Assert.Single(s.Emails.PaymentEmails());
        Assert.Contains("Il cliente ha pagato in contanti", email.Content.HtmlBody);
    }

    [Fact]
    public async Task RecordOffline_APaymentIntentThePayerCouldStillPay_IsCanceledOnStripe()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var completed = await s.CompletedOnlineAsync();
        var session = await OpenSessionAsync(s, completed.Id);
        var intentId = Assert.Single(s.Gateway.Intents).Id;

        await s.Service.RecordOfflinePaymentAsync(completed.Id, s.SupplierOrgId, UserId, "Pagato a mano");

        // The payer must not be able to pay online an amount the supplier declared received.
        var canceled = Assert.Single(s.Gateway.Canceled);
        Assert.Equal(intentId, canceled.PaymentIntentId);
        Assert.Equal(ServiceRequestScenario.SupplierAccountId, canceled.AccountId);
        Assert.Equal(ServiceCharges.CancellationIdempotencyKey(session.PaymentId, intentId), canceled.IdempotencyKey);
        Assert.Equal("canceled", Assert.Single(s.Gateway.Intents).Status);
        Assert.Equal(ServiceRequestStatus.Pagato, (await s.ReadAsync(completed.Id)).Status);
    }

    [Fact]
    public async Task RecordOffline_ThePaymentIntentLivesOnADeletedAccount_TheDeclarationIsStillRecorded()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var completed = await s.CompletedOnlineAsync();
        await OpenSessionAsync(s, completed.Id);
        s.Gateway.DeleteAccount(ServiceRequestScenario.SupplierAccountId);

        var recorded = await s.Service.RecordOfflinePaymentAsync(completed.Id, s.SupplierOrgId, UserId, "Pagato a mano");

        // Nobody can pay on an account that does not exist: the supplier is not blocked by a PaymentIntent that cannot be read.
        Assert.Equal(ServicePaymentStatus.Paid, recorded.Status);
        Assert.Equal(ServiceRequestStatus.Pagato, (await s.ReadAsync(completed.Id)).Status);
        Assert.Empty(s.Gateway.Canceled);
        var all = await s.PaymentsOfAsync(completed.Id);
        Assert.Equal(ServicePaymentStatus.Canceled, Assert.Single(all, p => p.Id != recorded.Id).Status);
    }

    [Theory]
    [InlineData("succeeded")]
    [InlineData("processing")]
    [InlineData("requires_capture")]
    public async Task RecordOffline_APaymentPaidOrInProgressOnStripe_Wins_AndTheDeclarationIsRefused(string stripeStatus)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var completed = await s.CompletedOnlineAsync();
        await OpenSessionAsync(s, completed.Id);
        var intentId = Assert.Single(s.Gateway.Intents).Id;
        s.Gateway.SetStatus(intentId, stripeStatus);
        s.ForgetNotifications();

        var ex = await Assert.ThrowsAsync<DomainConflictException>(
            () => s.Service.RecordOfflinePaymentAsync(completed.Id, s.SupplierOrgId, UserId, "Pagato a mano"));

        // The same money is never counted twice: the request is not marked paid, the online payment is shown as in progress.
        Assert.Equal(ServicePaymentErrors.InFlight, ex.Code);
        Assert.Equal(ServiceRequestStatus.Completato, (await s.ReadAsync(completed.Id)).Status);
        var payment = await s.OnlyPaymentOfAsync(completed.Id);
        Assert.Equal(ServicePaymentStatus.Processing, payment.Status);
        Assert.Equal(intentId, payment.StripePaymentIntentId);
        // A payment that is paid or in progress is never canceled.
        Assert.Empty(s.Gateway.Canceled);
        Assert.Empty(s.Emails.Snapshot());
    }

    [Theory]
    [InlineData(ServicePaymentStatus.Paid)]
    [InlineData(ServicePaymentStatus.PartiallyRefunded)]
    [InlineData(ServicePaymentStatus.Refunded)]
    public async Task RecordOffline_APaymentAlreadyPaid_Is409(ServicePaymentStatus status)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var completed = await s.CompletedOnlineAsync();
        await SupplierPaymentRequestTests.ChangePaymentAsync(s, completed.Id, p =>
        {
            p.Status = status;
            p.PaidAt = ServiceRequestScenario.Instant.UtcDateTime;
            p.PaidVia = ServicePaymentChannel.Stripe;
        });

        var ex = await Assert.ThrowsAsync<DomainConflictException>(
            () => s.Service.RecordOfflinePaymentAsync(completed.Id, s.SupplierOrgId, UserId, "Pagato a mano"));

        Assert.Equal(ServicePaymentErrors.NotPayable, ex.Code);
        Assert.Equal(ServicePaymentErrors.AlreadyPaidMessageKey, ex.MessageKey);
        Assert.Equal(ServiceRequestStatus.Completato, (await s.ReadAsync(completed.Id)).Status);
    }

    [Theory]
    [InlineData(ServicePaymentStatus.Processing)]
    [InlineData(ServicePaymentStatus.NeedsReview)]
    public async Task RecordOffline_APaymentBeingProcessedOrToReview_Is409(ServicePaymentStatus status)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var completed = await s.CompletedOnlineAsync();
        await SupplierPaymentRequestTests.ChangePaymentAsync(s, completed.Id, p => p.Status = status);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(
            () => s.Service.RecordOfflinePaymentAsync(completed.Id, s.SupplierOrgId, UserId, "Pagato a mano"));

        Assert.Equal(ServicePaymentErrors.InFlight, ex.Code);
        Assert.Equal(ServiceRequestStatus.Completato, (await s.ReadAsync(completed.Id)).Status);
    }

    // ─── When it is not possible ───

    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto)]
    [InlineData(ServiceRequestStatus.PresoInCarico)]
    [InlineData(ServiceRequestStatus.InCorso)]
    [InlineData(ServiceRequestStatus.Pagato)]
    [InlineData(ServiceRequestStatus.Rifiutato)]
    [InlineData(ServiceRequestStatus.Annullato)]
    public async Task RecordOffline_ARequestNotCompleted_Is422WithTheMarkPaidMessage(ServiceRequestStatus status)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedAsync(status, r => r.FinalAmountCents = 6_000);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => s.Service.RecordOfflinePaymentAsync(request.Id, s.SupplierOrgId, UserId, "Pagato a mano"));

        Assert.Equal(ServiceRequestErrorCodes.InvalidTransition, ex.Code);
        Assert.Equal(ServiceRequestErrorCodes.CannotMarkPaidMessageKey, ex.MessageKey);
        Assert.Equal(status, (await s.ReadAsync(request.Id)).Status);
        Assert.Empty(await s.PaymentsOfAsync(request.Id));
    }

    [Fact]
    public async Task RecordOffline_Twice_TheSecondIs422_AndThereIsOnePayment()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();
        await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId);
        await s.Service.RecordOfflinePaymentAsync(taken.Id, s.SupplierOrgId, UserId, null);

        await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.RecordOfflinePaymentAsync(taken.Id, s.SupplierOrgId, UserId, null));

        Assert.Single(await s.PaymentsOfAsync(taken.Id));
    }

    [Fact]
    public async Task RecordOffline_ARequestWithoutAFinalAmount_Is422()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync(withService: false);
        await s.Service.TakeAsync(request.Id, s.SupplierOrgId, UserId);
        var completed = await s.Service.CompleteAsync(request.Id, s.SupplierOrgId);
        Assert.Null(completed.FinalAmountCents);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.RecordOfflinePaymentAsync(request.Id, s.SupplierOrgId, UserId, null));

        Assert.Equal(ServicePaymentErrors.AmountRequired, ex.Code);
        Assert.Equal(ServiceRequestStatus.Completato, (await s.ReadAsync(request.Id)).Status);
        Assert.Empty(await s.PaymentsOfAsync(request.Id));
    }

    [Fact]
    public async Task RecordOffline_AReasonLongerThanTheColumn_IsAProgrammingError()
    {
        // The endpoint refuses it with a 400 before (MaxLength 500); the service does not silently cut a trace.
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();
        await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => s.Service.RecordOfflinePaymentAsync(taken.Id, s.SupplierOrgId, UserId, new string('x', ServicePaymentLimits.OfflineNoteMaxLength + 1)));

        Assert.Equal(ServiceRequestStatus.Completato, (await s.ReadAsync(taken.Id)).Status);
    }

    [Fact]
    public async Task RecordOffline_ARequestOfAnotherSupplier_IsForbidden_AndASuspendedSupplierRefused()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();
        await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId);
        var other = await s.AddOtherSupplierAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service.RecordOfflinePaymentAsync(taken.Id, other, UserId, null));
        await s.SetSupplierStatusAsync(SupplierStatus.Suspended);
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.RecordOfflinePaymentAsync(taken.Id, s.SupplierOrgId, UserId, null));

        Assert.Equal(ServiceRequestErrorCodes.SupplierNotActive, ex.Code);
        Assert.Equal(ServiceRequestStatus.Completato, (await s.ReadAsync(taken.Id)).Status);
        Assert.Empty(await s.PaymentsOfAsync(taken.Id));
    }

    [Fact]
    public async Task RecordOffline_TheSaveFails_NothingIsRecordedAndNoNoticeIsSent()
    {
        var interceptor = new FailingSaveInterceptor();
        using var s = await ServiceRequestScenario.CreateAsync(saveInterceptor: interceptor);
        var taken = await s.TakenAsync();
        await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId);
        s.ForgetNotifications();

        interceptor.Failure = new DbUpdateConcurrencyException("the request changed");
        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => s.Service.RecordOfflinePaymentAsync(taken.Id, s.SupplierOrgId, UserId, null));
        interceptor.Failure = null;

        Assert.Equal(ServiceRequestErrorCodes.StateChanged, ex.Code);
        Assert.Empty(await s.PaymentsOfAsync(taken.Id));
        Assert.Empty(s.Emails.Snapshot());
    }

    /// <summary>The payer opens the payment session of the request's only payment (a PaymentIntent is created on the fake Stripe).</summary>
    private static async Task<ServicePaymentSession> OpenSessionAsync(ServiceRequestScenario s, Guid requestId)
    {
        var payment = await s.OnlyPaymentOfAsync(requestId);
        var link = ServicePaymentTestSupport.LinkOf(Assert.Single(s.Emails.RequestEmails()).Content);
        return await s.Payments.CreatePublicSessionAsync(payment.Id, link.Token);
    }
}
