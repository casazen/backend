using System.Net;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Stripe;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-15c-c (decisions VT-D1 and VT-D16): the complaint about a job paid through CasaZen and its dispute, on the real <see cref="SupplierPaymentService"/>
/// over the in-memory database and a <see cref="FakePlatformPaymentGateway"/> (Stripe is never reached). One test per rule where a mistake costs money
/// or trust: a complaint <b>stops</b> the release and a release that came first <b>refuses</b> the complaint (never both win); there is <b>one</b> dispute
/// per payment; the supplier has its time to reply and an admin cannot decide before; a decision for the customer gives back <b>at most the supplier's
/// part</b>, <b>keeps the commission</b>, reserves its refund <b>with</b> the decision and frees the payment only when that refund has succeeded; a
/// decision twice moves nothing twice; a withdrawal pays the supplier; a job nobody marked completed is reported after its time and CasaZen opens the
/// complaint itself after 48 hours; the words of the parties are in no e-mail.
/// </summary>
public class ServiceDisputeTests
{
    private const int Amount = 6_000; // 10 %: commission 600, net 5_400
    private const int Commission = 600;
    private const int Net = 5_400;
    private const string IntentId = "pi_dispute_test";
    private const string CustomerWords = "Il bagno e ancora sporco, parola riservata del cliente";
    private const string SupplierWords = "Ho pulito tutto, risposta riservata del fornitore";
    private const string AdminWords = "Nota riservata dell'amministratore";
    private const string Admin = "auth0|admin-1";

    // ─── The worlds ───

    private sealed class World(ServiceRequestScenario scenario, Guid requestId, Guid paymentId) : IDisposable
    {
        public ServiceRequestScenario S { get; } = scenario;

        public Guid RequestId { get; } = requestId;

        public Guid PaymentId { get; } = paymentId;

        public SupplierPaymentService Service => S.Payments;

        public FakePlatformPaymentGateway Stripe => S.Kit.PlatformGateway;

        public DateTime Now => S.Clock.GetUtcNow().UtcDateTime;

        public async Task<ServiceRequestPayment> PaymentAsync() =>
            await S.Db.ServiceRequestPayments.AsNoTracking().SingleAsync(p => p.Id == PaymentId);

        public async Task<ServicePaymentDispute> DisputeAsync() =>
            await S.Db.ServicePaymentDisputes.AsNoTracking().SingleAsync(d => d.ServiceRequestPaymentId == PaymentId);

        public async Task<ServiceRequest> RequestAsync() => await S.ReadAsync(RequestId);

        public async Task<List<ServiceRequestPaymentRefund>> RefundsAsync() =>
            await S.Db.ServiceRequestPaymentRefunds.AsNoTracking().Where(r => r.ServiceRequestPaymentId == PaymentId).OrderBy(r => r.Sequence).ToListAsync();

        public Task<ServiceDisputePayerView> OpenAsync(ServiceDisputeReason reason = ServiceDisputeReason.PoorQuality, string? text = CustomerWords) =>
            Service.OpenDisputeAsync(new OpenServiceDisputeRequest(RequestId, ServiceDisputeOpener.Host, reason, text, S.HostOrgId, "auth0|host-member"));

        public Task<ServiceDisputeSupplierView> ReplyAsync(string text = SupplierWords) =>
            Service.ReplyToDisputeAsync(RequestId, S.SupplierOrgId, text);

        public async Task<AdminServiceDisputeDetail> ResolveAsync(string outcome, int? refundCents = null, string? note = AdminWords) =>
            await Service.ResolveDisputeAsync(
                (await DisputeAsync()).Id, new ResolveServiceDisputeRequest(outcome, refundCents, note), Admin);

        /// <summary>Opens the complaint, the supplier replies: the dispute waits for an admin.</summary>
        public async Task<ServicePaymentDispute> ReadyForTheAdminAsync()
        {
            await OpenAsync();
            await ReplyAsync();
            return await DisputeAsync();
        }

        public void Dispose() => S.Dispose();
    }

    /// <summary>
    /// A request of a host paid through CasaZen: the card was charged, the money is held. <paramref name="releaseInMinutes"/> is when it is due (the work is
    /// completed 30 minutes ago); a job that is not completed has no such time.
    /// </summary>
    private static async Task<World> HeldAsync(
        ServiceRequestStatus status = ServiceRequestStatus.Completato,
        int releaseInMinutes = 30,
        int scheduledEndHours = -3)
    {
        var s = await ServiceRequestScenario.CreateAsync();
        await s.EnablePaymentsAsync();
        await s.AddAdminAsync();
        var now = s.Clock.GetUtcNow().UtcDateTime;
        var request = await s.SeedAsync(status, r =>
        {
            r.PaymentMode = ServiceRequestPaymentMode.Online;
            r.FinalAmountCents = Amount;
            r.TakenAt = now.AddHours(-6);
            r.ScheduledStartUtc = now.AddHours(scheduledEndHours - 2);
            r.ScheduledEndUtc = now.AddHours(scheduledEndHours);
            if (status is ServiceRequestStatus.Completato or ServiceRequestStatus.Pagato)
                r.CompletedAt = now.AddMinutes(-30);
        });

        var group = ServiceCharges.HoldTransferGroup(Guid.NewGuid());
        s.Kit.PlatformGateway.AddBlock(IntentId, Amount, group);
        var payment = await s.Payments.AddAuthorizedPaymentAsync(
            request,
            new AuthorizedPaymentInput(
                IntentId, Amount, now, group, [new ServiceRequestPriceLine(ServiceRequestPriceLineKinds.Base, "Pulizia", Amount)]));
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();

        await s.Payments.CaptureForTakeAsync(await s.ReadAsync(request.Id));

        var held = await s.Db.ServiceRequestPayments.SingleAsync(p => p.Id == payment.Id);
        held.ReleaseAt = status is ServiceRequestStatus.Completato ? now.AddMinutes(releaseInMinutes) : null;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        return new World(s, request.Id, payment.Id);
    }

    private static StripeException Refusal(string code = "insufficient_funds") =>
        new(HttpStatusCode.BadRequest, new StripeError { Type = "invalid_request_error", Code = code }, "refused");

    // ─── The payer complains: the money stops ───

    [Fact]
    public async Task Open_ForACompletedJob_InsideTheWindow_StopsTheMoney_AndTheSupplierIsAskedToReply()
    {
        using var w = await HeldAsync();

        var view = await w.OpenAsync();

        Assert.Equal(ServiceDisputeStatus.AwaitingSupplier, view.Status);
        Assert.Equal(ServiceDisputeReason.PoorQuality, view.Reason);
        Assert.Equal(ServiceDisputeOpener.Host, view.OpenedBy);
        Assert.False(view.NoShow);
        Assert.Equal(CustomerWords, view.Text);
        Assert.Equal(w.Now.AddHours(48), view.SupplierReplyDueAt); // DisputeSupplierReplyHours
        Assert.False(view.SupplierReplied);

        var payment = await w.PaymentAsync();
        Assert.Equal(ServicePaymentStatus.Disputed, payment.Status);
        var dispute = await w.DisputeAsync();
        Assert.Equal(w.RequestId, dispute.ServiceRequestId);
        Assert.Equal(w.S.SupplierOrgId, dispute.SupplierOrgId);
        Assert.Equal("auth0|host-member", dispute.OpenedByUserId);

        // Nothing is paid out while the dispute stands, however many times the job runs and however long it takes.
        w.S.Clock.Advance(TimeSpan.FromHours(10));
        var run = await w.Service.RunFundsAsync();
        Assert.Equal(0, run.Released);
        Assert.Empty(w.Stripe.TransferRequests);

        // The supplier is asked to reply, the payer is told; the admins have nothing to do yet.
        Assert.Single(w.S.EmailsOf(EmailTemplates.Names.ServiceDisputeOpenedSupplier, "supplier@test.com"));
        Assert.Single(w.S.EmailsOf(EmailTemplates.Names.ServiceDisputeOpenedPayer, "host@test.com"));
        Assert.Empty(w.S.EmailsOf(EmailTemplates.Names.ServiceDisputeAdminAlert));
    }

