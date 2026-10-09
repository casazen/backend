using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Microsoft.Extensions.Logging;
using Xunit;
using static Casazen.Tests.Unit.Services.ServicePaymentFlows;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-15b, decision D2: what the Stripe webhook does to a service payment. A succeeded PaymentIntent makes the payment and the
/// request paid <b>only if</b> account, amount received, currency, commission and PaymentIntent are what was asked for; anything
/// else goes to <c>NeedsReview</c>, is logged as an error and tells the admins, and nothing is shown as paid. The events of one
/// payment arrive in any order and more than once: applying one twice changes nothing, a payment that is paid never goes back to a
/// payable state, and a stale event is ignored where it could undo a newer one. The races on PostgreSQL are in
/// <c>ServicePaymentsWebhookPostgresTests</c>.
/// </summary>
public class SupplierPaymentWebhookTests
{
    private static readonly Guid UnknownPayment = Guid.Parse("99999999-9999-9999-9999-999999999999");

    // ─── A succeeded payment ───

    [Fact]
    public async Task Succeeded_AsAskedFor_RecordsThePaymentAndTheRequestAsPaid()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        s.Gateway.SetStatus(opened.IntentId, "succeeded");

        var notices = await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Succeeded));

        var notice = Assert.Single(notices);
        Assert.Equal(ServicePaymentNoticeKind.Received, notice.Kind);
        Assert.Equal(opened.PaymentId, notice.PaymentId);
        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Equal(ServicePaymentStatus.Paid, payment.Status);
        Assert.Equal(ServiceRequestScenario.Instant.UtcDateTime, payment.PaidAt);
        Assert.Equal(ServicePaymentChannel.Stripe, payment.PaidVia);
        Assert.Null(payment.FailureCode);
        Assert.Equal(opened.IntentId, payment.StripePaymentIntentId);
        var request = await s.ReadAsync(opened.Request.Id);
        Assert.Equal(ServiceRequestStatus.Pagato, request.Status);
        Assert.Equal(ServiceRequestScenario.Instant.UtcDateTime, request.PaidAt);
        Assert.Equal(ServiceRequestActorParty.Host, request.PaidBy);
        Assert.Equal(ServiceRequestPaymentMode.Online, request.PaymentMode);
    }

    [Fact]
    public async Task Succeeded_ThePaidAtIsWhenStripeSaysItHappened_NeverInTheFuture()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        var earlier = ServiceRequestScenario.Instant.UtcDateTime.AddMinutes(-20);

        await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Succeeded, at: earlier));

        Assert.Equal(earlier, (await s.ReloadAsync(opened.PaymentId)).PaidAt);

        using var other = await ServiceRequestScenario.CreateAsync();
        var second = await other.OpenedAsync();
        await other.Payments.ApplyPaymentIntentEventAsync(second.EventOf(Succeeded, at: ServiceRequestScenario.Instant.UtcDateTime.AddHours(2)));

        Assert.Equal(ServiceRequestScenario.Instant.UtcDateTime, (await other.ReloadAsync(second.PaymentId)).PaidAt);
    }

    [Fact]
    public async Task Succeeded_TheSupplierGetsTheReceipt_WithGrossCommissionAndNet()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        s.ForgetNotifications();

        await s.PayAsync(opened);

        var receipt = Assert.Single(s.EmailsOf(EmailTemplates.Names.ServicePaymentReceived));
        Assert.Equal("supplier@test.com", receipt.To);
        Assert.Contains("60,00 €", receipt.Content.HtmlBody);
        Assert.Contains("Commissione CasaZen (10 %)", receipt.Content.HtmlBody);
        Assert.Contains("6,00 €", receipt.Content.HtmlBody);
        Assert.Contains("54,00 €", receipt.Content.HtmlBody);
        // Nobody else is told: the payer paid the price shown, the admins have nothing to look at.
        Assert.Single(s.Emails.PaymentEmails());
    }

    [Fact]
    public async Task Succeeded_ARepeatedEvent_RecordsOnce_AndSendsOneReceipt()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        s.Gateway.SetStatus(opened.IntentId, "succeeded");
        s.ForgetNotifications();

        var first = await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Succeeded));
        s.Clock.Advance(TimeSpan.FromMinutes(5));
        var again = await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Succeeded));
        await s.Payments.CompleteAsync(first);
        await s.Payments.CompleteAsync(again);

        Assert.Single(first);
        Assert.Empty(again);
        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Equal(ServiceRequestScenario.Instant.UtcDateTime, payment.PaidAt);
        Assert.Single(s.EmailsOf(EmailTemplates.Names.ServicePaymentReceived));
    }

    [Fact]
    public async Task Succeeded_ThePaymentThatWasInFlight_IsPaid()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        s.Gateway.SetStatus(opened.IntentId, "processing");
        await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Processing));
        Assert.Equal(ServicePaymentStatus.Processing, (await s.ReloadAsync(opened.PaymentId)).Status);

        s.Gateway.SetStatus(opened.IntentId, "succeeded");
        var notices = await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Succeeded));

        Assert.Equal(ServicePaymentNoticeKind.Received, Assert.Single(notices).Kind);
        Assert.Equal(ServicePaymentStatus.Paid, (await s.ReloadAsync(opened.PaymentId)).Status);
    }

    [Fact]
    public async Task Succeeded_APaymentWithoutCommission_IsPaidWithNoFeeOnTheIntent()
    {
        using var s = await ServiceRequestScenario.CreateAsync(paymentOptions: new Core.Options.SupplierPaymentsOptions { CommissionPercent = 0m });
        var opened = await s.OpenedAsync();
        Assert.Null(opened.Intent.ApplicationFeeCents);

        var notices = await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Succeeded));

        Assert.Equal(ServicePaymentNoticeKind.Received, Assert.Single(notices).Kind);
        Assert.Equal(ServicePaymentStatus.Paid, (await s.ReloadAsync(opened.PaymentId)).Status);
    }

    [Fact]
    public async Task Succeeded_WhileTheFlagIsOff_IsStillApplied_MoneyInFlightIsNeverDropped()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        s.Flags.Set(Core.Features.FeatureFlags.SupplierOnlinePayments, false);

        var notices = await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Succeeded));

        Assert.Equal(ServicePaymentNoticeKind.Received, Assert.Single(notices).Kind);
        Assert.Equal(ServiceRequestStatus.Pagato, (await s.ReadAsync(opened.Request.Id)).Status);
    }

    // ─── Money that is not what was asked for: never "Pagato" ───

    public static TheoryData<string, string> Mismatches => new()
    {
        { "account", "review:account" },
        { "amount", "review:amount" },
        { "received", "review:amount" },
        { "currency", "review:currency" },
        { "fee", "review:fee" },
        { "nofee", "review:fee" },
        { "intent", "review:intent" },
        { "platform", "review:account" },
    };

    private static Func<ServicePaymentIntentEvent, ServicePaymentIntentEvent> Differs(string what) => what switch
    {
        "account" => e => e with { AccountId = "acct_somebody_else" },
        "amount" => e => e with { AmountCents = e.AmountCents - 100 },
        "received" => e => e with { AmountReceivedCents = e.AmountReceivedCents - 1 },
        "currency" => e => e with { Currency = "usd" },
        "fee" => e => e with { ApplicationFeeCents = 700 },
        "nofee" => e => e with { ApplicationFeeCents = null },
        "intent" => e => e with { PaymentIntentId = "pi_not_ours" },
        "platform" => e => e with { AccountId = null },
        _ => throw new ArgumentOutOfRangeException(nameof(what)),
    };

    [Theory]
    [MemberData(nameof(Mismatches))]
    public async Task Succeeded_ThatDiffersFromWhatWasAskedFor_NeedsReview_AndNothingIsShownAsPaid(string what, string code)
    {
        var logger = new CapturingLogger<SupplierPaymentService>();
        using var s = await ServiceRequestScenario.CreateAsync(paymentLogger: logger);
        var opened = await s.OpenedAsync();

        var notices = await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Succeeded, Differs(what)));

        var notice = Assert.Single(notices);
        Assert.Equal(ServicePaymentNoticeKind.NeedsReview, notice.Kind);
        Assert.Equal(code, notice.Detail);
        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Equal(ServicePaymentStatus.NeedsReview, payment.Status);
        Assert.Equal(code, payment.FailureCode);
        Assert.Null(payment.PaidAt);
        Assert.Null(payment.PaidVia);
        var request = await s.ReadAsync(opened.Request.Id);
        Assert.Equal(ServiceRequestStatus.Completato, request.Status);
        Assert.Null(request.PaidAt);
        Assert.Null(request.PaidBy);
        // An error in the log: somebody has to look at money that arrived in a way nobody planned.
        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("NOT recorded as paid", error.Message);
        Assert.Contains(opened.PaymentId.ToString(), error.Message);
    }

    [Fact]
    public async Task Succeeded_ThatDiffersInSeveralWays_NamesEveryOne()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();

        await s.Payments.ApplyPaymentIntentEventAsync(
            opened.EventOf(Succeeded, e => e with { AccountId = "acct_other", ApplicationFeeCents = 1, Currency = "gbp" }));

        Assert.Equal("review:account+currency+fee", (await s.ReloadAsync(opened.PaymentId)).FailureCode);
    }

    [Fact]
    public async Task NeedsReview_TheAdminsAreToldByEmail_WithIdsAndTheCodeOnly()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.AddAdminAsync("admin@test.com");
        await s.AddAdminAsync("second-admin@test.com");
        await s.AddAdminAsync("gone@test.com", active: false);
        var opened = await s.OpenedAsync();
        s.ForgetNotifications();

        var notices = await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Succeeded, Differs("amount")));
        await s.Payments.CompleteAsync(notices);

        var alerts = s.EmailsOf(EmailTemplates.Names.ServicePaymentAdminAlert);
        Assert.Equal(["admin@test.com", "second-admin@test.com"], alerts.Select(a => a.To!).Order().ToArray());
        var body = alerts[0].Content.HtmlBody;
        Assert.Contains(opened.PaymentId.ToString("D"), body);
        Assert.Contains(opened.Request.Id.ToString("D"), body);
        Assert.Contains("review:amount", body);
        Assert.Contains("Supplier Org", body);
        // No personal data: the payer's address, the host's phone, the property are not in the alert.
        Assert.DoesNotContain("host@test.com", body);
        Assert.DoesNotContain(ServiceRequestScenario.PropertyAddress, body);
        Assert.DoesNotContain("+39 333 0001111", body);
        // The payer and the supplier are not told that something is wrong with their payment.
        Assert.Empty(s.EmailsOf(EmailTemplates.Names.ServicePaymentReceived));
    }

    [Fact]
    public async Task NeedsReview_NoAdminToTell_IsLoggedAsAnError_AndNothingIsThrown()
    {
        var logger = new CapturingLogger<SupplierPaymentService>();
        using var s = await ServiceRequestScenario.CreateAsync(paymentLogger: logger);
        var opened = await s.OpenedAsync();

        var notices = await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Succeeded, Differs("fee")));
        await s.Payments.CompleteAsync(notices);

        Assert.Empty(s.EmailsOf(EmailTemplates.Names.ServicePaymentAdminAlert));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("No active platform admin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NeedsReview_ThePayerCannotPayAgain_AndANewSucceededDoesNotChangeIt()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Succeeded, Differs("account")));

        var page = await s.Payments.GetPublicAsync(opened.PaymentId, opened.Token);
        var session = await Assert.ThrowsAsync<DomainConflictException>(() => s.Payments.CreatePublicSessionAsync(opened.PaymentId, opened.Token));
        var again = await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Succeeded));

        Assert.Equal(PublicServicePaymentState.Processing, page.State);
        Assert.Equal(ServicePaymentErrors.InFlight, session.Code);
        Assert.Empty(again);
        Assert.Equal(ServicePaymentStatus.NeedsReview, (await s.ReloadAsync(opened.PaymentId)).Status);
        Assert.Equal(ServiceRequestStatus.Completato, (await s.ReadAsync(opened.Request.Id)).Status);
    }

    [Fact]
    public async Task Succeeded_ForAPaymentTheSupplierWithdrew_IsNotRecorded_AndTheAdminsAreToldToRefundIt()
    {
        var logger = new CapturingLogger<SupplierPaymentService>();
        using var s = await ServiceRequestScenario.CreateAsync(paymentLogger: logger);
        await s.AddAdminAsync();
        var opened = await s.OpenedAsync();
        // The supplier recorded the payment as received outside CasaZen: the payment is withdrawn and its PaymentIntent canceled.
        await s.Service.RecordOfflinePaymentAsync(opened.Request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, "Pagato a mano");
        s.ForgetNotifications();

        // A charge that raced the cancellation still succeeded on Stripe.
        s.Gateway.SetStatus(opened.IntentId, "succeeded");
        var notices = await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Succeeded));
        await s.Payments.CompleteAsync(notices);

        var notice = Assert.Single(notices);
        Assert.Equal(ServicePaymentNoticeKind.NeedsReview, notice.Kind);
        Assert.Equal("payment_withdrawn", notice.Detail);
        var payments = await s.PaymentsOfAsync(opened.Request.Id);
        Assert.Equal(ServicePaymentStatus.Canceled, payments.Single(p => p.Id == opened.PaymentId).Status);
        Assert.Single(payments, p => p.Status == ServicePaymentStatus.Paid && p.PaidVia == ServicePaymentChannel.Offline);
        Assert.Equal(ServiceRequestStatus.Pagato, (await s.ReadAsync(opened.Request.Id)).Status);
        Assert.Equal(ServiceRequestActorParty.Supplier, (await s.ReadAsync(opened.Request.Id)).PaidBy);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("was withdrawn", StringComparison.Ordinal));
        Assert.Single(s.EmailsOf(EmailTemplates.Names.ServicePaymentAdminAlert));
    }

    [Fact]
    public async Task Succeeded_OfAnotherPaymentIntentForAPaymentThatIsPaid_ChangesNothing_AndIsLoggedAsAnError()
    {
        var logger = new CapturingLogger<SupplierPaymentService>();
        using var s = await ServiceRequestScenario.CreateAsync(paymentLogger: logger);
        var opened = await s.OpenedAsync();
        await s.PayAsync(opened);
        s.ForgetNotifications();

        // The payer, with an old tab, paid twice: a second PaymentIntent that names the same payment.
        var notices = await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Succeeded, e => e with { PaymentIntentId = "pi_second_charge" }));

        Assert.Empty(notices);
        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Equal(ServicePaymentStatus.Paid, payment.Status);
        Assert.Equal(opened.IntentId, payment.StripePaymentIntentId);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("refund one of the two charges", StringComparison.Ordinal));
        Assert.Empty(s.EmailsOf(EmailTemplates.Names.ServicePaymentReceived));
    }

    [Fact]
    public async Task Event_ForAPaymentIntentNoPaymentKnows_IsIgnored_NeverAnError()
    {
        var logger = new CapturingLogger<SupplierPaymentService>();
        using var s = await ServiceRequestScenario.CreateAsync(paymentLogger: logger);
        var opened = await s.OpenedAsync();

        foreach (var type in new[] { Succeeded, Processing, PaymentFailed, Canceled })
        {
            var notices = await s.Payments.ApplyPaymentIntentEventAsync(
                opened.EventOf(type, e => e with { PaymentIntentId = "pi_unknown", PaymentId = null }));
            Assert.Empty(notices);
        }

        Assert.Equal(ServicePaymentStatus.Requested, (await s.ReloadAsync(opened.PaymentId)).Status);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Event_WhoseMetadataNamesAPaymentThatDoesNotExist_IsIgnored()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();

        var notices = await s.Payments.ApplyPaymentIntentEventAsync(
            opened.EventOf(Succeeded, e => e with { PaymentIntentId = "pi_unknown", PaymentId = UnknownPayment }));

        Assert.Empty(notices);
    }

    [Fact]
    public async Task Event_ThatOnlyNamesThePaymentInItsMetadata_IsNotTrustedAsTheCurrentIntent()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();

        // processing, a failure and a cancellation of a PaymentIntent the payment does not hold change nothing…
        foreach (var type in new[] { Processing, PaymentFailed, Canceled })
            Assert.Empty(await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(type, e => e with { PaymentIntentId = "pi_foreign" })));

        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Equal(ServicePaymentStatus.Requested, payment.Status);
        Assert.Equal(opened.IntentId, payment.StripePaymentIntentId);
    }

    // ─── Any order, any number of times ───

    [Theory]
    [InlineData("payment_intent.processing")]
    [InlineData("payment_intent.payment_failed")]
    [InlineData("payment_intent.canceled")]
    public async Task APaymentThatIsPaid_NeverGoesBack_WhateverArrivesAfterTheSuccess(string lateEvent)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        await s.PayAsync(opened);
        s.Clock.Advance(TimeSpan.FromHours(1));

        var notices = await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(lateEvent));

        Assert.Empty(notices);
        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Equal(ServicePaymentStatus.Paid, payment.Status);
        Assert.Equal(opened.IntentId, payment.StripePaymentIntentId);
        Assert.Equal(ServiceRequestStatus.Pagato, (await s.ReadAsync(opened.Request.Id)).Status);
    }

    [Fact]
    public async Task Processing_MovesARequestedPaymentToProcessing_AndThePayerCannotStartAnother()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();

        await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Processing));

        Assert.Equal(ServicePaymentStatus.Processing, (await s.ReloadAsync(opened.PaymentId)).Status);
        Assert.Equal(PublicServicePaymentState.Processing, (await s.Payments.GetPublicAsync(opened.PaymentId, opened.Token)).State);
        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => s.Payments.CreatePublicSessionAsync(opened.PaymentId, opened.Token));
        Assert.Equal(ServicePaymentErrors.InFlight, ex.Code);
    }

    [Fact]
    public async Task Processing_Twice_IsTheSameAsOnce()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();

        await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Processing));
        var firstUpdate = (await s.ReloadAsync(opened.PaymentId)).UpdatedAt;
        s.Clock.Advance(TimeSpan.FromMinutes(3));
        await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Processing));

        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Equal(ServicePaymentStatus.Processing, payment.Status);
        Assert.Equal(firstUpdate, payment.UpdatedAt);
    }

    [Fact]
    public async Task PaymentFailed_OnTheSession_IsShownToThePayer_AndTheSameLinkWorksAgain()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        s.ForgetNotifications();

        var notices = await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(PaymentFailed));
        await s.Payments.CompleteAsync(notices);

        // The card was declined on the page the payer is looking at: the page says so, and nothing is emailed.
        Assert.Empty(notices);
        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Equal(ServicePaymentStatus.Failed, payment.Status);
        Assert.Equal("card_declined", payment.FailureCode);
        var page = await s.Payments.GetPublicAsync(opened.PaymentId, opened.Token);
        Assert.Equal(PublicServicePaymentState.Payable, page.State);
        Assert.True(page.LastAttemptFailed);
        Assert.Empty(s.Emails.PaymentEmails());
        // The PaymentIntent is reused: Stripe lets the payer try another card on it.
        var session = await s.Payments.CreatePublicSessionAsync(opened.PaymentId, opened.Token);
        Assert.Equal(opened.Intent.ClientSecret, session.ClientSecret);
    }

    [Fact]
    public async Task PaymentFailed_AfterItWasAccepted_GetsTheNewLinkByEmail_AndTheOldLinkStopsWorking()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Processing));
        s.ForgetNotifications();
        s.Clock.Advance(TimeSpan.FromDays(3));

        // A SEPA debit that came back.
        s.Gateway.SetStatus(opened.IntentId, "requires_payment_method", lastErrorCode: "debit_not_authorized");
        var notices = await s.Payments.ApplyPaymentIntentEventAsync(
            opened.EventOf(PaymentFailed, e => e with { FailureCode = "debit_not_authorized" }, at: ServiceRequestScenario.Instant.UtcDateTime.AddDays(3)));
        await s.Payments.CompleteAsync(notices);

        Assert.Equal(ServicePaymentNoticeKind.FailedInFlight, Assert.Single(notices).Kind);
        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Equal(ServicePaymentStatus.Failed, payment.Status);
        Assert.Equal("debit_not_authorized", payment.FailureCode);
        var email = Assert.Single(s.EmailsOf(EmailTemplates.Names.ServicePaymentFailed));
        Assert.Equal("host@test.com", email.To);
        Assert.Contains("non è andato a buon fine", email.Content.HtmlBody);
        Assert.Contains("60,00 €", email.Content.HtmlBody);
        Assert.DoesNotMatch(@"\d\s*%", email.Content.HtmlBody); // the payer is never told about the commission
        var newLink = ServicePaymentTestSupport.LinkOf(email.Content);
        Assert.Equal(opened.PaymentId, newLink.PaymentId);
        Assert.NotEqual(opened.Token, newLink.Token);
        await Assert.ThrowsAsync<NotFoundException>(() => s.Payments.GetPublicAsync(opened.PaymentId, opened.Token));
        var page = await s.Payments.GetPublicAsync(opened.PaymentId, newLink.Token);
        Assert.Equal(PublicServicePaymentState.Payable, page.State);
        Assert.True(page.LastAttemptFailed);
    }

    [Fact]
    public async Task PaymentFailed_AfterItWasAccepted_TheEmailThatCannotBeQueued_TakesTheNewLinkBack()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Processing));
        s.Clock.Advance(TimeSpan.FromHours(2));
        var notices = await s.Payments.ApplyPaymentIntentEventAsync(
            opened.EventOf(PaymentFailed, at: ServiceRequestScenario.Instant.UtcDateTime.AddHours(2)));
        s.Emails.Accepting = false;

        await s.Payments.CompleteAsync(notices);

        // Nothing was sent, so the link that was issued is not left around: the payer's previous link works as before.
        Assert.Empty(s.EmailsOf(EmailTemplates.Names.ServicePaymentFailed));
        Assert.Equal(PublicServicePaymentState.Payable, (await s.Payments.GetPublicAsync(opened.PaymentId, opened.Token)).State);
    }

    [Fact]
    public async Task PaymentFailed_ThatIsOlderThanTheProcessingTheNextAttemptStarted_IsStale_AndIgnored()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        var firstAttemptFailedAt = ServiceRequestScenario.Instant.UtcDateTime.AddMinutes(-10);
        s.Clock.Advance(TimeSpan.FromMinutes(5));

        // The payer retried with SEPA: the processing is recorded now. The failure of the first attempt arrives late.
        await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Processing, at: ServiceRequestScenario.Instant.UtcDateTime.AddMinutes(4)));
        var notices = await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(PaymentFailed, at: firstAttemptFailedAt));

        Assert.Empty(notices);
        Assert.Equal(ServicePaymentStatus.Processing, (await s.ReloadAsync(opened.PaymentId)).Status);
        s.ForgetNotifications();
        Assert.Empty(s.Emails.PaymentEmails());
    }

    [Fact]
    public async Task Processing_ThatIsOlderThanTheFailureTheNextAttemptHad_IsStale_AndIgnored()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        var processingAt = ServiceRequestScenario.Instant.UtcDateTime.AddMinutes(-10);
        s.Clock.Advance(TimeSpan.FromMinutes(5));
        await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(PaymentFailed, at: ServiceRequestScenario.Instant.UtcDateTime.AddMinutes(4)));

        await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Processing, at: processingAt));

        Assert.Equal(ServicePaymentStatus.Failed, (await s.ReloadAsync(opened.PaymentId)).Status);
    }

    [Fact]
    public async Task Canceled_TheCurrentPaymentIntent_ReopensThePayment_AndTheNextSessionMakesANewOne()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        s.Gateway.SetStatus(opened.IntentId, "canceled");

        await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Canceled));

        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Null(payment.StripePaymentIntentId);
        Assert.Equal(ServicePaymentStatus.Requested, payment.Status);
        await s.Payments.CreatePublicSessionAsync(opened.PaymentId, opened.Token);
        var renewed = await s.ReloadAsync(opened.PaymentId);
        Assert.NotNull(renewed.StripePaymentIntentId);
        Assert.NotEqual(opened.IntentId, renewed.StripePaymentIntentId);
        Assert.Equal(2, renewed.PaymentIntentCount);
    }

    [Fact]
    public async Task Canceled_AFailedPayment_StaysFailed_ButCanBeStartedAgain()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(PaymentFailed));
        s.Gateway.SetStatus(opened.IntentId, "canceled");

        await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Canceled));

        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Null(payment.StripePaymentIntentId);
        Assert.Equal(ServicePaymentStatus.Failed, payment.Status);
    }

    [Fact]
    public async Task Canceled_ByCasaZenItself_WhenTheSupplierChangedAccount_IsNotTheCurrentIntentAnyMore()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        // The supplier replaced its Stripe account: the next session cancels the old PaymentIntent and makes a new one.
        await s.ConnectSupplierAsync(ready: true, accountId: "acct_new_account");
        await s.Payments.CreatePublicSessionAsync(opened.PaymentId, opened.Token);
        var current = await s.ReloadAsync(opened.PaymentId);
        Assert.NotEqual(opened.IntentId, current.StripePaymentIntentId);

        // The event of the old one's cancellation arrives afterwards.
        var notices = await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Canceled));

        Assert.Empty(notices);
        var after = await s.ReloadAsync(opened.PaymentId);
        Assert.Equal(current.StripePaymentIntentId, after.StripePaymentIntentId);
        Assert.Equal("acct_new_account", after.ConnectedAccountId);
    }

    // ─── Disputes ───

    [Fact]
    public async Task Dispute_OnAServicePayment_IsLoggedAsAnError_AndTheAdminsAreTold_NothingElseChanges()
    {
        var logger = new CapturingLogger<SupplierPaymentService>();
        using var s = await ServiceRequestScenario.CreateAsync(paymentLogger: logger);
        await s.AddAdminAsync();
        var opened = await s.OpenedAsync();
        await s.PayAsync(opened);
        s.ForgetNotifications();

        var result = await s.Payments.ApplyDisputeCreatedAsync(
            new ServiceDisputeEvent("evt_dp", "dp_123", opened.IntentId, opened.Intent.ConnectedAccountId, 6_000, "eur", "fraudulent", "needs_response"));
        await s.Payments.CompleteAsync(result.Notices);

        Assert.True(result.Handled);
        var notice = Assert.Single(result.Notices);
        Assert.Equal(ServicePaymentNoticeKind.Disputed, notice.Kind);
        Assert.Contains("dp_123", notice.Detail);
        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("dp_123", error.Message);
        Assert.Contains("fraudulent", error.Message);
        var alert = Assert.Single(s.EmailsOf(EmailTemplates.Names.ServicePaymentAdminAlert));
        Assert.Contains("dp_123", alert.Content.HtmlBody);
        Assert.Contains("contestato", alert.Content.HtmlBody);
        Assert.Equal(ServicePaymentStatus.Paid, (await s.ReloadAsync(opened.PaymentId)).Status);
        Assert.Equal(ServiceRequestStatus.Pagato, (await s.ReadAsync(opened.Request.Id)).Status);
    }

    [Fact]
    public async Task Dispute_OnAnotherPaymentIntent_IsNotOurs()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.OpenedAsync();

        var unknown = await s.Payments.ApplyDisputeCreatedAsync(new ServiceDisputeEvent("evt", "dp_1", "pi_booking", "acct_host", 100, "eur", null, null));
        var none = await s.Payments.ApplyDisputeCreatedAsync(new ServiceDisputeEvent("evt", "dp_2", null, "acct_host", 100, "eur", null, null));

        Assert.False(unknown.Handled);
        Assert.False(none.Handled);
        Assert.Empty(unknown.Notices);
    }

    // ─── The notices of a payment that is gone ───

    [Fact]
    public async Task CompleteAsync_ANoticeOfAPaymentThatDoesNotExist_SendsNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        s.ForgetNotifications();

        await s.Payments.CompleteAsync([
            new ServicePaymentNotice(ServicePaymentNoticeKind.Received, UnknownPayment),
            new ServicePaymentNotice(ServicePaymentNoticeKind.NeedsReview, UnknownPayment, Detail: "review:fee"),
        ]);

        Assert.Empty(s.Emails.PaymentEmails());
    }
}
