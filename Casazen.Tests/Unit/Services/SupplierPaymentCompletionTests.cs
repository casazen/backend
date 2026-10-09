using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-15a: what happens to the payment when a request paid inside CasaZen is completed, and when the host confirms an amount above
/// the quote (decision D7). The payment is created <c>Requested</c> in the same save as the completion, with the commission
/// snapshotted (the supplier's own percentage, else the platform's, rounded to the cent away from zero); the link goes to the host's
/// address after the save; nothing reaches Stripe (the PaymentIntent is born with the payer's session).
/// </summary>
public class SupplierPaymentCompletionTests
{
    // ─── The payment is born with the completion ───

    [Fact]
    public async Task Complete_OnlineRequest_CreatesTheRequestedPaymentWithTheCommissionSnapshot()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var completed = await s.CompletedOnlineAsync();

        Assert.Equal(ServiceRequestStatus.Completato, completed.Status);
        Assert.Equal(ServiceRequestPaymentMode.Online, completed.PaymentMode);
        var payment = await s.OnlyPaymentOfAsync(completed.Id);
        Assert.Equal(ServicePaymentStatus.Requested, payment.Status);
        Assert.Equal(completed.Id, payment.ServiceRequestId);
        Assert.Equal(s.SupplierOrgId, payment.SupplierOrgId);
        Assert.Equal(ServicePayerKind.Host, payment.PayerKind);
        Assert.Equal(s.HostOrgId, payment.PayerOrgId);
        Assert.Equal(ServiceRequestScenario.ServicePriceCents, payment.AmountCents);
        Assert.Equal("eur", payment.Currency);
        // 10 % of 60,00 euro: the commission and what is left for the supplier are kept on the row.
        Assert.Equal(10m, payment.CommissionPercent);
        Assert.Equal(600, payment.ApplicationFeeCents);
        Assert.Equal(5_400, payment.NetCents);
        Assert.Null(payment.FeeVatMode);
        Assert.Null(payment.FeeVatCents);
        Assert.Equal(0, payment.RefundedCents);
        Assert.Null(payment.PaidAt);
        Assert.Null(payment.PaidVia);
        // The PaymentIntent is created when the payer opens the session, never before.
        Assert.Null(payment.StripePaymentIntentId);
        Assert.Null(payment.ConnectedAccountId);
        Assert.Equal(0, payment.PaymentIntentCount);
        Assert.Empty(s.Gateway.Created);
        var lines = ServiceRequestJson.ReadPriceLines(payment.LineItemsJson);
        Assert.Equal(ServiceRequestScenario.ServicePriceCents, lines.Sum(l => l.AmountCents));
        Assert.Equal(ServiceRequestPriceLineKinds.Base, Assert.Single(lines).Kind);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, payment.CreatedAt);
    }

    [Fact]
    public async Task Complete_OnlineRequest_SendsTheLinkToTheHostAfterTheSave_AndStoresOnlyTheHashOfTheToken()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var completed = await s.CompletedOnlineAsync();

        var payment = await s.OnlyPaymentOfAsync(completed.Id);
        var email = Assert.Single(s.Emails.RequestEmails());
        Assert.Equal("host@test.com", email.To);
        var (paymentId, token) = ServicePaymentTestSupport.LinkOf(email.Content);
        Assert.Equal(payment.Id, paymentId);
        // 256 random bits, URL-safe; the row keeps their SHA-256 and nothing else.
        Assert.Equal(43, token.Length);
        Assert.NotNull(payment.PaymentTokenHash);
        Assert.Equal(64, payment.PaymentTokenHash.Length);
        Assert.NotEqual(token, payment.PaymentTokenHash);
        Assert.True(CheckoutOutcomes.TokenMatches(payment.PaymentTokenHash, token));
        Assert.False(CheckoutOutcomes.TokenMatches(payment.PaymentTokenHash, token + "x"));
        Assert.DoesNotContain(token, payment.LineItemsJson);
        // The email names the supplier, the service, the property and the amount.
        Assert.Contains("Supplier Org", email.Content.HtmlBody);
        Assert.Contains(ServiceRequestScenario.ServiceName, email.Content.HtmlBody);
        Assert.Contains(ServiceRequestScenario.PropertyName, email.Content.HtmlBody);
        Assert.Contains("60,00 €", email.Content.HtmlBody);
        // The link counts from the email: first request, one email, valid 30 days.
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, payment.RequestedAt);
        Assert.Equal(payment.RequestedAt, payment.LastSentAt);
        Assert.Equal(1, payment.SentCount);
        // The host still gets the ordinary news that the work is completed.
        Assert.Contains(s.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.ServiceRequestStatusChanged);
    }

    [Fact]
    public async Task Complete_OnlineRequestWithExtras_ChargesTheFinalAmountAndKeepsTheLines()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnablePaymentsAsync();
        var taken = await s.TakenAsync();

        var completed = await s.Service.CompleteAsync(
            taken.Id,
            s.SupplierOrgId,
            new CompleteServiceRequestCommand(Extras: [new ServiceRequestExtra("Bagno in più", 1_000)]));

        // 60,00 euro agreed + 10,00 euro extra = 70,00 euro, which is 16.7 % above the quote: within the 20 % of decision D7.
        Assert.False(completed.FinalAmountNeedsConfirmation);
        var payment = await s.OnlyPaymentOfAsync(completed.Id);
        Assert.Equal(7_000, payment.AmountCents);
        Assert.Equal(700, payment.ApplicationFeeCents);
        Assert.Equal(6_300, payment.NetCents);
        var lines = ServiceRequestJson.ReadPriceLines(payment.LineItemsJson);
        Assert.Equal(2, lines.Count);
        Assert.Equal(7_000, lines.Sum(l => l.AmountCents));
        Assert.Contains(lines, l => l is { Kind: "extra", Label: "Bagno in più", AmountCents: 1_000 });
    }

    // ─── The commission: the supplier's own percentage, rounding, no rewriting ───

    [Theory]
    [InlineData(0, 6_000, 0, 6_000)] // a free period
    [InlineData(7.5, 6_000, 450, 5_550)]
    [InlineData(50, 6_000, 3_000, 3_000)]
    [InlineData(10.5, 6_000, 630, 5_370)]
    public async Task Complete_TheSupplierOwnPercentage_IsUsedAndSnapshotted(double percent, int amount, int fee, int net)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var profile = await s.Db.SupplierProfiles.SingleAsync(p => p.OrgId == s.SupplierOrgId);
        profile.CommissionPercentOverride = (decimal)percent;
        await s.Db.SaveChangesAsync();

        var completed = await s.CompletedOnlineAsync(amount);

        var payment = await s.OnlyPaymentOfAsync(completed.Id);
        Assert.Equal((decimal)percent, payment.CommissionPercent);
        Assert.Equal(fee, payment.ApplicationFeeCents);
        Assert.Equal(net, payment.NetCents);
    }

    [Theory]
    [InlineData(6_050, 605)] // 605.0 cents
    [InlineData(6_055, 606)] // 605.5 cents: half a cent goes up
    [InlineData(6_054, 605)] // 605.4 cents
    [InlineData(6_056, 606)] // 605.6 cents
    public async Task Complete_TheCommissionIsRoundedToTheCentAwayFromZero(int amount, int expectedFee)
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var completed = await s.CompletedOnlineAsync(amount);

        var payment = await s.OnlyPaymentOfAsync(completed.Id);
        Assert.Equal(expectedFee, payment.ApplicationFeeCents);
        Assert.Equal(amount - expectedFee, payment.NetCents);
    }

    [Fact]
    public async Task Complete_ARateThatChangesLater_NeverRewritesAPaymentThatExists()
    {
        using var s = await ServiceRequestScenario.CreateAsync(paymentOptions: new SupplierPaymentsOptions { CommissionPercent = 10m });
        var first = await s.CompletedOnlineAsync();

        // The product owner decides another percentage: the existing payment keeps the figures it was created with.
        s.Kit.PaymentOptions.CommissionPercent = 20m;
        var second = await s.TakenAsync();
        var completedSecond = await s.Service.CompleteAsync(second.Id, s.SupplierOrgId);

        var oldPayment = await s.OnlyPaymentOfAsync(first.Id);
        Assert.Equal(10m, oldPayment.CommissionPercent);
        Assert.Equal(600, oldPayment.ApplicationFeeCents);
        var newPayment = await s.OnlyPaymentOfAsync(completedSecond.Id);
        Assert.Equal(20m, newPayment.CommissionPercent);
        Assert.Equal(1_200, newPayment.ApplicationFeeCents);
        Assert.Equal(4_800, newPayment.NetCents);
    }

    [Fact]
    public async Task Complete_AFreePlatformPercentage_ChargesNoCommissionAndSendsNoFee()
    {
        using var s = await ServiceRequestScenario.CreateAsync(paymentOptions: new SupplierPaymentsOptions { CommissionPercent = 0m });

        var completed = await s.CompletedOnlineAsync();

        var payment = await s.OnlyPaymentOfAsync(completed.Id);
        Assert.Equal(0m, payment.CommissionPercent);
        Assert.Equal(0, payment.ApplicationFeeCents);
        Assert.Equal(payment.AmountCents, payment.NetCents);
    }

    [Fact]
    public async Task Complete_WithoutAConfiguredCommission_DoesNotInventOne()
    {
        // The options are validated at startup; if one still reaches the service without a percentage, it stops instead of using a default.
        using var s = await ServiceRequestScenario.CreateAsync(paymentOptions: new SupplierPaymentsOptions { CommissionPercent = null });
        await s.EnablePaymentsAsync();
        var taken = await s.TakenAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Service.CompleteAsync(taken.Id, s.SupplierOrgId));

        Assert.Equal(ServiceRequestStatus.PresoInCarico, (await s.ReadAsync(taken.Id)).Status);
        Assert.Empty(await s.PaymentsOfAsync(taken.Id));
    }

    // ─── What cannot be charged online falls back to the manual flow ───

    [Fact]
    public async Task Complete_AmountBelowTheMinimum_FallsBackToManualWithNoPayment()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var completed = await s.CompletedOnlineAsync(finalAmountCents: 49);

        Assert.Equal(ServiceRequestPaymentMode.Manual, completed.PaymentMode);
        Assert.Equal(ServiceRequestPaymentMode.Manual, (await s.ReadAsync(completed.Id)).PaymentMode);
        Assert.Empty(await s.PaymentsOfAsync(completed.Id));
        Assert.Empty(s.Emails.PaymentEmails());
        // Nothing is stuck: the host marks it paid by hand, as before.
        Assert.Equal(ServiceRequestStatus.Pagato, (await s.Service.MarkPaidAsync(completed.Id, s.HostOrgId)).Status);
    }

    [Fact]
    public async Task Complete_AmountAtTheMinimum_IsChargedOnline()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var completed = await s.CompletedOnlineAsync(finalAmountCents: 50);

        // Quote 60,00 and a final amount of 0,50: below the quote, never needs a confirmation. 10 % of 50 cents is 5.
        var payment = await s.OnlyPaymentOfAsync(completed.Id);
        Assert.Equal(50, payment.AmountCents);
        Assert.Equal(5, payment.ApplicationFeeCents);
    }

    [Fact]
    public async Task Complete_ARequestWithoutAnyPrice_FallsBackToManual()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnablePaymentsAsync();
        var request = await s.RequestAsync(withService: false);
        var taken = await s.Service.TakeAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);
        Assert.Equal(ServiceRequestPaymentMode.Online, taken.PaymentMode);

        var completed = await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId);

        Assert.Null(completed.FinalAmountCents);
        Assert.Equal(ServiceRequestPaymentMode.Manual, completed.PaymentMode);
        Assert.Empty(await s.PaymentsOfAsync(completed.Id));
    }

    [Fact]
    public async Task Complete_TheFlagTurnedOffAfterTheTake_FallsBackToManual_AndNothingIsStuck()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnablePaymentsAsync();
        var taken = await s.TakenAsync();
        s.Flags.Set(FeatureFlags.SupplierOnlinePayments, false);

        var completed = await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId);

        Assert.Equal(ServiceRequestPaymentMode.Manual, completed.PaymentMode);
        Assert.Empty(await s.PaymentsOfAsync(completed.Id));
        Assert.Empty(s.Emails.PaymentEmails());
        Assert.Equal(ServiceRequestStatus.Pagato, (await s.Service.MarkPaidAsync(completed.Id, s.HostOrgId)).Status);
    }

    [Fact]
    public async Task Complete_AManualRequest_CreatesNoPaymentAndSendsNoPaymentEmail()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();

        var completed = await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId);

        Assert.Equal(ServiceRequestPaymentMode.Manual, completed.PaymentMode);
        Assert.Empty(await s.PaymentsOfAsync(completed.Id));
        Assert.Empty(s.Emails.PaymentEmails());
    }

    // ─── A payment that cannot be sent yet is pending ───

    [Fact]
    public async Task Complete_TheSupplierNotReadyAnymore_LeavesThePaymentPendingWithoutALink()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnablePaymentsAsync();
        var taken = await s.TakenAsync();
        await s.ConnectSupplierAsync(ready: false);

        var completed = await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId);

        Assert.Equal(ServiceRequestPaymentMode.Online, completed.PaymentMode);
        var payment = await s.OnlyPaymentOfAsync(completed.Id);
        Assert.Equal(ServicePaymentStatus.Requested, payment.Status);
        Assert.Null(payment.PaymentTokenHash);
        Assert.Null(payment.RequestedAt);
        Assert.Null(payment.LastSentAt);
        Assert.Equal(0, payment.SentCount);
        Assert.Empty(s.Emails.PaymentEmails());

        // The supplier cannot ask for it until it can be paid ...
        var notReady = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.RequestPaymentAsync(completed.Id, s.SupplierOrgId));
        Assert.Equal(SupplierPaymentsErrors.NotReady, notReady.Code);
        Assert.Empty(s.Emails.PaymentEmails());

        // ... and then the link goes out, as the first request.
        await s.ConnectSupplierAsync(ready: true);
        var sent = await s.Service.RequestPaymentAsync(completed.Id, s.SupplierOrgId);
        Assert.Equal(1, sent.SentCount);
        Assert.NotNull(sent.PaymentTokenHash);
        Assert.Single(s.Emails.RequestEmails());
        Assert.Empty(s.Emails.ReminderEmails());
    }

    [Fact]
    public async Task Complete_TheHostOrgHasNoEmail_LeavesThePaymentPendingWithoutALink()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnablePaymentsAsync();
        var host = await s.Db.Orgs.SingleAsync(o => o.Id == s.HostOrgId);
        host.ContactEmail = string.Empty;
        await s.Db.SaveChangesAsync();
        var taken = await s.TakenAsync();

        var completed = await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId);

        var payment = await s.OnlyPaymentOfAsync(completed.Id);
        Assert.Null(payment.PaymentTokenHash);
        Assert.Equal(0, payment.SentCount);
        Assert.Empty(s.Emails.PaymentEmails());
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.RequestPaymentAsync(completed.Id, s.SupplierOrgId));
        Assert.Equal(ServicePaymentErrors.NoRecipient, ex.Code);
    }

    [Fact]
    public async Task Complete_TheEmailCannotBeQueued_TheLinkIsTakenBack_AndThePaymentStaysPending()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnablePaymentsAsync();
        var taken = await s.TakenAsync();
        s.Emails.Accepting = false;

        var completed = await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId);

        // The completion stands (the work is done); the payment waits for the supplier to ask again.
        Assert.Equal(ServiceRequestStatus.Completato, completed.Status);
        var payment = await s.OnlyPaymentOfAsync(completed.Id);
        Assert.Equal(ServicePaymentStatus.Requested, payment.Status);
        Assert.Null(payment.PaymentTokenHash);
        Assert.Null(payment.RequestedAt);
        Assert.Null(payment.LastSentAt);
        Assert.Equal(0, payment.SentCount);

        s.Emails.Accepting = true;
        var sent = await s.Service.RequestPaymentAsync(completed.Id, s.SupplierOrgId);
        Assert.Equal(1, sent.SentCount);
        Assert.Single(s.Emails.RequestEmails());
    }

    [Fact]
    public async Task Complete_TheSaveFails_NoPaymentIsLeftBehindAndNoLinkIsSent()
    {
        var interceptor = new FailingSaveInterceptor();
        using var s = await ServiceRequestScenario.CreateAsync(saveInterceptor: interceptor);
        await s.EnablePaymentsAsync();
        var taken = await s.TakenAsync();
        s.ForgetNotifications();

        interceptor.Failure = new DbUpdateConcurrencyException("the request changed");
        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => s.Service.CompleteAsync(taken.Id, s.SupplierOrgId));
        interceptor.Failure = null;

        Assert.Equal(ServiceRequestErrorCodes.StateChanged, ex.Code);
        Assert.Empty(await s.PaymentsOfAsync(taken.Id));
        Assert.Empty(s.Emails.Snapshot());
        // The context does not keep the payment that was never saved: the next save does not write it by accident.
        Assert.DoesNotContain(s.Db.ChangeTracker.Entries<ServiceRequestPayment>(), e => e.State == EntityState.Added);
    }

    [Fact]
    public async Task Complete_AnotherCallCreatedThePaymentFirst_Is409AndNoPaymentIsLeftBehind()
    {
        // Two completions that read the same request: the loser may be refused by the unique index instead of the xmin check.
        var interceptor = new FailingSaveInterceptor();
        using var s = await ServiceRequestScenario.CreateAsync(saveInterceptor: interceptor);
        await s.EnablePaymentsAsync();
        var taken = await s.TakenAsync();
        s.ForgetNotifications();

        interceptor.Failure = new DbUpdateException(
            "save failed",
            new PostgresException(
                "duplicate key value violates unique constraint", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation,
                constraintName: ServiceCharges.LivePaymentIndexName));
        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => s.Service.CompleteAsync(taken.Id, s.SupplierOrgId));
        interceptor.Failure = null;

        Assert.Equal(ServiceRequestErrorCodes.StateChanged, ex.Code);
        Assert.Empty(await s.PaymentsOfAsync(taken.Id));
        Assert.Empty(s.Emails.Snapshot());
        Assert.DoesNotContain(s.Db.ChangeTracker.Entries<ServiceRequestPayment>(), e => e.State == EntityState.Added);
    }

    [Fact]
    public async Task Complete_AnotherUniqueViolation_IsNotHiddenAsAConflict()
    {
        // Only the "one live payment per request" index means that someone else got there first: any other database error stays one.
        var interceptor = new FailingSaveInterceptor();
        using var s = await ServiceRequestScenario.CreateAsync(saveInterceptor: interceptor);
        await s.EnablePaymentsAsync();
        var taken = await s.TakenAsync();

        interceptor.Failure = new DbUpdateException(
            "save failed",
            new PostgresException(
                "duplicate key value violates unique constraint", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation,
                constraintName: "UIX_SomethingElse"));
        await Assert.ThrowsAsync<DbUpdateException>(() => s.Service.CompleteAsync(taken.Id, s.SupplierOrgId));
        interceptor.Failure = null;
    }

    [Fact]
    public async Task Complete_TheCallerGoesAwayRightAfterTheSave_TheLinkIsStillEmailed()
    {
        var interceptor = new FailingSaveInterceptor();
        using var s = await ServiceRequestScenario.CreateAsync(saveInterceptor: interceptor);
        await s.EnablePaymentsAsync();
        var taken = await s.TakenAsync();
        s.ForgetNotifications();
        using var caller = new CancellationTokenSource();
        interceptor.AfterSave = caller.Cancel;

        // What the host notification does with a token that is already canceled is not what is under test.
        await Record.ExceptionAsync(() => s.Service.CompleteAsync(taken.Id, s.SupplierOrgId, null, caller.Token));
        interceptor.AfterSave = null;

        // The payment was saved with a link, so the link must have been sent: a link that exists and was never sent is the
        // one thing the payer and the supplier cannot see.
        Assert.True(caller.IsCancellationRequested);
        var payment = await s.OnlyPaymentOfAsync(taken.Id);
        var link = ServicePaymentTestSupport.LinkOf(Assert.Single(s.Emails.RequestEmails()).Content);
        Assert.Equal(payment.Id, link.PaymentId);
        Assert.True(CheckoutOutcomes.TokenMatches(payment.PaymentTokenHash, link.Token));
    }

    // ─── An amount above the quote waits for the host (decision D7) ───

    [Fact]
    public async Task Complete_AmountAboveTheQuoteByMoreThanTheTolerance_CreatesNoPaymentUntilTheHostConfirms()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var completed = await s.CompletedOnlineAsync(finalAmountCents: 8_000); // 33 % above the 60,00 quote

        Assert.True(completed.FinalAmountNeedsConfirmation);
        Assert.Equal(ServiceRequestPaymentMode.Online, completed.PaymentMode);
        Assert.Empty(await s.PaymentsOfAsync(completed.Id));
        Assert.Empty(s.Emails.PaymentEmails());

        // The supplier cannot ask for a payment the host has not agreed to.
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.RequestPaymentAsync(completed.Id, s.SupplierOrgId));
        Assert.Equal(ServicePaymentErrors.AmountUnconfirmed, ex.Code);
        Assert.Empty(await s.PaymentsOfAsync(completed.Id));
    }

    [Fact]
    public async Task ConfirmFinalAmount_CreatesThePaymentWithTheConfirmedAmount_AndSendsTheLink()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var completed = await s.CompletedOnlineAsync(finalAmountCents: 8_000);
        s.ForgetNotifications();
        s.Clock.Advance(TimeSpan.FromHours(3));

        var confirmed = await s.Service.ConfirmFinalAmountAsync(completed.Id, s.HostOrgId);

        // The confirmation clears the flag every reader looks at, and keeps its trace.
        Assert.False(confirmed.FinalAmountNeedsConfirmation);
        var saved = await s.ReadAsync(completed.Id);
        Assert.False(saved.FinalAmountNeedsConfirmation);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, saved.FinalAmountConfirmedAt);
        Assert.Equal(ServiceRequestPaymentMode.Online, saved.PaymentMode);
        var payment = await s.OnlyPaymentOfAsync(completed.Id);
        Assert.Equal(8_000, payment.AmountCents);
        Assert.Equal(800, payment.ApplicationFeeCents);
        Assert.Equal(7_200, payment.NetCents);
        var email = Assert.Single(s.Emails.RequestEmails());
        Assert.Equal("host@test.com", email.To);
        Assert.Equal(payment.Id, ServicePaymentTestSupport.LinkOf(email.Content).PaymentId);
        Assert.Contains("80,00 €", email.Content.HtmlBody);
    }

    [Fact]
    public async Task ConfirmFinalAmount_Twice_IsNotAnError_AndCreatesNothingMore()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var completed = await s.CompletedOnlineAsync(finalAmountCents: 8_000);
        await s.Service.ConfirmFinalAmountAsync(completed.Id, s.HostOrgId);
        s.ForgetNotifications();

        var again = await s.Service.ConfirmFinalAmountAsync(completed.Id, s.HostOrgId);

        Assert.False(again.FinalAmountNeedsConfirmation);
        Assert.Single(await s.PaymentsOfAsync(completed.Id));
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task ConfirmFinalAmount_NothingToConfirm_Is422()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var completed = await s.CompletedOnlineAsync(); // the agreed price: no confirmation needed

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.ConfirmFinalAmountAsync(completed.Id, s.HostOrgId));

        Assert.Equal(ServicePaymentErrors.NoConfirmationNeeded, ex.Code);
        Assert.Equal("service_request_no_confirmation_needed", ex.Code);
        Assert.Equal(ServicePaymentErrors.NoConfirmationNeededMessageKey, ex.MessageKey);
    }

    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto)]
    [InlineData(ServiceRequestStatus.PresoInCarico)]
    [InlineData(ServiceRequestStatus.Pagato)]
    [InlineData(ServiceRequestStatus.Annullato)]
    public async Task ConfirmFinalAmount_ARequestThatIsNotCompleted_Is422(ServiceRequestStatus status)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedAsync(status, r => r.FinalAmountNeedsConfirmation = true);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => s.Service.ConfirmFinalAmountAsync(request.Id, s.HostOrgId));

        Assert.Equal(ServicePaymentErrors.NoConfirmationNeeded, ex.Code);
    }

    [Fact]
    public async Task ConfirmFinalAmount_ForTheRequestOfAnotherOrg_IsNotFound()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var completed = await s.CompletedOnlineAsync(finalAmountCents: 8_000);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => s.Service.ConfirmFinalAmountAsync(completed.Id, Guid.NewGuid()));

        Assert.Equal(ServiceRequestErrorCodes.NotFound, ex.Code);
        Assert.Empty(await s.PaymentsOfAsync(completed.Id));
        Assert.True((await s.ReadAsync(completed.Id)).FinalAmountNeedsConfirmation);
    }

    [Fact]
    public async Task ConfirmFinalAmount_OfAManualRequest_OnlyRecordsTheConfirmation()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync();
        var completed = await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId, new CompleteServiceRequestCommand(FinalAmountCents: 8_000));
        Assert.True(completed.FinalAmountNeedsConfirmation);
        Assert.Equal(ServiceRequestPaymentMode.Manual, completed.PaymentMode);

        var confirmed = await s.Service.ConfirmFinalAmountAsync(taken.Id, s.HostOrgId);

        Assert.False(confirmed.FinalAmountNeedsConfirmation);
        Assert.NotNull((await s.ReadAsync(taken.Id)).FinalAmountConfirmedAt);
        Assert.Empty(await s.PaymentsOfAsync(taken.Id));
        Assert.Empty(s.Emails.PaymentEmails());
    }

    [Fact]
    public async Task ConfirmFinalAmount_TheFlagTurnedOffMeanwhile_FallsBackToManual()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var completed = await s.CompletedOnlineAsync(finalAmountCents: 8_000);
        s.Flags.Set(FeatureFlags.SupplierOnlinePayments, false);

        var confirmed = await s.Service.ConfirmFinalAmountAsync(completed.Id, s.HostOrgId);

        Assert.Equal(ServiceRequestPaymentMode.Manual, confirmed.PaymentMode);
        Assert.Empty(await s.PaymentsOfAsync(completed.Id));
        Assert.Equal(ServiceRequestStatus.Pagato, (await s.Service.MarkPaidAsync(completed.Id, s.HostOrgId)).Status);
    }

    [Fact]
    public async Task ConfirmFinalAmount_TheSaveFails_NoPaymentIsLeftBehind()
    {
        var interceptor = new FailingSaveInterceptor();
        using var s = await ServiceRequestScenario.CreateAsync(saveInterceptor: interceptor);
        var completed = await s.CompletedOnlineAsync(finalAmountCents: 8_000);
        s.ForgetNotifications();

        interceptor.Failure = new DbUpdateConcurrencyException("the request changed");
        await Assert.ThrowsAsync<DomainConflictException>(() => s.Service.ConfirmFinalAmountAsync(completed.Id, s.HostOrgId));
        interceptor.Failure = null;

        Assert.Empty(await s.PaymentsOfAsync(completed.Id));
        Assert.Empty(s.Emails.Snapshot());
        Assert.DoesNotContain(s.Db.ChangeTracker.Entries<ServiceRequestPayment>(), e => e.State == EntityState.Added);
    }

    [Fact]
    public async Task ConfirmFinalAmount_AnotherCallCreatedThePaymentFirst_Is409AndNoPaymentIsLeftBehind()
    {
        // A double click, or two tabs: both read the amount as still to confirm; the loser is a conflict, never a 500.
        var interceptor = new FailingSaveInterceptor();
        using var s = await ServiceRequestScenario.CreateAsync(saveInterceptor: interceptor);
        var completed = await s.CompletedOnlineAsync(finalAmountCents: 8_000);
        s.ForgetNotifications();

        interceptor.Failure = new DbUpdateException(
            "save failed",
            new PostgresException(
                "duplicate key value violates unique constraint", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation,
                constraintName: ServiceCharges.LivePaymentIndexName));
        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => s.Service.ConfirmFinalAmountAsync(completed.Id, s.HostOrgId));
        interceptor.Failure = null;

        Assert.Equal(ServiceRequestErrorCodes.StateChanged, ex.Code);
        Assert.Empty(await s.PaymentsOfAsync(completed.Id));
        Assert.Empty(s.Emails.Snapshot());
        Assert.DoesNotContain(s.Db.ChangeTracker.Entries<ServiceRequestPayment>(), e => e.State == EntityState.Added);
    }
}
