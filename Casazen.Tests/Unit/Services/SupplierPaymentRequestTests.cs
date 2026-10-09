using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-15a: the supplier asks for the payment of a completed request paid inside CasaZen, or reminds the payer
/// (<c>POST api/supplier/requests/{id}/payment-request</c>): a new personal link replaces the previous one, at most one email a
/// day, only when the supplier can be paid, and only for a request the supplier owns and may still act on.
/// </summary>
public class SupplierPaymentRequestTests
{
    private static async Task<ServiceRequest> CompletedAsync(ServiceRequestScenario s) => await s.CompletedOnlineAsync();

    [Fact]
    public async Task RequestPayment_AfterADay_SendsAReminderWithANewLink_AndTheOldLinkStopsWorking()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var completed = await CompletedAsync(s);
        var firstLink = ServicePaymentTestSupport.LinkOf(Assert.Single(s.Emails.RequestEmails()).Content);
        s.Clock.Advance(TimeSpan.FromHours(24));

        var payment = await s.Service.RequestPaymentAsync(completed.Id, s.SupplierOrgId);

        Assert.Equal(2, payment.SentCount);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, payment.LastSentAt);
        // RequestedAt is when the payer was first asked; the reminder moves only the last email and the validity of the link.
        Assert.Equal(ServiceRequestScenario.Instant.UtcDateTime, payment.RequestedAt);
        var reminder = Assert.Single(s.Emails.ReminderEmails());
        Assert.Equal("host@test.com", reminder.To);
        var secondLink = ServicePaymentTestSupport.LinkOf(reminder.Content);
        Assert.Equal(firstLink.PaymentId, secondLink.PaymentId);
        Assert.NotEqual(firstLink.Token, secondLink.Token);
        var saved = await s.OnlyPaymentOfAsync(completed.Id);
        Assert.True(CheckoutOutcomes.TokenMatches(saved.PaymentTokenHash, secondLink.Token));
        Assert.False(CheckoutOutcomes.TokenMatches(saved.PaymentTokenHash, firstLink.Token));
        // Still one request email: the first one.
        Assert.Single(s.Emails.RequestEmails());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(23 * 60 + 59)]
    public async Task RequestPayment_BeforeADayHasPassed_Is422AndSendsNothing(int minutes)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var completed = await CompletedAsync(s);
        var before = await s.OnlyPaymentOfAsync(completed.Id);
        s.Clock.Advance(TimeSpan.FromMinutes(minutes));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.RequestPaymentAsync(completed.Id, s.SupplierOrgId));

        Assert.Equal(ServicePaymentErrors.RequestTooSoon, ex.Code);
        Assert.Equal("service_payment_request_too_soon", ex.Code);
        Assert.Equal(ServicePaymentLimits.MinHoursBetweenRequests, Assert.Single(ex.MessageArgs));
        Assert.Single(s.Emails.PaymentEmails());
        var after = await s.OnlyPaymentOfAsync(completed.Id);
        Assert.Equal(before.PaymentTokenHash, after.PaymentTokenHash);
        Assert.Equal(1, after.SentCount);
    }

    [Fact]
    public async Task RequestPayment_AReminderEveryDay_CountsEachEmail()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var completed = await CompletedAsync(s);

        for (var day = 1; day <= 3; day++)
        {
            s.Clock.Advance(TimeSpan.FromHours(24));
            await s.Service.RequestPaymentAsync(completed.Id, s.SupplierOrgId);
        }

        Assert.Equal(4, (await s.OnlyPaymentOfAsync(completed.Id)).SentCount);
        Assert.Single(s.Emails.RequestEmails());
        Assert.Equal(3, s.Emails.ReminderEmails().Count);
    }

    [Fact]
    public async Task RequestPayment_TheEmailCannotBeQueued_Is422_AndThePreviousLinkStillWorks()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var completed = await CompletedAsync(s);
        var link = ServicePaymentTestSupport.LinkOf(Assert.Single(s.Emails.RequestEmails()).Content);
        var before = await s.OnlyPaymentOfAsync(completed.Id);
        s.Clock.Advance(TimeSpan.FromHours(25));
        s.Emails.Refuse = true;

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.RequestPaymentAsync(completed.Id, s.SupplierOrgId));

        Assert.Equal(ServicePaymentErrors.RequestNotSent, ex.Code);
        var after = await s.OnlyPaymentOfAsync(completed.Id);
        // Nothing was sent, so nothing changed: the link the host already has keeps working and the count did not move.
        Assert.Equal(before.PaymentTokenHash, after.PaymentTokenHash);
        Assert.Equal(before.LastSentAt, after.LastSentAt);
        Assert.Equal(before.RequestedAt, after.RequestedAt);
        Assert.Equal(1, after.SentCount);
        Assert.True(CheckoutOutcomes.TokenMatches(after.PaymentTokenHash, link.Token));
    }

    [Fact]
    public async Task RequestPayment_ARequestNotPaidInsideCasaZen_Is422()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();
        var completed = await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.RequestPaymentAsync(completed.Id, s.SupplierOrgId));

        Assert.Equal(ServicePaymentErrors.NotOnline, ex.Code);
        Assert.Empty(await s.PaymentsOfAsync(completed.Id));
    }

    [Theory]
    [InlineData(ServiceRequestStatus.PresoInCarico)]
    [InlineData(ServiceRequestStatus.InCorso)]
    public async Task RequestPayment_AJobNotCompletedYet_Is422(ServiceRequestStatus status)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnablePaymentsAsync();
        var taken = await s.TakenAsync();
        if (status == ServiceRequestStatus.InCorso)
            await s.Service.StartAsync(taken.Id, s.SupplierOrgId);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.RequestPaymentAsync(taken.Id, s.SupplierOrgId));

        Assert.Equal(ServicePaymentErrors.NotRequestable, ex.Code);
        Assert.Empty(await s.PaymentsOfAsync(taken.Id));
    }

    [Fact]
    public async Task RequestPayment_ARequestAlreadyPaid_Is422()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var completed = await CompletedAsync(s);
        await s.ChangeRequestAsync(completed.Id, r => r.Status = ServiceRequestStatus.Pagato);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.RequestPaymentAsync(completed.Id, s.SupplierOrgId));

        Assert.Equal(ServicePaymentErrors.NotRequestable, ex.Code);
    }

    [Theory]
    [InlineData(ServicePaymentStatus.Paid, "service_payment_not_payable", "ServicePaymentAlreadyPaid")]
    [InlineData(ServicePaymentStatus.PartiallyRefunded, "service_payment_not_payable", "ServicePaymentAlreadyPaid")]
    [InlineData(ServicePaymentStatus.Refunded, "service_payment_not_payable", "ServicePaymentAlreadyPaid")]
    [InlineData(ServicePaymentStatus.Processing, "service_payment_in_flight", "ServicePaymentInFlight")]
    [InlineData(ServicePaymentStatus.NeedsReview, "service_payment_in_flight", "ServicePaymentInFlight")]
    public async Task RequestPayment_APaymentPaidOrInFlight_Is409AndSendsNothing(ServicePaymentStatus status, string code, string key)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var completed = await CompletedAsync(s);
        await ChangePaymentAsync(s, completed.Id, p =>
        {
            p.Status = status;
            p.PaidAt = ServiceRequestScenario.Instant.UtcDateTime;
            p.PaidVia = ServicePaymentChannel.Stripe;
        });
        s.Clock.Advance(TimeSpan.FromDays(2));
        s.ForgetNotifications();

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => s.Service.RequestPaymentAsync(completed.Id, s.SupplierOrgId));

        Assert.Equal(code, ex.Code);
        Assert.Equal(key, ex.MessageKey);
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task RequestPayment_AFailedPayment_CanBeAskedAgain()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var completed = await CompletedAsync(s);
        await ChangePaymentAsync(s, completed.Id, p =>
        {
            p.Status = ServicePaymentStatus.Failed;
            p.FailureCode = "card_declined";
        });
        s.Clock.Advance(TimeSpan.FromDays(1));

        var payment = await s.Service.RequestPaymentAsync(completed.Id, s.SupplierOrgId);

        Assert.Equal(ServicePaymentStatus.Failed, payment.Status);
        Assert.Equal(2, payment.SentCount);
        Assert.Single(s.Emails.ReminderEmails());
    }

    [Fact]
    public async Task RequestPayment_WhenThePaymentWasCanceled_CreatesANewOneWithTheCommissionOfToday()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var completed = await CompletedAsync(s);
        await ChangePaymentAsync(s, completed.Id, p =>
        {
            p.Status = ServicePaymentStatus.Canceled;
            p.CanceledAt = ServiceRequestScenario.Instant.UtcDateTime;
        });
        s.Kit.PaymentOptions.CommissionPercent = 15m;
        s.Clock.Advance(TimeSpan.FromMinutes(1));
        s.ForgetNotifications();

        var created = await s.Service.RequestPaymentAsync(completed.Id, s.SupplierOrgId);

        var all = await s.PaymentsOfAsync(completed.Id);
        Assert.Equal(2, all.Count);
        Assert.Equal(ServicePaymentStatus.Canceled, all[0].Status);
        Assert.Equal(created.Id, all[1].Id);
        Assert.Equal(ServicePaymentStatus.Requested, created.Status);
        Assert.Equal(15m, created.CommissionPercent);
        Assert.Equal(900, created.ApplicationFeeCents);
        Assert.Equal(1, created.SentCount);
        Assert.Single(s.Emails.RequestEmails());
    }

    [Fact]
    public async Task RequestPayment_TheSupplierNoLongerReady_Is422WithTheSupplierAccountCode()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var completed = await CompletedAsync(s);
        s.Clock.Advance(TimeSpan.FromDays(2));
        await s.ConnectSupplierAsync(ready: false);
        s.ForgetNotifications();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.RequestPaymentAsync(completed.Id, s.SupplierOrgId));

        Assert.Equal("supplier_payments_not_ready", ex.Code);
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task RequestPayment_AnAmountBelowTheMinimumRaisedLater_Is422()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var completed = await CompletedAsync(s);
        s.Kit.PaymentOptions.MinAmountCents = 10_000;
        s.Clock.Advance(TimeSpan.FromDays(2));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.RequestPaymentAsync(completed.Id, s.SupplierOrgId));

        Assert.Equal(ServicePaymentErrors.AmountRequired, ex.Code);
    }

    [Fact]
    public async Task RequestPayment_ARequestOfAnotherSupplier_IsForbidden_AndAnUnknownOneNotFound()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var completed = await CompletedAsync(s);
        var other = await s.AddOtherSupplierAsync();
        s.ForgetNotifications();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service.RequestPaymentAsync(completed.Id, other));
        var ex = await Assert.ThrowsAsync<NotFoundException>(() => s.Service.RequestPaymentAsync(Guid.NewGuid(), s.SupplierOrgId));

        Assert.Equal(ServiceRequestErrorCodes.NotFound, ex.Code);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Equal(1, (await s.OnlyPaymentOfAsync(completed.Id)).SentCount);
    }

    [Fact]
    public async Task RequestPayment_ASuspendedSupplier_Is422LikeEveryOtherSupplierAction()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var completed = await CompletedAsync(s);
        await s.SetSupplierStatusAsync(SupplierStatus.Suspended);
        s.Clock.Advance(TimeSpan.FromDays(2));
        s.ForgetNotifications();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.RequestPaymentAsync(completed.Id, s.SupplierOrgId));

        Assert.Equal(ServiceRequestErrorCodes.SupplierNotActive, ex.Code);
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task RequestPayment_ASecondLivePaymentRefusedByTheDatabase_Is409AndNothingIsSent()
    {
        // The unique index "one payment per request that is not canceled" is the last guard: its violation is a conflict, never a 500.
        var interceptor = new FailingSaveInterceptor();
        using var s = await ServiceRequestScenario.CreateAsync(saveInterceptor: interceptor);
        var completed = await CompletedAsync(s);
        await ChangePaymentAsync(s, completed.Id, p => p.Status = ServicePaymentStatus.Canceled);
        s.ForgetNotifications();

        interceptor.Failure = new DbUpdateException(
            "save failed",
            new PostgresException(
                "duplicate key value violates unique constraint", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation,
                constraintName: "UIX_ServiceRequestPayments_ServiceRequestId_Live"));
        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => s.Service.RequestPaymentAsync(completed.Id, s.SupplierOrgId));
        interceptor.Failure = null;

        Assert.Equal(ServicePaymentErrors.StateChanged, ex.Code);
        Assert.Equal("service_payment_state_changed", ex.Code);
        Assert.Equal(ServicePaymentErrors.StateChangedMessageKey, ex.MessageKey);
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task RequestPayment_TheRequestChangedWhileItWasBeingSaved_Is409AndNothingIsSent()
    {
        var interceptor = new FailingSaveInterceptor();
        using var s = await ServiceRequestScenario.CreateAsync(saveInterceptor: interceptor);
        var completed = await CompletedAsync(s);
        s.Clock.Advance(TimeSpan.FromDays(2));
        s.ForgetNotifications();

        interceptor.Failure = new DbUpdateConcurrencyException("the request changed");
        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => s.Service.RequestPaymentAsync(completed.Id, s.SupplierOrgId));
        interceptor.Failure = null;

        Assert.Equal(ServiceRequestErrorCodes.StateChanged, ex.Code);
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task RequestPayment_TheCallerGoesAwayRightAfterTheLinkIsSaved_TheEmailIsStillQueued()
    {
        // A dropped connection after the commit must not leave the payer's old link dead and nothing sent (and the supplier
        // locked out for a day).
        var interceptor = new FailingSaveInterceptor();
        using var s = await ServiceRequestScenario.CreateAsync(saveInterceptor: interceptor);
        var completed = await CompletedAsync(s);
        s.Clock.Advance(TimeSpan.FromDays(2));
        s.ForgetNotifications();
        using var caller = new CancellationTokenSource();
        interceptor.AfterSave = caller.Cancel;

        var payment = await s.Service.RequestPaymentAsync(completed.Id, s.SupplierOrgId, caller.Token);
        interceptor.AfterSave = null;

        Assert.True(caller.IsCancellationRequested);
        Assert.Equal(2, payment.SentCount);
        var link = ServicePaymentTestSupport.LinkOf(Assert.Single(s.Emails.ReminderEmails()).Content);
        Assert.True(CheckoutOutcomes.TokenMatches((await s.OnlyPaymentOfAsync(completed.Id)).PaymentTokenHash, link.Token));
    }

    /// <summary>Writes a change straight to the only live payment of a request.</summary>
    internal static async Task ChangePaymentAsync(ServiceRequestScenario s, Guid requestId, Action<ServiceRequestPayment> change)
    {
        var payment = await s.Db.ServiceRequestPayments.SingleAsync(p => p.ServiceRequestId == requestId);
        change(payment);
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
    }
}