    [Fact]
    public async Task Open_TheEmails_NeverCarryTheWordsOfTheParties()
    {
        using var w = await HeldAsync();
        await w.OpenAsync();
        await w.ReplyAsync();

        foreach (var (_, content, _) in w.S.Emails.Snapshot())
        {
            Assert.DoesNotContain("riservata", content.HtmlBody, StringComparison.Ordinal);
            Assert.DoesNotContain("sporco", content.HtmlBody, StringComparison.Ordinal);
            Assert.DoesNotContain("pulito", content.HtmlBody, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Open_OutsideTheWindow_IsRefused_AndNothingChanges()
    {
        using var w = await HeldAsync(releaseInMinutes: -1);

        var refusal = await Assert.ThrowsAsync<DomainRuleException>(async () => await w.OpenAsync());

        Assert.Equal("service_dispute_window_closed", refusal.Code);
        Assert.True(refusal.Extensions.ContainsKey("windowEndedAt"));
        Assert.Equal(ServicePaymentStatus.Held, (await w.PaymentAsync()).Status);
        Assert.Empty(w.S.Db.ServicePaymentDisputes);
    }

    [Fact]
    public async Task Open_AfterTheMoneyWasReleased_IsRefused_TheSupplierWasPaidOnce()
    {
        using var w = await HeldAsync(releaseInMinutes: -1);
        await w.Service.RunFundsAsync();

        var refusal = await Assert.ThrowsAsync<DomainRuleException>(async () => await w.OpenAsync());

        Assert.Equal("service_dispute_window_closed", refusal.Code);
        Assert.Equal(ServicePaymentStatus.Released, (await w.PaymentAsync()).Status);
        Assert.Single(w.Stripe.TransferRequests);
    }

    [Fact]
    public async Task Open_AfterTheWordAllFine_IsRefused()
    {
        using var w = await HeldAsync();
        await w.Service.ConfirmOkAsync(w.RequestId);

        var refusal = await Assert.ThrowsAsync<DomainRuleException>(async () => await w.OpenAsync());

        Assert.Equal("service_dispute_window_closed", refusal.Code);
        Assert.Single(w.Stripe.TransferRequests);
    }

    [Fact]
    public async Task AllFine_AfterTheComplaint_IsRefused_AndTheMoneyStaysStopped()
    {
        using var w = await HeldAsync();
        await w.OpenAsync();

        var refusal = await Assert.ThrowsAsync<DomainRuleException>(async () => await w.Service.ConfirmOkAsync(w.RequestId));

        Assert.Equal("service_payment_not_confirmable", refusal.Code);
        Assert.Equal(ServicePaymentStatus.Disputed, (await w.PaymentAsync()).Status);
        Assert.Empty(w.Stripe.TransferRequests);
    }

    [Fact]
    public async Task Open_Twice_IsTheSameDispute_AndTheSupplierIsAskedOnce()
    {
        using var w = await HeldAsync();

        var first = await w.OpenAsync();
        var second = await w.OpenAsync(ServiceDisputeReason.NotDone, "un altro testo");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(ServiceDisputeReason.PoorQuality, second.Reason); // the first complaint stands, the second writes nothing
        Assert.Equal(CustomerWords, second.Text);
        Assert.Single(w.S.Db.ServicePaymentDisputes);
        Assert.Single(w.S.EmailsOf(EmailTemplates.Names.ServiceDisputeOpenedSupplier));
    }

    [Fact]
    public async Task Open_ByAnotherOrg_IsNotFound_AndTheMoneyIsUntouched()
    {
        using var w = await HeldAsync();

        var refusal = await Assert.ThrowsAsync<NotFoundException>(
            async () => await w.Service.OpenDisputeAsync(
                new OpenServiceDisputeRequest(w.RequestId, ServiceDisputeOpener.Host, ServiceDisputeReason.NotDone, null, Guid.NewGuid(), null)));
        var customer = await Assert.ThrowsAsync<NotFoundException>(
            async () => await w.Service.OpenDisputeAsync(
                new OpenServiceDisputeRequest(w.RequestId, ServiceDisputeOpener.Customer, ServiceDisputeReason.NotDone, null)));

        Assert.Equal("service_dispute_not_found", refusal.Code);
        Assert.Equal("service_dispute_not_found", customer.Code); // a host's payment is not a showcase customer's
        Assert.Equal(ServicePaymentStatus.Held, (await w.PaymentAsync()).Status);
        Assert.Empty(w.S.Db.ServicePaymentDisputes);
    }

    [Fact]
    public async Task Open_ForARequestWithNoMoneyHeldByCasaZen_HasNothingToReport()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedAsync(ServiceRequestStatus.Completato);

        var refusal = await Assert.ThrowsAsync<DomainRuleException>(
            async () => await s.Payments.OpenDisputeAsync(
                new OpenServiceDisputeRequest(request.Id, ServiceDisputeOpener.Host, ServiceDisputeReason.NotDone, null, s.HostOrgId, null)));

        Assert.Equal("service_dispute_not_reportable", refusal.Code);
    }

    [Theory]
    [InlineData("other", null, "text")] // the reason "other" needs the words
    [InlineData("other", "   ", "text")]
    [InlineData("not_done", "x\u0000y", "text")] // a control character
    [InlineData("nonsense", "ok", "reason")]
    public async Task Open_WithAReasonOrTextThatIsNotValid_IsRefusedBeforeAnythingIsLocked(string reason, string? text, string field)
    {
        using var w = await HeldAsync();

        var request = new OpenServiceDisputeRequest(
            w.RequestId,
            ServiceDisputeOpener.Host,
            ServiceDisputeRules.TryParseReason(reason, out var parsed) ? parsed : (ServiceDisputeReason)99,
            text,
            w.S.HostOrgId,
            null);
        var refusal = await Assert.ThrowsAsync<DomainRuleException>(async () => await w.Service.OpenDisputeAsync(request));

        Assert.Equal("service_dispute_invalid", refusal.Code);
        Assert.Equal(new[] { field }, Assert.IsType<string[]>(refusal.Extensions["fields"]));
        Assert.Equal(ServicePaymentStatus.Held, (await w.PaymentAsync()).Status);
    }

    [Fact]
    public async Task Open_TheTextOfTheComplaintIsNotRequired_ForAReasonThatSaysIt()
    {
        using var w = await HeldAsync();

        var view = await w.OpenAsync(ServiceDisputeReason.NotDone, text: null);

        Assert.Null(view.Text);
        Assert.Equal(ServicePaymentStatus.Disputed, (await w.PaymentAsync()).Status);
    }

    // ─── A job nobody marked completed ───

    [Fact]
    public async Task Open_ForAJobNobodyMarkedCompleted_IsTooEarlyBeforeItsTimeEnded_AndANoShowAfter()
    {
        using var w = await HeldAsync(ServiceRequestStatus.PresoInCarico, scheduledEndHours: 2);

        var early = await Assert.ThrowsAsync<DomainRuleException>(async () => await w.OpenAsync(ServiceDisputeReason.NotDone, null));
        Assert.Equal("service_dispute_too_early", early.Code);
        Assert.Equal(ServicePaymentStatus.Held, (await w.PaymentAsync()).Status);

        w.S.Clock.Advance(TimeSpan.FromHours(2)); // the time agreed for the work ended
        var view = await w.OpenAsync(ServiceDisputeReason.NotDone, null);

        Assert.True(view.NoShow);
        Assert.Equal(ServicePaymentStatus.Disputed, (await w.PaymentAsync()).Status);
    }

    // ─── The supplier replies ───

    [Fact]
    public async Task Reply_SendsTheDisputeToTheAdmins_AndTheAdminsAndThePayerAreTold()
    {
        using var w = await HeldAsync();
        await w.OpenAsync();

        var view = await w.ReplyAsync();

        Assert.Equal(ServiceDisputeStatus.AwaitingDecision, view.Status);
        Assert.Equal(SupplierWords, view.Reply);
        Assert.False(view.CanReply);
        var dispute = await w.DisputeAsync();
        Assert.Equal(ServiceDisputeStatus.AwaitingDecision, dispute.Status);
        Assert.Equal(SupplierWords, dispute.SupplierReply);
        Assert.Equal(w.Now.AddHours(72), dispute.DecisionDueAt); // DisputeDecisionTargetHours
        Assert.Equal(ServicePaymentStatus.Disputed, (await w.PaymentAsync()).Status);

        Assert.Single(w.S.EmailsOf(EmailTemplates.Names.ServiceDisputeRepliedPayer, "host@test.com"));
        var alert = Assert.Single(w.S.EmailsOf(EmailTemplates.Names.ServiceDisputeAdminAlert, "admin@test.com"));
        Assert.Contains(DisputeAlertCodes.Replied, alert.Content.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reply_TheSameWordsTwice_IsOneReply_OtherWordsAreARefusedSecondReply()
    {
        using var w = await HeldAsync();
        await w.OpenAsync();
        await w.ReplyAsync();

        var again = await w.ReplyAsync(); // a double click
        var second = await Assert.ThrowsAsync<DomainConflictException>(async () => await w.ReplyAsync("altre parole"));

        Assert.Equal(SupplierWords, again.Reply);
        Assert.Equal("service_dispute_already_replied", second.Code);
        Assert.Equal(SupplierWords, (await w.DisputeAsync()).SupplierReply);
        Assert.Single(w.S.EmailsOf(EmailTemplates.Names.ServiceDisputeRepliedPayer));
    }

    [Fact]
    public async Task Reply_OfAnotherSupplier_IsNotFound_AndNobodyElseReadsTheDispute()
    {
        using var w = await HeldAsync();
        await w.OpenAsync();
        var other = await w.S.AddOtherSupplierAsync();

        var refusal = await Assert.ThrowsAsync<NotFoundException>(async () => await w.Service.ReplyToDisputeAsync(w.RequestId, other, "parole"));

        Assert.Equal("service_dispute_not_found", refusal.Code);
        Assert.Null(await w.Service.FindDisputeForSupplierAsync(w.RequestId, other));
        Assert.NotNull(await w.Service.FindDisputeForSupplierAsync(w.RequestId, w.S.SupplierOrgId));
        Assert.Null((await w.DisputeAsync()).SupplierReply);
    }

    [Fact]
    public async Task Reply_WithoutWords_IsRefused()
    {
        using var w = await HeldAsync();
        await w.OpenAsync();

        var refusal = await Assert.ThrowsAsync<DomainRuleException>(async () => await w.ReplyAsync("   "));

        Assert.Equal("service_dispute_invalid", refusal.Code);
        Assert.Equal(ServiceDisputeStatus.AwaitingSupplier, (await w.DisputeAsync()).Status);
    }

    [Fact]
    public async Task TheSuppliersView_HasThePayersWordsAndItsOwnReply_ButNotTheAdminsNote()
    {
        using var w = await HeldAsync();
        await w.ReadyForTheAdminAsync();
        await w.ResolveAsync("for_supplier");

        var view = await w.Service.FindDisputeForSupplierAsync(w.RequestId, w.S.SupplierOrgId);

        Assert.NotNull(view);
        Assert.Equal(CustomerWords, view.CustomerText);
        Assert.Equal(SupplierWords, view.Reply);
        Assert.Equal(ServiceDisputeStatus.ResolvedForSupplier, view.Status);
        // The admin's note is in no view of the parties: the types do not even have a member for it.
        Assert.DoesNotContain(typeof(ServiceDisputeSupplierView).GetProperties(), p => p.Name.Contains("Admin", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(ServiceDisputePayerView).GetProperties(), p => p.Name.Contains("Admin", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(ServiceDisputePayerView).GetProperties(), p => p.Name.Equals("Reply", StringComparison.Ordinal));
    }

    // ─── Time passes: the job ───

    [Fact]
    public async Task Job_WhenTheSupplierDoesNotReplyInTime_TheDisputeGoesToTheAdminsOnce()
    {
        using var w = await HeldAsync();
        await w.OpenAsync();

        w.S.Clock.Advance(TimeSpan.FromHours(47));
        Assert.Equal(0, (await w.Service.RunDisputesAsync()).ReachedAdmins);

        w.S.Clock.Advance(TimeSpan.FromHours(1)); // 48 hours
        var run = await w.Service.RunDisputesAsync();
        var again = await w.Service.RunDisputesAsync();

        Assert.Equal(1, run.ReachedAdmins);
        Assert.Equal(0, again.ReachedAdmins);
        var dispute = await w.DisputeAsync();
        Assert.Equal(ServiceDisputeStatus.AwaitingDecision, dispute.Status);
        Assert.Null(dispute.SupplierReply);
        Assert.Equal(w.Now.AddHours(72), dispute.DecisionDueAt);
        var alert = Assert.Single(w.S.EmailsOf(EmailTemplates.Names.ServiceDisputeAdminAlert, "admin@test.com"));
        Assert.Contains(DisputeAlertCodes.NoReply, alert.Content.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reply_AfterTheTime_IsStillTaken_WhileTheDisputeIsOpen()
    {
        using var w = await HeldAsync();
        await w.OpenAsync();
        w.S.Clock.Advance(TimeSpan.FromHours(49));
        await w.Service.RunDisputesAsync();

        var view = await w.ReplyAsync();

        Assert.Equal(SupplierWords, view.Reply);
        Assert.Equal(ServiceDisputeStatus.AwaitingDecision, view.Status);
        Assert.NotNull((await w.DisputeAsync()).SupplierRepliedAt);
        Assert.Single(w.S.EmailsOf(EmailTemplates.Names.ServiceDisputeAdminAlert)); // the admins were told once, when it reached them
    }

    [Fact]
    public async Task Job_WhenTheDecisionIsOverdue_TheAdminsAreToldOnce_AndNothingIsDecidedForThem()
    {
        using var w = await HeldAsync();
        await w.ReadyForTheAdminAsync();

        w.S.Clock.Advance(TimeSpan.FromHours(71));
        Assert.Equal(0, (await w.Service.RunDisputesAsync()).Overdue);

        w.S.Clock.Advance(TimeSpan.FromHours(1)); // 72 hours: the target
        var run = await w.Service.RunDisputesAsync();
        var again = await w.Service.RunDisputesAsync();

        Assert.Equal(1, run.Overdue);
        Assert.Equal(0, again.Overdue);
        Assert.Equal(2, w.S.EmailsOf(EmailTemplates.Names.ServiceDisputeAdminAlert, "admin@test.com").Count); // when it reached them, and now
        Assert.Equal(ServiceDisputeStatus.AwaitingDecision, (await w.DisputeAsync()).Status);
        Assert.Equal(ServicePaymentStatus.Disputed, (await w.PaymentAsync()).Status);
        Assert.Empty(w.Stripe.TransferRequests);
        Assert.Empty(w.Stripe.RefundRequests);
    }

    // ─── The payer takes the complaint back ───

    [Fact]
    public async Task Withdraw_FreesTheMoney_TheSupplierIsPaidTheNetOnce_AndToldSo()
    {
        using var w = await HeldAsync();
        await w.OpenAsync();

        var view = await w.Service.WithdrawDisputeAsync(w.RequestId, ServiceDisputeOpener.Host, w.S.HostOrgId);
        var again = await w.Service.WithdrawDisputeAsync(w.RequestId, ServiceDisputeOpener.Host, w.S.HostOrgId);

        Assert.Equal(ServiceDisputeStatus.Withdrawn, view.Status);
        Assert.Equal(ServiceDisputeStatus.Withdrawn, again.Status);
        var payment = await w.PaymentAsync();
        Assert.Equal(ServicePaymentStatus.Held, payment.Status);
        Assert.Equal(w.Now, payment.ReleaseAt); // due now: the payer's complaint was the only thing in the way

        var run = await w.Service.RunFundsAsync();
        Assert.Equal(1, run.Released);
        Assert.Equal(Net, Assert.Single(w.Stripe.TransferRequests).AmountCents);
        Assert.Equal(ServicePaymentStatus.Released, (await w.PaymentAsync()).Status);
        Assert.Single(w.S.EmailsOf(EmailTemplates.Names.ServiceDisputeWithdrawnSupplier, "supplier@test.com"));
        Assert.Empty(w.S.Kit.Penalties.Recorded); // taking a complaint back costs the supplier nothing
    }

    [Fact]
    public async Task Withdraw_ByAnotherOrg_IsNotFound()
    {
        using var w = await HeldAsync();
        await w.OpenAsync();

        await Assert.ThrowsAsync<NotFoundException>(
            async () => await w.Service.WithdrawDisputeAsync(w.RequestId, ServiceDisputeOpener.Host, Guid.NewGuid()));

        Assert.Equal(ServicePaymentStatus.Disputed, (await w.PaymentAsync()).Status);
    }

    [Fact]
    public async Task Withdraw_AfterTheDecision_IsARefusedConflict()
    {
        using var w = await HeldAsync();
        await w.ReadyForTheAdminAsync();
        await w.ResolveAsync("for_customer");

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(
            async () => await w.Service.WithdrawDisputeAsync(w.RequestId, ServiceDisputeOpener.Host, w.S.HostOrgId));

        Assert.Equal("service_dispute_not_open", refusal.Code);
    }

    // ─── An admin decides ───

    [Fact]
    public async Task Resolve_BeforeTheSupplierHadItsTime_IsRefused_AndTellsUntilWhen()
    {
        using var w = await HeldAsync();
        await w.OpenAsync();

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(async () => await w.ResolveAsync("for_customer"));

        Assert.Equal("service_dispute_awaiting_supplier", refusal.Code);
        Assert.True(refusal.Extensions.ContainsKey("supplierReplyDueAt"));
        Assert.Equal(ServicePaymentStatus.Disputed, (await w.PaymentAsync()).Status);
        Assert.Empty(w.Stripe.RefundRequests);
    }

    [Fact]
    public async Task Resolve_ForTheSupplier_PaysTheNetAtTheNextRun_AndCostsTheSupplierNothing()
    {
        using var w = await HeldAsync();
        await w.ReadyForTheAdminAsync();

        var detail = await w.ResolveAsync("for_supplier");

        Assert.Equal(ServiceDisputeStatus.ResolvedForSupplier, detail.Summary.Status);
        Assert.Equal(AdminWords, detail.AdminNote);
        Assert.Equal(Admin, detail.ResolvedByUserId);
        var payment = await w.PaymentAsync();
        Assert.Equal(ServicePaymentStatus.Held, payment.Status);
        Assert.Equal(w.Now, payment.ReleaseAt);
        Assert.Empty(w.Stripe.RefundRequests); // nothing goes back to the payer

        await w.Service.RunFundsAsync();
        Assert.Equal(Net, Assert.Single(w.Stripe.TransferRequests).AmountCents);
        Assert.Equal(ServiceRequestStatus.Pagato, (await w.RequestAsync()).Status);
        Assert.Empty(w.S.Kit.Penalties.Recorded);

        Assert.Single(w.S.EmailsOf(EmailTemplates.Names.ServiceDisputeResolvedPayer, "host@test.com"));
        Assert.Single(w.S.EmailsOf(EmailTemplates.Names.ServiceDisputeResolvedSupplier, "supplier@test.com"));
        var audit = Assert.Single(w.S.Db.SupplierAdminAuditEntries, e => e.Action == SupplierAdminAuditAction.ServiceDisputeResolved);
        Assert.Equal(w.S.SupplierOrgId, audit.SupplierOrgId);
        Assert.Equal(Admin, audit.ActorUserId);
        Assert.DoesNotContain("riservata", audit.Reason, StringComparison.Ordinal); // the words of the parties are not in the trail
    }

    [Fact]
    public async Task Resolve_ForTheSupplier_WithAnAmount_IsInvalid()
    {
        using var w = await HeldAsync();
        await w.ReadyForTheAdminAsync();

        var refusal = await Assert.ThrowsAsync<DomainRuleException>(async () => await w.ResolveAsync("for_supplier", refundCents: 100));

        Assert.Equal("service_dispute_invalid", refusal.Code);
        Assert.Equal(ServiceDisputeStatus.AwaitingDecision, (await w.DisputeAsync()).Status);
    }

    [Fact]
    public async Task Resolve_ForTheCustomer_GivesBackTheSuppliersPartOnly_KeepsTheCommission_AndPenalizesTheSupplier()
    {
        using var w = await HeldAsync();
        var before = await w.ReadyForTheAdminAsync();

        var detail = await w.ResolveAsync("for_customer");

        // The refund was reserved with the decision, for the supplier's whole part: the price minus the commission, never the commission.
        var refundRequest = Assert.Single(w.Stripe.RefundRequests);
        Assert.Equal(Net, refundRequest.AmountCents);
        Assert.Equal(ServiceCharges.RefundIdempotencyKey(w.PaymentId, 1), refundRequest.IdempotencyKey);
        var refund = Assert.Single(await w.RefundsAsync());
        Assert.Equal(ServicePaymentRefundOrigin.System, refund.Origin);
        Assert.Equal(ServicePaymentRefundStatus.Succeeded, refund.Status);
        Assert.Equal($"dispute:{before.Id:D}", refund.Reason);
        Assert.Equal(Admin, refund.RequestedByUserId);

        // Everything but the commission went back: the cycle is over, CasaZen keeps 600 and the supplier gets nothing.
        var payment = await w.PaymentAsync();
        Assert.Equal(ServicePaymentStatus.PartiallyRefunded, payment.Status);
        Assert.Equal(Net, payment.RefundedCents);
        Assert.Equal(Amount - Net, Commission);
        var dispute = await w.DisputeAsync();
        Assert.Equal(ServiceDisputeStatus.ResolvedForCustomer, dispute.Status);
        Assert.Equal(Net, dispute.DecidedRefundCents);
        Assert.Equal(Net, dispute.RefundTargetCents);
        Assert.NotNull(dispute.SettledAt);
        Assert.Equal(AdminWords, dispute.AdminNote);

        var run = await w.Service.RunFundsAsync();
        Assert.Equal(0, run.Released);
        Assert.Empty(w.Stripe.TransferRequests);

        Assert.True(detail.Summary.Settled);
        Assert.Equal(ServicePaymentStatus.PartiallyRefunded, detail.PaymentStatus);
        Assert.Equal(0, detail.Summary.RefundableCents);

        var penalty = Assert.Single(w.S.Kit.Penalties.Recorded);
        Assert.Equal(SupplierDisputePenaltyKind.DisputeLost, penalty.Kind);
        Assert.Equal(w.S.SupplierOrgId, penalty.SupplierOrgId);
        Assert.Equal(before.Id, penalty.DisputeId);

        Assert.Single(w.S.EmailsOf(EmailTemplates.Names.ServiceDisputeResolvedPayer, "host@test.com"));
        Assert.Single(w.S.EmailsOf(EmailTemplates.Names.ServiceDisputeResolvedSupplier, "supplier@test.com"));
        Assert.Single(w.S.EmailsOf(EmailTemplates.Names.ServicePaymentRefundedPayer, "host@test.com")); // the refund itself, once it succeeded
        Assert.Empty(w.S.EmailsOf(EmailTemplates.Names.ServicePaymentRefundedSupplier)); // the supplier is told by the decision, not twice
    }

    [Fact]
    public async Task Resolve_ForTheCustomer_WithAPartialAmount_PaysTheSupplierTheRest_AndTheTotalIsTheWholePrice()
    {
        using var w = await HeldAsync();
        await w.ReadyForTheAdminAsync();

        await w.ResolveAsync("for_customer", refundCents: 1_000);

        // The payer got 1_000 back; the payment is held again, due now, only for what the supplier is still owed.
        var payment = await w.PaymentAsync();
        Assert.Equal(ServicePaymentStatus.Held, payment.Status);
        Assert.Equal(1_000, payment.RefundedCents);
        Assert.Equal(w.Now, payment.ReleaseAt);
        Assert.NotNull((await w.DisputeAsync()).SettledAt);

        await w.Service.RunFundsAsync();

        var transfer = Assert.Single(w.Stripe.TransferRequests);
        Assert.Equal(Net - 1_000, transfer.AmountCents);
        Assert.Equal(ServicePaymentStatus.Released, (await w.PaymentAsync()).Status);
        // 6_000 paid = 1_000 back to the payer + 4_400 to the supplier + 600 commission: not a cent more or less.
        Assert.Equal(Commission, Amount - Assert.Single(w.Stripe.RefundRequests).AmountCents - transfer.AmountCents);
        Assert.Equal(ServiceRequestStatus.Pagato, (await w.RequestAsync()).Status);
    }

    [Theory]
    [InlineData(5_401)]
    [InlineData(6_000)] // the whole price: the commission is never given back by a dispute
    public async Task Resolve_ForTheCustomer_MoreThanTheSuppliersPart_IsRefused_AndNothingMoves(int asked)
    {
        using var w = await HeldAsync();
        await w.ReadyForTheAdminAsync();

        var refusal = await Assert.ThrowsAsync<DomainRuleException>(async () => await w.ResolveAsync("for_customer", refundCents: asked));

        Assert.Equal("service_dispute_refund_exceeds", refusal.Code);
        Assert.Equal(Net, refusal.Extensions["maxRefundCents"]);
        Assert.Equal(ServicePaymentStatus.Disputed, (await w.PaymentAsync()).Status);
        Assert.Equal(ServiceDisputeStatus.AwaitingDecision, (await w.DisputeAsync()).Status);
        Assert.Empty(await w.RefundsAsync());
        Assert.Empty(w.Stripe.RefundRequests);
        Assert.Empty(w.S.Kit.Penalties.Recorded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Resolve_ForTheCustomer_ZeroOrLess_IsNotAnAmount(int asked)
    {
        using var w = await HeldAsync();
        await w.ReadyForTheAdminAsync();

        var refusal = await Assert.ThrowsAsync<DomainRuleException>(async () => await w.ResolveAsync("for_customer", refundCents: asked));

        Assert.Equal("service_dispute_refund_invalid", refusal.Code);
        Assert.Empty(await w.RefundsAsync());
    }

    [Fact]
    public async Task Resolve_WithAnOutcomeThatIsNotOne_IsRefused()
    {
        using var w = await HeldAsync();
        await w.ReadyForTheAdminAsync();

        var refusal = await Assert.ThrowsAsync<DomainRuleException>(async () => await w.ResolveAsync("maybe"));

        Assert.Equal("service_dispute_outcome_invalid", refusal.Code);
    }

    [Fact]
    public async Task Resolve_TheSameDecisionTwice_MovesNothingTwice_AnotherDecisionIsAConflict()
    {
        using var w = await HeldAsync();
        await w.ReadyForTheAdminAsync();

        var first = await w.ResolveAsync("for_customer", refundCents: 1_000);
        var again = await w.ResolveAsync("for_customer", refundCents: 1_000);
        var withoutAmount = await w.ResolveAsync("for_customer"); // asking again without the amount is the same decision

        Assert.Equal(first.Summary.Id, again.Summary.Id);
        Assert.Equal(1_000, withoutAmount.Summary.DecidedRefundCents);
        Assert.Single(w.Stripe.RefundRequests);
        Assert.Single(await w.RefundsAsync());
        Assert.Single(w.S.Kit.Penalties.Recorded);
        Assert.Single(w.S.EmailsOf(EmailTemplates.Names.ServiceDisputeResolvedPayer));
        Assert.Single(w.S.Db.SupplierAdminAuditEntries, e => e.Action == SupplierAdminAuditAction.ServiceDisputeResolved);

        var otherAmount = await Assert.ThrowsAsync<DomainConflictException>(async () => await w.ResolveAsync("for_customer", refundCents: 2_000));
        var otherOutcome = await Assert.ThrowsAsync<DomainConflictException>(async () => await w.ResolveAsync("for_supplier"));
        Assert.Equal("service_dispute_state_changed", otherAmount.Code);
        Assert.Equal("service_dispute_state_changed", otherOutcome.Code);
        Assert.Single(w.Stripe.RefundRequests);
    }

    [Fact]
    public async Task Resolve_AfterThePayerWithdrew_IsAConflict_NotASecondDecision()
    {
        using var w = await HeldAsync();
        await w.ReadyForTheAdminAsync();
        var disputeId = (await w.DisputeAsync()).Id;
        await w.Service.WithdrawDisputeAsync(w.RequestId, ServiceDisputeOpener.Host, w.S.HostOrgId); // the payer was first

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(
            async () => await w.Service.ResolveDisputeAsync(disputeId, new ResolveServiceDisputeRequest("for_customer", null, null), Admin));

        Assert.Equal("service_dispute_state_changed", refusal.Code);
        Assert.Empty(w.Stripe.RefundRequests);
        Assert.Empty(w.S.Kit.Penalties.Recorded);
        Assert.Equal(ServiceDisputeStatus.Withdrawn, (await w.DisputeAsync()).Status);
    }

    [Fact]
    public async Task Resolve_ForTheCustomer_WhenStripeRefusesTheRefund_ThePaymentStaysStopped_TheAdminsAreToldOnce_AndARetryCompletesIt()
    {
        using var w = await HeldAsync();
        await w.ReadyForTheAdminAsync();
        w.Stripe.FailNextRefund(Refusal());

        await w.ResolveAsync("for_customer", refundCents: 1_000);

        // The decision stands, the refund did not go: the payment is not freed and the supplier is not paid.
        var refund = Assert.Single(await w.RefundsAsync());
        Assert.Equal(ServicePaymentRefundStatus.Failed, refund.Status);
        var payment = await w.PaymentAsync();
        Assert.Equal(ServicePaymentStatus.Disputed, payment.Status);
        Assert.Equal(0, payment.RefundedCents);
        var dispute = await w.DisputeAsync();
        Assert.Equal(ServiceDisputeStatus.ResolvedForCustomer, dispute.Status);
        Assert.Null(dispute.SettledAt);
        await w.Service.RunFundsAsync();
        Assert.Empty(w.Stripe.TransferRequests);

        // Not before the time to try (30 minutes), then once.
        Assert.Equal(0, (await w.Service.RunDisputesAsync()).RefundAlerts);
        w.S.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(1, (await w.Service.RunDisputesAsync()).RefundAlerts);
        Assert.Equal(0, (await w.Service.RunDisputesAsync()).RefundAlerts);
        Assert.Single(
            w.S.EmailsOf(EmailTemplates.Names.ServiceDisputeAdminAlert, "admin@test.com"),
            e => e.Content.HtmlBody.Contains(DisputeAlertCodes.RefundIncomplete, StringComparison.Ordinal));

        // The admin sends the refund again with the tool that exists (the commission stays with CasaZen): the decision is done.
        await w.Service.RefundAsync(w.PaymentId, 1_000, "retry", Admin, keepCommission: true);

        var freed = await w.PaymentAsync();
        Assert.Equal(ServicePaymentStatus.Held, freed.Status);
        Assert.Equal(1_000, freed.RefundedCents);
        Assert.NotNull((await w.DisputeAsync()).SettledAt);
        await w.Service.RunFundsAsync();
        Assert.Equal(Net - 1_000, Assert.Single(w.Stripe.TransferRequests).AmountCents);
    }

    [Fact]
    public async Task Resolve_ForTheCustomer_WhenTheSinkOfThePenaltyFails_TheDecisionStands()
    {
        using var w = await HeldAsync();
        await w.ReadyForTheAdminAsync();
        w.S.Kit.Penalties.FailNext(new InvalidOperationException("the ranking is down"));

        var detail = await w.ResolveAsync("for_customer");

        Assert.Equal(ServiceDisputeStatus.ResolvedForCustomer, detail.Summary.Status);
        Assert.Equal(ServicePaymentStatus.PartiallyRefunded, (await w.PaymentAsync()).Status);
        Assert.Empty(w.S.Kit.Penalties.Recorded); // lost: the dispute row is the record it is rebuilt from
    }

    [Fact]
    public async Task Resolve_ForTheCustomer_WhenAnAdminRefundedMostOfIt_GivesBackOnlyWhatIsLeft()
    {
        using var w = await HeldAsync();
        await w.OpenAsync();
        await w.Service.RefundAsync(w.PaymentId, 4_000, "goodwill", Admin, keepCommission: true); // the admin's own tool, meanwhile
        await w.ReplyAsync();

        var detail = await w.ResolveAsync("for_customer");

        Assert.Equal(Net - 4_000, detail.Summary.DecidedRefundCents); // 1_400: the rest of the supplier's part
        Assert.Equal(2, w.Stripe.RefundRequests.Count);
        Assert.Equal(Net, (await w.PaymentAsync()).RefundedCents);
        Assert.Equal(ServicePaymentStatus.PartiallyRefunded, (await w.PaymentAsync()).Status);
        Assert.Empty(w.Stripe.TransferRequests);
    }

    [Fact]
    public async Task Resolve_ForTheCustomer_WhenNothingIsLeftOfTheSuppliersPart_TheDecisionStandsWithNoMoney()
    {
        using var w = await HeldAsync();
        await w.OpenAsync();
        await w.Service.RefundAsync(w.PaymentId, Net, "all but the commission", Admin, keepCommission: true);
        await w.ReplyAsync();

        var detail = await w.ResolveAsync("for_customer");

        Assert.Equal(0, detail.Summary.DecidedRefundCents);
        Assert.True(detail.Summary.Settled);
        Assert.Single(w.Stripe.RefundRequests); // only the admin's own refund
        Assert.Single(w.S.Kit.Penalties.Recorded); // the supplier lost all the same
    }

    // ─── The queue of the admins ───

    [Fact]
    public async Task TheAdminsQueue_ListsTheOpenOnes_WithTheNumbersOfTheMoney_AndNoWordsOfAnyone()
    {
        using var w = await HeldAsync();
        await w.ReadyForTheAdminAsync();

        var (items, total) = await w.Service.ListDisputesAsync(new AdminServiceDisputeQuery(null, null, OpenOnly: true, 1, 20));
        var detail = await w.Service.GetDisputeAsync(items[0].Id);

        Assert.Equal(1, total);
        var line = Assert.Single(items);
        Assert.Equal("Supplier Org", line.SupplierName);
        Assert.Equal(ServiceDisputeStatus.AwaitingDecision, line.Status);
        Assert.Equal(Amount, line.AmountCents);
        Assert.Equal(Net, line.NetCents);
        Assert.Equal(Net, line.RefundableCents);
        Assert.False(line.Overdue);
        Assert.Equal(CustomerWords, detail.CustomerText);
        Assert.Equal(SupplierWords, detail.SupplierReply);
        Assert.Equal(Commission, detail.CommissionCents);
        Assert.Equal(ServicePaymentStatus.Disputed, detail.PaymentStatus);
        Assert.Equal(ServiceRequestStatus.Completato, detail.RequestStatus);
        Assert.DoesNotContain(typeof(AdminServiceDisputeSummary).GetProperties(), p => p.Name.Contains("Text", StringComparison.Ordinal) || p.Name.Contains("Reply", StringComparison.OrdinalIgnoreCase) && p.Name != "SupplierReplyDueAt");
    }

    [Fact]
    public async Task TheAdminsQueue_TheFiltersAndThePage_AreAppliedInTheQuery()
    {
        using var w = await HeldAsync();
        await w.ReadyForTheAdminAsync();
        await w.ResolveAsync("for_supplier");

        Assert.Empty((await w.Service.ListDisputesAsync(new AdminServiceDisputeQuery(null, null, OpenOnly: true, 1, 20))).Items);
        Assert.Single((await w.Service.ListDisputesAsync(new AdminServiceDisputeQuery(ServiceDisputeStatus.ResolvedForSupplier, null, false, 1, 20))).Items);
        Assert.Empty((await w.Service.ListDisputesAsync(new AdminServiceDisputeQuery(ServiceDisputeStatus.ResolvedForCustomer, null, false, 1, 20))).Items);
        Assert.Empty((await w.Service.ListDisputesAsync(new AdminServiceDisputeQuery(null, Guid.NewGuid(), false, 1, 20))).Items);
        var (page2, total) = await w.Service.ListDisputesAsync(new AdminServiceDisputeQuery(null, null, false, 2, 1));
        Assert.Empty(page2);
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task GetDispute_OfADisputeThatDoesNotExist_IsNotFound()
    {
        using var w = await HeldAsync();

        var refusal = await Assert.ThrowsAsync<NotFoundException>(async () => await w.Service.GetDisputeAsync(Guid.NewGuid()));

        Assert.Equal("service_dispute_not_found", refusal.Code);
    }

    // ─── The customer of a showcase, through the manager ───

    private const string SupplierAccountId = ServiceRequestScenario.SupplierAccountId;

    private static async Task<(ServiceRequestScenario S, ServiceRequest Request, ShowcaseBookingCredentials Credentials)> BookedAndTakenAsync()
    {
        var s = await ServiceRequestScenario.CreateAsync();
        await s.EnablePaymentsAsync();
        await s.AddAdminAsync();
        var supplier = await s.EnableBookingAsync();

        var (held, token) = await s.HoldAsync(supplier: supplier);
        var hold = Assert.Single(await s.HoldsAsync());
        var charge = s.Kit.PlatformGateway.SetStatus(hold.StripePaymentIntentId!, "requires_capture");
        await s.Kit.Booking.ApplyHoldAuthorizationAsync(new ShowcaseHoldAuthorization(
            hold.OrgId,
            hold.Id,
            new HeldChargeObservation(
                charge.Id, null, charge.AmountCents, charge.AmountCapturableCents, charge.AmountReceivedCents, charge.Currency, charge.ApplicationFeeCents),
            s.Clock.GetUtcNow().UtcDateTime));
        await s.Kit.Booking.ConfirmEmailAsync(supplier, held.Id, token);
        var request = Assert.Single(await s.ShowcaseRequestsAsync());
        await s.TakeAsSupplierAsync(request.Id);
        return (s, request, ShowcaseManageScenario.CredentialsOf(request.PublicCode!));
    }

    [Fact]
    public async Task Customer_ReportsAProblemInsideTheWindow_ThroughTheManager_AndTakesItBack()
    {
        var (s, request, credentials) = await BookedAndTakenAsync();
        using var scope = s;
        await s.Service.CompleteAsync(request.Id, s.SupplierOrgId, new CompleteServiceRequestCommand());

        var view = await s.Kit.Manager.LookupAsync(credentials);
        Assert.True(view.Actions.CanReportProblem);
        Assert.True(view.Actions.CanConfirmOk);
        Assert.False(view.Actions.CanWithdrawProblem);
        Assert.Null(view.Payment!.Dispute);

        var reported = await s.Kit.Manager.ReportProblemAsync(credentials, "poor_quality", CustomerWords);

        // The money stopped: the page shows the complaint, the customer can take it back and can no longer say "all fine" or report again.
        var dispute = reported.Payment!.Dispute!;
        Assert.Equal("AwaitingSupplier", dispute.Status);
        Assert.Equal("poor_quality", dispute.Reason);
        Assert.Equal(CustomerWords, dispute.Text);
        Assert.False(dispute.OpenedByCasaZen);
        Assert.False(dispute.SupplierReplied);
        Assert.Equal("held", reported.Payment.Status); // for the customer the money is held, whatever the reason
        Assert.Null(reported.Payment.ReleaseAtUtc); // no time is running
        Assert.False(reported.Actions.CanReportProblem);
        Assert.False(reported.Actions.CanConfirmOk);
        Assert.True(reported.Actions.CanWithdrawProblem);
        var stopped = await s.Kit.Manager.ReportProblemAsync(credentials, "not_done", "again"); // the same complaint
        Assert.Equal("poor_quality", stopped.Payment!.Dispute!.Reason);
        var early = await Assert.ThrowsAsync<DomainRuleException>(async () => await s.Kit.Manager.ConfirmOkAsync(credentials));
        Assert.Equal("service_payment_not_confirmable", early.Code);
        await s.Payments.RunFundsAsync();
        Assert.Empty(s.Kit.PlatformGateway.Transfers);

        // The customer is told, and so is the supplier; the supplier's reply is not shown to the customer, only that it replied.
        Assert.Single(s.EmailsOf(EmailTemplates.Names.ServiceDisputeOpenedPayer, ShowcaseScenario.CustomerEmail));
        Assert.Single(s.EmailsOf(EmailTemplates.Names.ServiceDisputeOpenedSupplier, "supplier@test.com"));
        await s.Payments.ReplyToDisputeAsync(request.Id, s.SupplierOrgId, SupplierWords);
        var replied = (await s.Kit.Manager.LookupAsync(credentials)).Payment!.Dispute!;
        Assert.True(replied.SupplierReplied);
        Assert.Equal("AwaitingDecision", replied.Status);
        Assert.DoesNotContain(typeof(ShowcaseBookingDisputeView).GetProperties(), p => p.Name.Equals("Reply", StringComparison.Ordinal) || p.Name.Contains("Admin", StringComparison.Ordinal));

        var withdrawn = await s.Kit.Manager.WithdrawProblemAsync(credentials);

        Assert.Equal("Withdrawn", withdrawn.Payment!.Dispute!.Status);
        Assert.False(withdrawn.Actions.CanWithdrawProblem);
        Assert.False(withdrawn.Actions.CanReportProblem); // the time is due now: the supplier is paid
        await s.Payments.RunFundsAsync();
        var transfer = Assert.Single(s.Kit.PlatformGateway.Transfers);
        Assert.Equal(Net, transfer.AmountCents);
        Assert.Equal(SupplierAccountId, transfer.DestinationAccountId);
    }

    [Fact]
    public async Task Customer_ReportsAProblem_ForABookingNotPaidInsideCasaZen_HasNothingToReport()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (_, credentials) = await s.BookedForManagementAsync();

        var refusal = await Assert.ThrowsAsync<DomainRuleException>(
            async () => await s.Kit.Manager.ReportProblemAsync(credentials, "not_done", null));
        var withdraw = await Assert.ThrowsAsync<NotFoundException>(async () => await s.Kit.Manager.WithdrawProblemAsync(credentials));

        Assert.Equal("service_dispute_not_reportable", refusal.Code);
        Assert.Equal("service_dispute_not_found", withdraw.Code);
    }

    [Fact]
    public async Task Customer_WithTheWrongAddress_IsNotFound_AsForEveryOtherAction_AndNothingIsStopped()
    {
        var (s, request, credentials) = await BookedAndTakenAsync();
        using var scope = s;
        await s.Service.CompleteAsync(request.Id, s.SupplierOrgId, new CompleteServiceRequestCommand());
        var stranger = credentials with { Email = "stranger@example.com" };

        var refusal = await Assert.ThrowsAsync<NotFoundException>(async () => await s.Kit.Manager.ReportProblemAsync(stranger, "not_done", null));

        Assert.Equal("supplier_booking_not_found", refusal.Code);
        Assert.Empty(s.Db.ServicePaymentDisputes);
        Assert.Equal(ServicePaymentStatus.Held, Assert.Single(await s.PaymentsOfAsync(request.Id)).Status);
    }

    [Fact]
    public async Task Customer_WithAReasonThatIsNotOne_IsInvalid_NamingTheField()
    {
        var (s, request, credentials) = await BookedAndTakenAsync();
        using var scope = s;
        await s.Service.CompleteAsync(request.Id, s.SupplierOrgId, new CompleteServiceRequestCommand());

        var refusal = await Assert.ThrowsAsync<DomainRuleException>(async () => await s.Kit.Manager.ReportProblemAsync(credentials, "broken", null));

        Assert.Equal("service_dispute_invalid", refusal.Code);
        Assert.Equal(new[] { "reason" }, Assert.IsType<string[]>(refusal.Extensions["fields"]));
        Assert.Empty(s.Db.ServicePaymentDisputes);
    }

    // ─── A job nobody marked completed: the no-show ───

    [Fact]
    public async Task NoShow_TheCustomerCanReportAfterTheTimeEnded_AndCasaZenOpensItItselfAfter48Hours_Once()
    {
        var (s, request, credentials) = await BookedAndTakenAsync();
        using var scope = s;
        var end = (await s.ReadAsync(request.Id)).ScheduledEndUtc!.Value;

        // Before the end of the time agreed there is nothing to report and the job does nothing.
        s.Clock.SetUtcNow(new DateTimeOffset(end.AddMinutes(-1), TimeSpan.Zero));
        Assert.False((await s.Kit.Manager.LookupAsync(credentials)).Actions.CanReportProblem);
        var tooEarly = await Assert.ThrowsAsync<DomainRuleException>(async () => await s.Kit.Manager.ReportProblemAsync(credentials, "not_done", null));
        Assert.Equal("service_dispute_too_early", tooEarly.Code);

        // From the end the customer may report without waiting; CasaZen does not open anything yet.
        s.Clock.SetUtcNow(new DateTimeOffset(end.AddMinutes(1), TimeSpan.Zero));
        Assert.True((await s.Kit.Manager.LookupAsync(credentials)).Actions.CanReportProblem);
        Assert.Equal(0, (await s.Payments.RunDisputesAsync()).NoShowsOpened);
        s.Clock.SetUtcNow(new DateTimeOffset(end.AddHours(47), TimeSpan.Zero));
        Assert.Equal(0, (await s.Payments.RunDisputesAsync()).NoShowsOpened);

        // 48 hours after the end CasaZen opens the complaint for the customer, once.
        s.Clock.SetUtcNow(new DateTimeOffset(end.AddHours(48), TimeSpan.Zero));
        var run = await s.Payments.RunDisputesAsync();
        var again = await s.Payments.RunDisputesAsync();

        Assert.Equal(1, run.NoShowsOpened);
        Assert.Equal(0, again.NoShowsOpened);
        var dispute = Assert.Single(s.Db.ServicePaymentDisputes.AsNoTracking());
        Assert.Equal(ServiceDisputeOpener.System, dispute.OpenedBy);
        Assert.Equal(ServiceDisputeReason.NotDone, dispute.Reason);
        Assert.True(dispute.NoShow);
        Assert.Null(dispute.CustomerText);
        Assert.Equal(ServicePaymentStatus.Disputed, Assert.Single(await s.PaymentsOfAsync(request.Id)).Status);
        var view = await s.Kit.Manager.LookupAsync(credentials);
        Assert.True(view.Payment!.Dispute!.NoShow);
        Assert.True(view.Payment.Dispute.OpenedByCasaZen);
        Assert.True(view.Actions.CanWithdrawProblem);
        Assert.Single(s.EmailsOf(EmailTemplates.Names.ServiceDisputeOpenedPayer, ShowcaseScenario.CustomerEmail));
        Assert.Single(s.EmailsOf(EmailTemplates.Names.ServiceDisputeOpenedSupplier, "supplier@test.com"));
    }

    [Fact]
    public async Task NoShow_TheJobNeverOpensAComplaintForACompletedJob()
    {
        var (s, request, _) = await BookedAndTakenAsync();
        using var scope = s;
        var end = (await s.ReadAsync(request.Id)).ScheduledEndUtc!.Value;
        await s.Service.CompleteAsync(request.Id, s.SupplierOrgId, new CompleteServiceRequestCommand());
        s.Clock.SetUtcNow(new DateTimeOffset(end.AddHours(48), TimeSpan.Zero));

        var run = await s.Payments.RunDisputesAsync();

        Assert.Equal(0, run.NoShowsOpened);
        Assert.Empty(s.Db.ServicePaymentDisputes);
    }

    [Fact]
    public async Task NoShow_DecidedForTheSupplier_CompletesTheRequest_AndTheSupplierIsPaid_WithNoPenalty()
    {
        var (s, request, _) = await BookedAndTakenAsync();
        using var scope = s;
        var end = (await s.ReadAsync(request.Id)).ScheduledEndUtc!.Value;
        s.Clock.SetUtcNow(new DateTimeOffset(end.AddHours(48), TimeSpan.Zero));
        await s.Payments.RunDisputesAsync();
        await s.Payments.ReplyToDisputeAsync(request.Id, s.SupplierOrgId, SupplierWords);
        var disputeId = Assert.Single(s.Db.ServicePaymentDisputes.AsNoTracking()).Id;

        await s.Payments.ResolveDisputeAsync(disputeId, new ResolveServiceDisputeRequest("for_supplier", null, null), Admin);

        // The work was done and only not marked: the request is completed, so the release (which pays a completed request only) can go on.
        var completed = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Completato, completed.Status);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, completed.CompletedAt);
        Assert.Equal(ServiceRequestScenario.ServicePriceCents, completed.FinalAmountCents);
        await s.Payments.RunFundsAsync();
        Assert.Equal(Net, Assert.Single(s.Kit.PlatformGateway.TransferRequests).AmountCents);
        Assert.Equal(ServiceRequestStatus.Pagato, (await s.ReadAsync(request.Id)).Status);
        Assert.Empty(s.Kit.Penalties.Recorded);
    }

    [Fact]
    public async Task NoShow_DecidedForTheCustomer_GivesBackTheSuppliersPart_AndTheSupplierIsPenalizedAsANoShow()
    {
        var (s, request, _) = await BookedAndTakenAsync();
        using var scope = s;
        var end = (await s.ReadAsync(request.Id)).ScheduledEndUtc!.Value;
        s.Clock.SetUtcNow(new DateTimeOffset(end.AddHours(48), TimeSpan.Zero));
        await s.Payments.RunDisputesAsync();
        await s.Payments.ReplyToDisputeAsync(request.Id, s.SupplierOrgId, SupplierWords);
        var disputeId = Assert.Single(s.Db.ServicePaymentDisputes.AsNoTracking()).Id;

        await s.Payments.ResolveDisputeAsync(disputeId, new ResolveServiceDisputeRequest("for_customer", null, null), Admin);

        Assert.Equal(Net, Assert.Single(s.Kit.PlatformGateway.RefundRequests).AmountCents);
        Assert.Equal(ServicePaymentStatus.PartiallyRefunded, Assert.Single(await s.PaymentsOfAsync(request.Id)).Status);
        var penalty = Assert.Single(s.Kit.Penalties.Recorded);
        Assert.Equal(SupplierDisputePenaltyKind.NoShow, penalty.Kind);
        await s.Payments.RunFundsAsync();
        Assert.Empty(s.Kit.PlatformGateway.TransferRequests);
    }
}
