using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;
using Xunit;
using static Casazen.Tests.Unit.Services.ServicePaymentFlows;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// SP-15b: <c>StripeWebhookHandler</c> routes the events of a supplier's service payment (<c>metadata.kind = service-charge</c>) to
/// the service payments <b>before</b> its generic <c>payment_intent.succeeded</c> case, inside the event transaction: the claim of
/// the event id and the update commit together, a duplicate is skipped, a failure releases the claim, and the emails follow the
/// commit. Refunds and disputes of a service payment are theirs; those of anything else go on as before. <c>account.updated</c> of a
/// supplier that has just become able to take payments queues its pending payment requests after the commit.
/// </summary>
public class SupplierPaymentWebhookHandlerTests
{
    private static StripeWebhookHandler Handler(
        ServiceRequestScenario s,
        IConnectOnboardingService? connect = null,
        IPaymentRefundService? bookingRefunds = null,
        ISupplierPaymentWebhookService? servicePayments = null) =>
        new(
            new PaymentRepository(s.Db),
            new BookingRepository(s.Db),
            connect ?? Mock.Of<IConnectOnboardingService>(),
            s.Db,
            Mock.Of<IStripeBillingService>(),
            Mock.Of<IEntitlementService>(),
            Mock.Of<IPlatformInvoiceService>(),
            Mock.Of<IRentBillingService>(),
            bookingRefunds ?? Mock.Of<IPaymentRefundService>(),
            TestCheckoutPaymentSettlement.Create(s.Db),
            TestDeferredCharges.Create(s.Db),
            servicePayments ?? s.Payments,
            NullLogger<StripeWebhookHandler>.Instance);

    private static Event PaymentIntentEvent(
        OpenedPayment opened,
        string type,
        string eventId,
        string? account = ServiceRequestScenario.SupplierAccountId,
        Action<PaymentIntent>? change = null)
    {
        var intent = new PaymentIntent
        {
            Id = opened.IntentId,
            Amount = opened.Intent.AmountCents,
            AmountReceived = opened.Intent.AmountCents,
            Currency = opened.Intent.Currency,
            ApplicationFeeAmount = opened.Intent.ApplicationFeeCents,
            Metadata = new Dictionary<string, string>
            {
                ["kind"] = ServiceCharges.Kind,
                [ServiceCharges.PaymentMetadataKey] = opened.PaymentId.ToString(),
            },
        };
        change?.Invoke(intent);
        return new Event
        {
            Id = eventId,
            Type = type,
            Account = account,
            Created = ServiceRequestScenario.Instant.UtcDateTime,
            Data = new EventData { Object = intent },
        };
    }

    // ─── The payment of a service: before the generic case ───

    [Fact]
    public async Task ServiceCharge_OnTheConnectEndpoint_IsRecordedAsPaid_AndNeverReachesTheBookingSettlement()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        s.Gateway.SetStatus(opened.IntentId, "succeeded");
        s.ForgetNotifications();

        await Handler(s).HandleEventAsync(PaymentIntentEvent(opened, "payment_intent.succeeded", "evt_sc_1"), WebhookSource.Connected);

