using System.Net;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Stripe;
using Xunit;
using static Casazen.Tests.Unit.Services.ServicePaymentFlows;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-15b: the jobs of the service payments. <c>service-payment-sync</c> reads the PaymentIntents of the payments Stripe is
/// processing and applies them as the webhook would; <c>service-payment-reminders</c> flags the late payments (7 days) and reminds
/// the payer at +2 and +7 days from the first request, three emails with a link at most, never twice in a day;
/// <c>SendPendingPaymentRequestsJob</c> sends the requests that waited for the supplier to be ready. Every payment is handled under
/// its lock and read again after it, so a second run, a retry or the supplier's own request never makes the payer receive a second
/// email. The feature flag stops what creates a request (the first requests and the reminders), never the money in flight.
/// </summary>
public class SupplierPaymentJobsTests
{
    // ─── service-payment-sync ───

    [Fact]
    public async Task Sync_APaymentInFlightThatSucceededOnStripe_IsRecordedAsPaid_AndTheSupplierGetsTheReceipt()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Processing)); // the success event is lost
        s.Gateway.SetStatus(opened.IntentId, "succeeded");
        s.ForgetNotifications();
        s.Clock.Advance(TimeSpan.FromMinutes(15));

        var run = await s.Payments.SynchronizeAsync();

        Assert.Equal(1, run.PaymentsRead);
        Assert.Equal(1, run.PaymentsUpdated);
        Assert.Equal(0, run.Errors);
        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Equal(ServicePaymentStatus.Paid, payment.Status);
        Assert.Equal(ServicePaymentChannel.Stripe, payment.PaidVia);
        Assert.Equal(ServiceRequestStatus.Pagato, (await s.ReadAsync(opened.Request.Id)).Status);
        Assert.Equal(ServiceRequestActorParty.Host, (await s.ReadAsync(opened.Request.Id)).PaidBy);
        Assert.Single(s.EmailsOf(EmailTemplates.Names.ServicePaymentReceived));
    }

    [Fact]
    public async Task Sync_TwiceInARow_IsTheSameAsOnce_AndTheSecondRunReadsNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Processing));
        s.Gateway.SetStatus(opened.IntentId, "succeeded");
        s.ForgetNotifications();

        await s.Payments.SynchronizeAsync();
        var second = await s.Payments.SynchronizeAsync();

        Assert.Equal(0, second.PaymentsRead);
        Assert.Single(s.EmailsOf(EmailTemplates.Names.ServicePaymentReceived));
    }

    [Fact]
    public async Task Sync_AfterTheWebhookAlreadyRecordedIt_ChangesNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Processing));
        await s.PayAsync(opened);
        s.ForgetNotifications();

        var run = await s.Payments.SynchronizeAsync();

        Assert.Equal(0, run.PaymentsRead); // Paid is not "in flight" any more
        Assert.Empty(s.Emails.PaymentEmails());
    }

    [Fact]
    public async Task Sync_AFailureStripeReports_MovesThePaymentToFailed_AndThePayerGetsANewLink()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Processing));
        s.Gateway.SetStatus(opened.IntentId, "requires_payment_method", lastErrorCode: "debit_not_authorized");
        s.ForgetNotifications();
        s.Clock.Advance(TimeSpan.FromMinutes(15));

        var run = await s.Payments.SynchronizeAsync();

        Assert.Equal(1, run.PaymentsUpdated);
        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Equal(ServicePaymentStatus.Failed, payment.Status);
        Assert.Equal("debit_not_authorized", payment.FailureCode);
        var email = Assert.Single(s.EmailsOf(EmailTemplates.Names.ServicePaymentFailed));
        Assert.Equal("host@test.com", email.To);
        Assert.NotEqual(opened.Token, ServicePaymentTestSupport.LinkOf(email.Content).Token);
    }

    [Fact]
    public async Task Sync_APaymentIntentCanceledOnStripe_ReopensThePayment()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Processing));
        s.Gateway.SetStatus(opened.IntentId, "canceled");

        await s.Payments.SynchronizeAsync();

        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Equal(ServicePaymentStatus.Requested, payment.Status);
        Assert.Null(payment.StripePaymentIntentId);
    }

    [Fact]
    public async Task Sync_APaymentIntentStillProcessing_ChangesNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Processing));
        s.Gateway.SetStatus(opened.IntentId, "processing");

        var run = await s.Payments.SynchronizeAsync();

        Assert.Equal(1, run.PaymentsRead);
        Assert.Equal(0, run.PaymentsUpdated);
        Assert.Equal(ServicePaymentStatus.Processing, (await s.ReloadAsync(opened.PaymentId)).Status);
    }

    [Fact]
    public async Task Sync_AMoneyThatDoesNotMatch_GoesToReview_NotToPaid()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.AddAdminAsync();
        var opened = await s.OpenedAsync();
        await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Processing));
        s.Gateway.UpdateIntent(opened.IntentId, i => i with { Status = "succeeded", AmountReceivedCents = 5_000 });
        s.ForgetNotifications();

        var run = await s.Payments.SynchronizeAsync();

        Assert.Equal(1, run.PaymentsUpdated);
        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Equal(ServicePaymentStatus.NeedsReview, payment.Status);
        Assert.Equal("review:amount", payment.FailureCode);
        Assert.Equal(ServiceRequestStatus.Completato, (await s.ReadAsync(opened.Request.Id)).Status);
        Assert.Single(s.EmailsOf(EmailTemplates.Names.ServicePaymentAdminAlert));
    }

    [Fact]
    public async Task Sync_APaymentIntentThatIsGoneWithItsAccount_NeedsReview_AndTheAdminsAreTold()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.AddAdminAsync();
        var opened = await s.OpenedAsync();
        await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Processing));
        s.Gateway.DeleteAccount(ServiceRequestScenario.SupplierAccountId);
        s.ForgetNotifications();

        var run = await s.Payments.SynchronizeAsync();

        Assert.Equal(0, run.Errors);
        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Equal(ServicePaymentStatus.NeedsReview, payment.Status);
        Assert.Equal("review:intent_gone", payment.FailureCode);
        Assert.Single(s.EmailsOf(EmailTemplates.Names.ServicePaymentAdminAlert));
    }

    [Fact]
    public async Task Sync_AnErrorReadingOnePayment_IsCounted_AndTheOthersGoOn()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var first = await s.OpenedAsync();
        var second = await s.OpenedAsync();
        foreach (var opened in new[] { first, second })
        {
            await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Processing));
            s.Gateway.SetStatus(opened.IntentId, "succeeded");
        }

        s.Gateway.FailNextGet(new StripeException(HttpStatusCode.InternalServerError, new StripeError { Type = "api_error" }, "down"));
        var run = await s.Payments.SynchronizeAsync();

        Assert.Equal(1, run.Errors);
        Assert.Equal(1, run.PaymentsUpdated);
        var statuses = new[] { (await s.ReloadAsync(first.PaymentId)).Status, (await s.ReloadAsync(second.PaymentId)).Status };
        Assert.Single(statuses, status => status == ServicePaymentStatus.Paid);
        Assert.Single(statuses, status => status == ServicePaymentStatus.Processing);
        // The next run reads the one that failed.
        var next = await s.Payments.SynchronizeAsync();
        Assert.Equal(1, next.PaymentsUpdated);
        Assert.Equal(ServicePaymentStatus.Paid, (await s.ReloadAsync(first.PaymentId)).Status);
        Assert.Equal(ServicePaymentStatus.Paid, (await s.ReloadAsync(second.PaymentId)).Status);
    }

    [Fact]
    public async Task Sync_WithTheFlagOff_StillFollowsTheMoneyInFlight()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        await s.Payments.ApplyPaymentIntentEventAsync(opened.EventOf(Processing));
        s.Gateway.SetStatus(opened.IntentId, "succeeded");
        s.Flags.Set(FeatureFlags.SupplierOnlinePayments, false);

        var run = await s.Payments.SynchronizeAsync();

        Assert.Equal(1, run.PaymentsUpdated);
        Assert.Equal(ServicePaymentStatus.Paid, (await s.ReloadAsync(opened.PaymentId)).Status);
    }

    // ─── service-payment-reminders ───

    private static async Task<(ServiceRequestScenario S, Guid RequestId, Guid PaymentId, string Token)> AskedAsync(ServiceRequestScenario? scenario = null)
    {
        var s = scenario ?? await ServiceRequestScenario.CreateAsync();
        var request = await s.CompletedOnlineAsync();
        var payment = await s.OnlyPaymentOfAsync(request.Id);
        var token = s.TokenOf(payment.Id);
        s.ForgetNotifications();
        return (s, request.Id, payment.Id, token);
    }

    private static List<(string? To, Casazen.Infrastructure.Email.EmailContent Content, string Template)> Reminders(ServiceRequestScenario s) =>
        s.EmailsOf(EmailTemplates.Names.ServicePaymentReminder);

    [Fact]
    public async Task Reminders_BeforeTheSecondDay_NothingIsSent()
    {
        var (s, _, paymentId, _) = await AskedAsync();
        using var _ = s;
        s.Clock.Advance(TimeSpan.FromDays(2).Subtract(TimeSpan.FromMinutes(1)));

        var run = await s.Payments.RunRemindersAsync();

        Assert.Equal(0, run.RemindersSent);
        Assert.Empty(Reminders(s));
        Assert.Equal(1, (await s.ReloadAsync(paymentId)).SentCount);
    }

    [Fact]
    public async Task Reminders_AtTheSecondDay_SendOneReminder_WithANewLink_TheOldOneStopsWorking()
    {
        var (s, _, paymentId, oldToken) = await AskedAsync();
        using var _ = s;
        s.Clock.Advance(TimeSpan.FromDays(2));

        var run = await s.Payments.RunRemindersAsync();

        Assert.True(run.EmailsEnabled);
        Assert.Equal(1, run.RemindersSent);
        Assert.Equal(0, run.Errors);
        var email = Assert.Single(Reminders(s));
        Assert.Equal("host@test.com", email.To);
        Assert.Contains("60,00 €", email.Content.HtmlBody);
        var link = ServicePaymentTestSupport.LinkOf(email.Content);
        Assert.Equal(paymentId, link.PaymentId);
        Assert.NotEqual(oldToken, link.Token);
        var payment = await s.ReloadAsync(paymentId);
        Assert.Equal(2, payment.SentCount);
        Assert.Equal(ServiceRequestScenario.Instant.UtcDateTime.AddDays(2), payment.LastSentAt);
        // The first link was replaced, as when the supplier reminds by hand; the new one pays.
        await Assert.ThrowsAsync<NotFoundException>(() => s.Payments.GetPublicAsync(paymentId, oldToken));
        Assert.Equal(PublicServicePaymentState.Payable, (await s.Payments.GetPublicAsync(paymentId, link.Token)).State);
        // Valid for 30 days from this email.
        Assert.Equal(payment.LastSentAt!.Value.AddDays(30), (await s.Payments.GetPublicAsync(paymentId, link.Token)).ValidUntil);
    }

    [Fact]
    public async Task Reminders_TheSameDayTwice_SendOneEmail()
    {
        var (s, _, _, _) = await AskedAsync();
        using var _ = s;
        s.Clock.Advance(TimeSpan.FromDays(2));

        var first = await s.Payments.RunRemindersAsync();
        var second = await s.Payments.RunRemindersAsync();
        s.Clock.Advance(TimeSpan.FromHours(20));
        var third = await s.Payments.RunRemindersAsync();

        Assert.Equal(1, first.RemindersSent);
        Assert.Equal(0, second.RemindersSent);
        Assert.Equal(0, third.RemindersSent);
        Assert.Single(Reminders(s));
    }

    [Fact]
    public async Task Reminders_AtTheSecondAndTheSeventhDay_ThenNeverAThird_ThreeEmailsInAll()
    {
        var (s, _, paymentId, _) = await AskedAsync();
        using var _ = s;

        s.Clock.Advance(TimeSpan.FromDays(2));
        await s.Payments.RunRemindersAsync(); // reminder 1
        s.Clock.Advance(TimeSpan.FromDays(3)); // day 5
        var dayFive = await s.Payments.RunRemindersAsync();
        s.Clock.Advance(TimeSpan.FromDays(2)); // day 7
        var daySeven = await s.Payments.RunRemindersAsync(); // reminder 2
        s.Clock.Advance(TimeSpan.FromDays(10)); // day 17
        var later = await s.Payments.RunRemindersAsync();
        s.Clock.Advance(TimeSpan.FromDays(10));
        var muchLater = await s.Payments.RunRemindersAsync();

        Assert.Equal(0, dayFive.RemindersSent);
        Assert.Equal(1, daySeven.RemindersSent);
        Assert.Equal(0, later.RemindersSent);
        Assert.Equal(0, muchLater.RemindersSent);
        Assert.Equal(2, Reminders(s).Count);
        Assert.Equal(ServicePaymentLimits.MaxPaymentEmails, (await s.ReloadAsync(paymentId)).SentCount);
    }

    [Fact]
    public async Task Reminders_AReminderTheSupplierSentByHand_CountsAsOne()
    {
        var (s, requestId, paymentId, _) = await AskedAsync();
        using var _ = s;
        s.Clock.Advance(TimeSpan.FromDays(1).Add(TimeSpan.FromHours(1)));
        await s.Service.RequestPaymentAsync(requestId, s.SupplierOrgId); // day 1: the supplier reminds
        s.ForgetNotifications();

        s.Clock.Advance(TimeSpan.FromDays(1)); // day 2 and one hour: the day of the first automatic reminder, already made up for
        var dayTwo = await s.Payments.RunRemindersAsync();
        s.Clock.Advance(TimeSpan.FromDays(5)); // day 7
        var daySeven = await s.Payments.RunRemindersAsync();

        Assert.Equal(0, dayTwo.RemindersSent);
        Assert.Equal(1, daySeven.RemindersSent);
        Assert.Single(Reminders(s));
        Assert.Equal(3, (await s.ReloadAsync(paymentId)).SentCount);
    }

    [Theory]
    [InlineData(ServicePaymentStatus.Paid)]
    [InlineData(ServicePaymentStatus.Processing)]
    [InlineData(ServicePaymentStatus.NeedsReview)]
    [InlineData(ServicePaymentStatus.Canceled)]
    [InlineData(ServicePaymentStatus.Refunded)]
    public async Task Reminders_ForAPaymentThatIsNotOwedAnyMore_NothingIsSent(ServicePaymentStatus status)
    {
        var (s, requestId, _, _) = await AskedAsync();
        using var _ = s;
        await SupplierPaymentRequestTests.ChangePaymentAsync(s, requestId, p =>
        {
            p.Status = status;
            if (status is ServicePaymentStatus.Paid or ServicePaymentStatus.Refunded)
            {
                p.PaidAt = ServiceRequestScenario.Instant.UtcDateTime;
                p.PaidVia = ServicePaymentChannel.Stripe;
            }
        });
        s.Clock.Advance(TimeSpan.FromDays(10));

        var run = await s.Payments.RunRemindersAsync();

        Assert.Equal(0, run.RemindersSent);
        Assert.Equal(0, run.MarkedLate);
        Assert.Empty(Reminders(s));
    }

    [Fact]
    public async Task Reminders_AFailedPayment_IsRemindedLikeAnOpenOne()
    {
        var (s, requestId, _, _) = await AskedAsync();
        using var _ = s;
        await SupplierPaymentRequestTests.ChangePaymentAsync(s, requestId, p => { p.Status = ServicePaymentStatus.Failed; p.FailureCode = "card_declined"; });
        s.Clock.Advance(TimeSpan.FromDays(3));

        var run = await s.Payments.RunRemindersAsync();

        Assert.Equal(1, run.RemindersSent);
    }

    [Fact]
    public async Task Reminders_TheSupplierCannotBePaidAnymore_NothingIsSent_TheLinkWouldLeadToAnUnavailablePage()
    {
        var (s, _, _, _) = await AskedAsync();
        using var _ = s;
        await s.ConnectSupplierAsync(ready: false);
        s.Clock.Advance(TimeSpan.FromDays(3));

        var run = await s.Payments.RunRemindersAsync();

        Assert.Equal(0, run.RemindersSent);
        Assert.Empty(Reminders(s));
    }

    [Fact]
    public async Task Reminders_ThePayerHasNoEmailAddress_NothingIsSent()
    {
        var (s, _, _, _) = await AskedAsync();
        using var _ = s;
        var host = await s.Db.Orgs.SingleAsync(o => o.Id == s.HostOrgId);
        host.ContactEmail = string.Empty;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        s.Clock.Advance(TimeSpan.FromDays(3));

        var run = await s.Payments.RunRemindersAsync();

        Assert.Equal(0, run.RemindersSent);
        Assert.Empty(Reminders(s));
    }

    [Fact]
    public async Task Reminders_TheRequestWasCanceledOrIsNotOnline_NothingIsSent()
    {
        var (s, requestId, _, _) = await AskedAsync();
        using var _ = s;
        await s.ChangeRequestAsync(requestId, r => r.PaymentMode = ServiceRequestPaymentMode.Manual);
        s.Clock.Advance(TimeSpan.FromDays(3));

        var run = await s.Payments.RunRemindersAsync();

        Assert.Equal(0, run.RemindersSent);
    }

    [Fact]
    public async Task Reminders_WithTheFlagOff_SendNothing_ButTheLatePaymentsAreStillFlagged()
    {
        var (s, _, paymentId, _) = await AskedAsync();
        using var _ = s;
        s.Flags.Set(FeatureFlags.SupplierOnlinePayments, false);
        s.Clock.Advance(TimeSpan.FromDays(8));

        var run = await s.Payments.RunRemindersAsync();

        Assert.False(run.EmailsEnabled);
        Assert.Equal(1, run.MarkedLate);
        Assert.Equal(0, run.RemindersSent);
        Assert.Equal(0, run.RequestsSent);
        Assert.Empty(s.Emails.PaymentEmails());
        Assert.NotNull((await s.ReloadAsync(paymentId)).LateAt);
    }

    [Fact]
    public async Task Reminders_AnEmailThatCannotBeQueued_TakesTheNewLinkBack_AndTheNextRunTriesAgain()
    {
        var (s, _, paymentId, oldToken) = await AskedAsync();
        using var _ = s;
        s.Clock.Advance(TimeSpan.FromDays(2));
        s.Emails.Accepting = false;

        var failed = await s.Payments.RunRemindersAsync();

        Assert.Equal(0, failed.RemindersSent);
        Assert.Equal(1, failed.Errors);
        var payment = await s.ReloadAsync(paymentId);
        Assert.Equal(1, payment.SentCount);
        Assert.Equal(PublicServicePaymentState.Payable, (await s.Payments.GetPublicAsync(paymentId, oldToken)).State);

        s.Emails.Accepting = true;
        var next = await s.Payments.RunRemindersAsync();

        Assert.Equal(1, next.RemindersSent);
        Assert.Single(Reminders(s));
    }

    // ─── Late ───

    [Fact]
    public async Task Late_FromTheSeventhDay_ThePaymentIsFlaggedOnce_AtTheMomentItBecameLate()
    {
        var (s, _, paymentId, _) = await AskedAsync();
        using var _ = s;
        var asked = (await s.ReloadAsync(paymentId)).RequestedAt!.Value;

        s.Clock.Advance(TimeSpan.FromDays(7).Subtract(TimeSpan.FromMinutes(1)));
        var early = await s.Payments.RunRemindersAsync();
        Assert.Equal(0, early.MarkedLate);
        Assert.Null((await s.ReloadAsync(paymentId)).LateAt);

        s.Clock.Advance(TimeSpan.FromMinutes(5));
        var late = await s.Payments.RunRemindersAsync();
        s.Clock.Advance(TimeSpan.FromDays(3));
        var again = await s.Payments.RunRemindersAsync();

        Assert.Equal(1, late.MarkedLate);
        Assert.Equal(0, again.MarkedLate);
        // The moment it became late, not the moment the job noticed: the same figure whenever the job runs.
        Assert.Equal(asked.AddDays(7), (await s.ReloadAsync(paymentId)).LateAt);
    }

    [Fact]
    public async Task Late_APaymentThatWasPaidBeforeTheJobRan_IsNotLate()
    {
        var (s, requestId, paymentId, _) = await AskedAsync();
        using var _ = s;
        await SupplierPaymentRequestTests.ChangePaymentAsync(s, requestId, p =>
        {
            p.Status = ServicePaymentStatus.Paid;
            p.PaidAt = ServiceRequestScenario.Instant.UtcDateTime.AddDays(1);
            p.PaidVia = ServicePaymentChannel.Stripe;
        });
        s.Clock.Advance(TimeSpan.FromDays(9));

        var run = await s.Payments.RunRemindersAsync();

        Assert.Equal(0, run.MarkedLate);
        Assert.Null((await s.ReloadAsync(paymentId)).LateAt);
    }

    [Fact]
    public async Task Late_TheDaysComeFromTheConfiguration()
    {
        using var scenario = await ServiceRequestScenario.CreateAsync(paymentOptions: new Core.Options.SupplierPaymentsOptions { CommissionPercent = 10m, LateAfterDays = 3 });
        var (s, _, paymentId, _) = await AskedAsync(scenario);
        s.Clock.Advance(TimeSpan.FromDays(3));

        var run = await s.Payments.RunRemindersAsync();

        Assert.Equal(1, run.MarkedLate);
        Assert.NotNull((await s.ReloadAsync(paymentId)).LateAt);
    }

    // ─── Pending requests: the supplier was not ready when the work was completed ───

    private static async Task<(ServiceRequestScenario S, Guid RequestId, Guid PaymentId)> PendingAsync(ServiceRequestScenario? scenario = null)
    {
        var s = scenario ?? await ServiceRequestScenario.CreateAsync();
        await s.EnablePaymentsAsync();
        var taken = await s.TakenAsync();
        // The Stripe account stops being ready between the take and the completion: the payment is created, with no link to send.
        await s.ConnectSupplierAsync(ready: false);
        var request = await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId);
        var payment = await s.OnlyPaymentOfAsync(request.Id);
        Assert.Null(payment.PaymentTokenHash);
        Assert.Equal(0, payment.SentCount);
        Assert.Empty(s.Emails.PaymentEmails());
        return (s, request.Id, payment.Id);
    }

    [Fact]
    public async Task SendPending_WhenTheSupplierBecomesReady_TheFirstRequestGoesOut_Once()
    {
        var (s, _, paymentId) = await PendingAsync();
        using var _ = s;
        await s.ConnectSupplierAsync(ready: true);

        var sent = await s.Payments.SendPendingRequestsAsync(s.SupplierOrgId);
        var again = await s.Payments.SendPendingRequestsAsync(s.SupplierOrgId);

        Assert.Equal(1, sent);
        Assert.Equal(0, again);
        var email = Assert.Single(s.EmailsOf(EmailTemplates.Names.ServicePaymentRequest));
        Assert.Equal("host@test.com", email.To);
        var link = ServicePaymentTestSupport.LinkOf(email.Content);
        Assert.Equal(paymentId, link.PaymentId);
        var payment = await s.ReloadAsync(paymentId);
        Assert.Equal(1, payment.SentCount);
        Assert.Equal(ServiceRequestScenario.Instant.UtcDateTime, payment.RequestedAt);
        Assert.NotNull(payment.PaymentTokenHash);
        Assert.Equal(PublicServicePaymentState.Payable, (await s.Payments.GetPublicAsync(paymentId, link.Token)).State);
    }

    [Fact]
    public async Task SendPending_TheSupplierIsStillNotReady_NothingIsSent()
    {
        var (s, _, paymentId) = await PendingAsync();
        using var _ = s;

        var sent = await s.Payments.SendPendingRequestsAsync(s.SupplierOrgId);

        Assert.Equal(0, sent);
        Assert.Empty(s.Emails.PaymentEmails());
        Assert.Null((await s.ReloadAsync(paymentId)).PaymentTokenHash);
    }

    [Fact]
    public async Task SendPending_WithTheFlagOff_NothingIsSent()
    {
        var (s, _, _) = await PendingAsync();
        using var _ = s;
        await s.ConnectSupplierAsync(ready: true);
        s.Flags.Set(FeatureFlags.SupplierOnlinePayments, false);

        Assert.Equal(0, await s.Payments.SendPendingRequestsAsync(s.SupplierOrgId));
        Assert.Empty(s.Emails.PaymentEmails());
    }

    [Fact]
    public async Task SendPending_OnlyTheSuppliersOwnPayments()
    {
        var (s, _, paymentId) = await PendingAsync();
        using var _ = s;
        await s.ConnectSupplierAsync(ready: true);

        var other = await s.Payments.SendPendingRequestsAsync(Guid.NewGuid());

        Assert.Equal(0, other);
        Assert.Null((await s.ReloadAsync(paymentId)).PaymentTokenHash);
    }

    [Fact]
    public async Task SendPending_ARequestTheSupplierAlreadySent_IsNotSentTwice()
    {
        var (s, requestId, _) = await PendingAsync();
        using var _ = s;
        await s.ConnectSupplierAsync(ready: true);
        await s.Service.RequestPaymentAsync(requestId, s.SupplierOrgId); // the supplier asks for it by hand first
        s.ForgetNotifications();

        var sent = await s.Payments.SendPendingRequestsAsync(s.SupplierOrgId);

        Assert.Equal(0, sent);
        Assert.Empty(s.Emails.PaymentEmails());
    }

    [Fact]
    public async Task SendPending_ThePayerHasNoEmailAddress_StaysPending()
    {
        var (s, _, paymentId) = await PendingAsync();
        using var _ = s;
        await s.ConnectSupplierAsync(ready: true);
        var host = await s.Db.Orgs.SingleAsync(o => o.Id == s.HostOrgId);
        host.ContactEmail = string.Empty;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();

        Assert.Equal(0, await s.Payments.SendPendingRequestsAsync(s.SupplierOrgId));
        Assert.Null((await s.ReloadAsync(paymentId)).PaymentTokenHash);
    }

    [Fact]
    public async Task SendPending_AnEmailThatCannotBeQueued_LeavesThePaymentPending_ForTheNextRun()
    {
        var (s, _, paymentId) = await PendingAsync();
        using var _ = s;
        await s.ConnectSupplierAsync(ready: true);
        s.Emails.Accepting = false;

        var failed = await s.Payments.SendPendingRequestsAsync(s.SupplierOrgId);

        Assert.Equal(0, failed);
        var payment = await s.ReloadAsync(paymentId);
        Assert.Null(payment.PaymentTokenHash);
        Assert.Equal(0, payment.SentCount);
        Assert.Null(payment.RequestedAt);

        s.Emails.Accepting = true;
        Assert.Equal(1, await s.Payments.SendPendingRequestsAsync(s.SupplierOrgId));
    }

    [Fact]
    public async Task Reminders_TheDailyRunAlsoSendsThePendingPayments_ForAJobThatWasNeverQueued()
    {
        var (s, _, paymentId) = await PendingAsync();
        using var _ = s;
        await s.ConnectSupplierAsync(ready: true); // the supplier became ready, but no account.updated queued the job

        var run = await s.Payments.RunRemindersAsync();

        Assert.Equal(1, run.RequestsSent);
        Assert.Equal(0, run.RemindersSent);
        Assert.Single(s.EmailsOf(EmailTemplates.Names.ServicePaymentRequest));
        Assert.Equal(1, (await s.ReloadAsync(paymentId)).SentCount);
    }

    [Fact]
    public async Task Reminders_ThePendingPaymentThatWasJustSent_IsNotRemindedTheSameRun()
    {
        var (s, _, _) = await PendingAsync();
        using var _ = s;
        await s.ConnectSupplierAsync(ready: true);
        s.Clock.Advance(TimeSpan.FromDays(9)); // long past the reminder days

        var run = await s.Payments.RunRemindersAsync();

        // Its first request goes out now: the reminder days count from it, not from when the work was completed.
        Assert.Equal(1, run.RequestsSent);
        Assert.Equal(0, run.RemindersSent);
        Assert.Empty(Reminders(s));
    }
}