        Assert.Equal(ServicePaymentStatus.Paid, (await s.ReloadAsync(opened.PaymentId)).Status);
        Assert.Equal(ServiceRequestStatus.Pagato, (await s.ReadAsync(opened.Request.Id)).Status);
        Assert.True(await s.Db.ProcessedStripeEvents.AnyAsync(e => e.EventId == "evt_sc_1"));
        // The generic handler never saw it: no booking payment was touched or refunded, and the receipt follows the commit.
        Assert.Empty(await s.Db.Payments.ToListAsync());
        Assert.Empty(await s.Db.PaymentRefunds.ToListAsync());
        Assert.Single(s.EmailsOf(EmailTemplates.Names.ServicePaymentReceived));
    }

    [Theory]
    [InlineData("payment_intent.processing", ServicePaymentStatus.Processing)]
    [InlineData("payment_intent.payment_failed", ServicePaymentStatus.Failed)]
    [InlineData("payment_intent.canceled", ServicePaymentStatus.Requested)]
    public async Task ServiceCharge_TheOtherEvents_AreAppliedToo(string type, ServicePaymentStatus expected)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();

        await Handler(s).HandleEventAsync(
            PaymentIntentEvent(opened, type, "evt_sc_2", change: pi => pi.LastPaymentError = new StripeError { Code = "card_declined" }),
            WebhookSource.Connected);

        Assert.Equal(expected, (await s.ReloadAsync(opened.PaymentId)).Status);
    }

    [Fact]
    public async Task ServiceCharge_TheSameEventDeliveredTwice_IsSkipped_AndSendsOneReceipt()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        var delivery = PaymentIntentEvent(opened, "payment_intent.succeeded", "evt_sc_dup");
        s.ForgetNotifications();

        await Handler(s).HandleEventAsync(delivery, WebhookSource.Connected);
        s.Clock.Advance(TimeSpan.FromMinutes(5));
        await Handler(s).HandleEventAsync(delivery, WebhookSource.Connected);

        Assert.Equal(1, await s.Db.ProcessedStripeEvents.CountAsync(e => e.EventId == "evt_sc_dup"));
        Assert.Single(s.EmailsOf(EmailTemplates.Names.ServicePaymentReceived));
        Assert.Equal(ServiceRequestScenario.Instant.UtcDateTime, (await s.ReloadAsync(opened.PaymentId)).PaidAt);
    }

    [Fact]
    public async Task ServiceCharge_ADifferentEventThatSaysTheSame_ChangesNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        s.ForgetNotifications();

        await Handler(s).HandleEventAsync(PaymentIntentEvent(opened, "payment_intent.succeeded", "evt_a"), WebhookSource.Connected);
        await Handler(s).HandleEventAsync(PaymentIntentEvent(opened, "payment_intent.succeeded", "evt_b"), WebhookSource.Connected);

        Assert.Single(s.EmailsOf(EmailTemplates.Names.ServicePaymentReceived));
        Assert.Equal(2, await s.Db.ProcessedStripeEvents.CountAsync());
    }

    [Fact]
    public async Task ServiceCharge_OnThePlatformEndpointListeningToConnectedAccounts_IsHandledLikeTheConnectOne()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();

        await Handler(s).HandleEventAsync(PaymentIntentEvent(opened, "payment_intent.succeeded", "evt_platform_acct"), WebhookSource.Platform);

        Assert.Equal(ServicePaymentStatus.Paid, (await s.ReloadAsync(opened.PaymentId)).Status);
    }

    [Fact]
    public async Task ServiceCharge_OnThePlatformAccountItself_NeedsReview_NeverPaid()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.AddAdminAsync();
        var opened = await s.OpenedAsync();
        s.ForgetNotifications();

        await Handler(s).HandleEventAsync(PaymentIntentEvent(opened, "payment_intent.succeeded", "evt_platform", account: null), WebhookSource.Platform);

        Assert.Equal(ServicePaymentStatus.NeedsReview, (await s.ReloadAsync(opened.PaymentId)).Status);
        Assert.Equal(ServiceRequestStatus.Completato, (await s.ReadAsync(opened.Request.Id)).Status);
        Assert.Single(s.EmailsOf(EmailTemplates.Names.ServicePaymentAdminAlert));
        Assert.Empty(s.EmailsOf(EmailTemplates.Names.ServicePaymentReceived));
    }

    [Theory]
    [InlineData("fee")]
    [InlineData("amount")]
    [InlineData("currency")]
    public async Task ServiceCharge_ThatDiffersFromWhatWasAskedFor_NeedsReview_ThroughTheRealEvent(string what)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();

        await Handler(s).HandleEventAsync(
            PaymentIntentEvent(
                opened,
                "payment_intent.succeeded",
                $"evt_diff_{what}",
                change: pi =>
                {
                    switch (what)
                    {
                        case "fee": pi.ApplicationFeeAmount = 1; break;
                        case "amount": pi.AmountReceived = 100; break;
                        default: pi.Currency = "usd"; break;
                    }
                }),
            WebhookSource.Connected);

        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Equal(ServicePaymentStatus.NeedsReview, payment.Status);
        Assert.Equal($"review:{what}", payment.FailureCode);
        Assert.Equal(ServiceRequestStatus.Completato, (await s.ReadAsync(opened.Request.Id)).Status);
    }

    [Fact]
    public async Task ServiceCharge_WhenTheUpdateFails_TheClaimIsReleased_AndTheEventCanBeRetried()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        var failing = new Mock<ISupplierPaymentWebhookService>();
        failing.Setup(x => x.ApplyPaymentIntentEventAsync(It.IsAny<ServicePaymentIntentEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated failure after the claim of the event."));
        var delivery = PaymentIntentEvent(opened, "payment_intent.succeeded", "evt_retry");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(s, servicePayments: failing.Object).HandleEventAsync(delivery, WebhookSource.Connected));

        Assert.False(await s.Db.ProcessedStripeEvents.AnyAsync(e => e.EventId == "evt_retry"));
        Assert.Equal(ServicePaymentStatus.Requested, (await s.ReloadAsync(opened.PaymentId)).Status);

        // Hangfire retries it, and this time it goes through.
        await Handler(s).HandleEventAsync(delivery, WebhookSource.Connected);
        Assert.Equal(ServicePaymentStatus.Paid, (await s.ReloadAsync(opened.PaymentId)).Status);
        Assert.True(await s.Db.ProcessedStripeEvents.AnyAsync(e => e.EventId == "evt_retry"));
    }

    [Fact]
    public async Task ServiceCharge_WithTheFlagOff_IsStillProcessed_MoneyInFlight()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        s.Flags.Set(Core.Features.FeatureFlags.SupplierOnlinePayments, false);

        await Handler(s).HandleEventAsync(PaymentIntentEvent(opened, "payment_intent.succeeded", "evt_flag_off"), WebhookSource.Connected);

        Assert.Equal(ServicePaymentStatus.Paid, (await s.ReloadAsync(opened.PaymentId)).Status);
    }

    [Fact]
    public void TheHandler_RoutesTheServiceChargeCase_BeforeTheGenericPaymentIntentSucceeded()
    {
        // The ordering is what makes the event reach the service payments: a C# switch takes the first case that matches, so a
        // service-charge event placed after the generic case would be "processed" with no effect on the Connect endpoint (the
        // generic handler drops every kind but direct-booking) and handed to the booking settlement on the platform endpoint.
        var code = System.IO.File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Casazen.Infrastructure", "External", "StripeWebhookHandler.cs"));

        var serviceCharge = code.IndexOf("case \"payment_intent.succeeded\" when IsServiceCharge(", StringComparison.Ordinal);
        var generic = code.IndexOf("case \"payment_intent.succeeded\":", StringComparison.Ordinal);
        var rent = code.IndexOf("case \"payment_intent.succeeded\" when IsRentCharge(", StringComparison.Ordinal);

        Assert.True(serviceCharge > 0, "the service-charge case is missing");
        Assert.True(generic > 0, "the generic case is missing");
        Assert.True(serviceCharge < generic, "the service-charge case must come BEFORE the generic payment_intent.succeeded");
        Assert.True(rent > 0 && rent < generic, "the rent case still precedes the generic one");
        foreach (var type in new[] { "processing", "payment_failed", "canceled" })
        {
            var specific = code.IndexOf($"case \"payment_intent.{type}\" when IsServiceCharge(", StringComparison.Ordinal);
            Assert.True(specific > 0, $"the service-charge case of {type} is missing");
        }
    }

    // ─── Refunds and disputes: the service payments' own, or the booking payments' as before ───

    [Fact]
    public async Task ChargeRefunded_OfAServicePayment_IsRecordedByTheServicePayments_AndNotByTheBookingRefunds()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        await s.PayAsync(opened);
        s.Gateway.AddExternalRefund(opened.IntentId, 2_000);
        s.ForgetNotifications();
        var bookingRefunds = new Mock<IPaymentRefundService>();

        await Handler(s, bookingRefunds: bookingRefunds.Object).HandleEventAsync(
            new Event
            {
                Id = "evt_cr",
                Type = "charge.refunded",
                Account = ServiceRequestScenario.SupplierAccountId,
                Data = new EventData { Object = new Charge { Id = "ch_1", PaymentIntentId = opened.IntentId } },
            },
            WebhookSource.Connected);

        var payment = await s.ReloadAsync(opened.PaymentId);
        Assert.Equal(2_000, payment.RefundedCents);
        Assert.Equal(ServicePaymentStatus.PartiallyRefunded, payment.Status);
        bookingRefunds.Verify(x => x.SyncPaymentIntentRefundsAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        bookingRefunds.Verify(x => x.ApplyStripeRefundsAsync(It.IsAny<IReadOnlyList<StripeRefundSnapshot>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Single(s.EmailsOf(EmailTemplates.Names.ServicePaymentRefundedPayer));
        Assert.Single(s.EmailsOf(EmailTemplates.Names.ServicePaymentRefundedSupplier));
    }

    [Fact]
    public async Task ChargeRefunded_OfABookingPayment_GoesOnToTheBookingRefunds_AsBefore()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var bookingRefunds = new Mock<IPaymentRefundService>();
        bookingRefunds.Setup(x => x.SyncPaymentIntentRefundsAsync("pi_booking", "acct_host", It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<Guid>());

        await Handler(s, bookingRefunds: bookingRefunds.Object).HandleEventAsync(
            new Event
            {
                Id = "evt_cr_booking",
                Type = "charge.refunded",
                Account = "acct_host",
                Data = new EventData { Object = new Charge { Id = "ch_2", PaymentIntentId = "pi_booking" } },
            },
            WebhookSource.Connected);

        bookingRefunds.Verify(x => x.SyncPaymentIntentRefundsAsync("pi_booking", "acct_host", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RefundUpdated_OfAServicePayment_IsTheServicePaymentsOwn_AndOfABookingPaymentGoesOnToTheBookingRefunds()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        await s.PayAsync(opened);
        s.Gateway.NewRefundStatus = "pending";
        var created = await s.Payments.RefundAsync(opened.PaymentId, 1_000, null, "auth0|admin");
        Assert.Equal(0, (await s.ReloadAsync(opened.PaymentId)).RefundedCents);
        var bookingRefunds = new Mock<IPaymentRefundService>();
        bookingRefunds.Setup(x => x.ApplyStripeRefundsAsync(It.IsAny<IReadOnlyList<StripeRefundSnapshot>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Guid>());

        await Handler(s, bookingRefunds: bookingRefunds.Object).HandleEventAsync(
            new Event
            {
                Id = "evt_ru",
                Type = "refund.updated",
                Account = ServiceRequestScenario.SupplierAccountId,
                Data = new EventData
                {
                    Object = new Refund
                    {
                        Id = created.StripeRefundId!,
                        PaymentIntentId = opened.IntentId,
                        Amount = 1_000,
                        Status = "succeeded",
                        Metadata = new Dictionary<string, string> { [ServiceCharges.RefundMetadataKey] = created.Id.ToString() },
                    },
                },
            },
            WebhookSource.Connected);
        await Handler(s, bookingRefunds: bookingRefunds.Object).HandleEventAsync(
            new Event
            {
                Id = "evt_ru_booking",
                Type = "refund.updated",
                Account = "acct_host",
                Data = new EventData { Object = new Refund { Id = "re_booking", PaymentIntentId = "pi_booking", Amount = 500, Status = "succeeded" } },
            },
            WebhookSource.Connected);

        Assert.Equal(1_000, (await s.ReloadAsync(opened.PaymentId)).RefundedCents);
        bookingRefunds.Verify(
            x => x.ApplyStripeRefundsAsync(It.Is<IReadOnlyList<StripeRefundSnapshot>>(r => r.Single().RefundId == "re_booking"), "acct_host", It.IsAny<CancellationToken>()),
            Times.Once);
        bookingRefunds.Verify(
            x => x.ApplyStripeRefundsAsync(It.Is<IReadOnlyList<StripeRefundSnapshot>>(r => r.Single().RefundId != "re_booking"), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Dispute_OnAServicePayment_TellsTheAdmins_AndOnAnotherPaymentIsUnhandledAsBefore()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.AddAdminAsync();
        var opened = await s.OpenedAsync();
        await s.PayAsync(opened);
        s.ForgetNotifications();

        await Handler(s).HandleEventAsync(DisputeEvent("evt_dp_ours", "dp_ours", opened.IntentId), WebhookSource.Connected);
        await Handler(s).HandleEventAsync(DisputeEvent("evt_dp_booking", "dp_booking", "pi_booking"), WebhookSource.Connected);

        var alert = Assert.Single(s.EmailsOf(EmailTemplates.Names.ServicePaymentAdminAlert));
        Assert.Contains("dp_ours", alert.Content.HtmlBody);
        Assert.Equal(ServicePaymentStatus.Paid, (await s.ReloadAsync(opened.PaymentId)).Status);
        Assert.True(await s.Db.ProcessedStripeEvents.AnyAsync(e => e.EventId == "evt_dp_booking"));
    }

    private static Event DisputeEvent(string eventId, string disputeId, string paymentIntentId) => new()
    {
        Id = eventId,
        Type = "charge.dispute.created",
        Account = ServiceRequestScenario.SupplierAccountId,
        Data = new EventData
        {
            Object = new Dispute { Id = disputeId, PaymentIntentId = paymentIntentId, Amount = 6_000, Currency = "eur", Reason = "fraudulent", Status = "needs_response" },
        },
    };

    // ─── account.updated: the supplier that becomes ready ───

    private static Event AccountUpdated(string eventId, string accountId, bool charges, bool payouts) => new()
    {
        Id = eventId,
        Type = "account.updated",
        Account = accountId,
        Data = new EventData
        {
            Object = new Account
            {
                Id = accountId,
                ChargesEnabled = charges,
                PayoutsEnabled = payouts,
                DetailsSubmitted = charges,
                Requirements = new AccountRequirements { CurrentlyDue = [] },
            },
        },
    };

    private static ConnectOnboardingService RealConnect(ServiceRequestScenario s) =>
        new(s.Db, Mock.Of<IStripeConnectGateway>(), NullLogger<ConnectOnboardingService>.Instance);

    [Fact]
    public async Task AccountUpdated_ASupplierThatBecomesReady_QueuesItsPendingPaymentRequests_OnceItIsCommitted()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.ConnectSupplierAsync(ready: false);

        await Handler(s, RealConnect(s)).HandleEventAsync(
            AccountUpdated("evt_ready_1", ServiceRequestScenario.SupplierAccountId, charges: true, payouts: true), WebhookSource.Connected);

        Assert.Equal([s.SupplierOrgId], s.JobScheduler.Scheduled);
        var org = await s.Db.Orgs.AsNoTracking().SingleAsync(o => o.Id == s.SupplierOrgId);
        Assert.True(org.ConnectChargesEnabled && org.ConnectPayoutsEnabled);
    }

    [Fact]
    public async Task AccountUpdated_ASupplierThatWasReadyAlready_QueuesNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.ConnectSupplierAsync(ready: true);

        await Handler(s, RealConnect(s)).HandleEventAsync(
            AccountUpdated("evt_ready_2", ServiceRequestScenario.SupplierAccountId, charges: true, payouts: true), WebhookSource.Connected);

        Assert.Empty(s.JobScheduler.Scheduled);
    }

    [Theory]
    [InlineData(true, false)] // charges, not payouts yet
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task AccountUpdated_ASupplierThatIsNotFullyReady_QueuesNothing(bool charges, bool payouts)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.ConnectSupplierAsync(ready: false);

        await Handler(s, RealConnect(s)).HandleEventAsync(
            AccountUpdated("evt_partial", ServiceRequestScenario.SupplierAccountId, charges, payouts), WebhookSource.Connected);

        Assert.Empty(s.JobScheduler.Scheduled);
    }

    [Fact]
    public async Task AccountUpdated_ReadyAfterBeingNotReady_ThenReadyAgain_QueuesOnlyTheTransition()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.ConnectSupplierAsync(ready: false);

        await Handler(s, RealConnect(s)).HandleEventAsync(AccountUpdated("evt_t1", ServiceRequestScenario.SupplierAccountId, true, true), WebhookSource.Connected);
        await Handler(s, RealConnect(s)).HandleEventAsync(AccountUpdated("evt_t2", ServiceRequestScenario.SupplierAccountId, true, true), WebhookSource.Connected);
        await Handler(s, RealConnect(s)).HandleEventAsync(AccountUpdated("evt_t3", ServiceRequestScenario.SupplierAccountId, false, false), WebhookSource.Connected);
        await Handler(s, RealConnect(s)).HandleEventAsync(AccountUpdated("evt_t4", ServiceRequestScenario.SupplierAccountId, true, true), WebhookSource.Connected);

        // Becoming ready (first time) and becoming ready again after Stripe disabled it: two transitions, two jobs.
        Assert.Equal([s.SupplierOrgId, s.SupplierOrgId], s.JobScheduler.Scheduled);
    }

    [Fact]
    public async Task AccountUpdated_TheSameEventTwice_QueuesOnce()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.ConnectSupplierAsync(ready: false);
        var delivery = AccountUpdated("evt_same", ServiceRequestScenario.SupplierAccountId, true, true);

        await Handler(s, RealConnect(s)).HandleEventAsync(delivery, WebhookSource.Connected);
        await Handler(s, RealConnect(s)).HandleEventAsync(delivery, WebhookSource.Connected);

        Assert.Single(s.JobScheduler.Scheduled);
    }

    [Fact]
    public async Task AccountUpdated_OfAHostOrg_OrAnUnknownAccount_OrOnThePlatformEndpoint_QueuesNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var host = await s.Db.Orgs.SingleAsync(o => o.Id == s.HostOrgId);
        host.StripeConnectedAccountId = "acct_host";
        host.ConnectChargesEnabled = false;
        host.ConnectPayoutsEnabled = false;
        await s.ConnectSupplierAsync(ready: false);
        await s.Db.SaveChangesAsync();

        await Handler(s, RealConnect(s)).HandleEventAsync(AccountUpdated("evt_h", "acct_host", true, true), WebhookSource.Connected);
        await Handler(s, RealConnect(s)).HandleEventAsync(AccountUpdated("evt_u", "acct_unknown", true, true), WebhookSource.Connected);
        await Handler(s, RealConnect(s)).HandleEventAsync(
            AccountUpdated("evt_p", ServiceRequestScenario.SupplierAccountId, true, true), WebhookSource.Platform);

        Assert.Empty(s.JobScheduler.Scheduled);
    }

    [Fact]
    public async Task AccountUpdated_WhenApplyingTheAccountFails_NothingIsQueued_AndTheEventCanBeRetried()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.ConnectSupplierAsync(ready: false);
        var connect = new Mock<IConnectOnboardingService>();
        connect.Setup(x => x.ApplyAccountUpdatedAsync(It.IsAny<ConnectAccountSnapshot>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated failure while applying the account."));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(s, connect.Object).HandleEventAsync(
            AccountUpdated("evt_fail", ServiceRequestScenario.SupplierAccountId, true, true), WebhookSource.Connected));

        Assert.Empty(s.JobScheduler.Scheduled);
        Assert.False(await s.Db.ProcessedStripeEvents.AnyAsync(e => e.EventId == "evt_fail"));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !System.IO.File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
