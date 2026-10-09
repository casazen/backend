using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Stripe;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-15a: the payer's side. The page of the link (<see cref="ISupplierPaymentService.GetPublicAsync"/>) and the Stripe session
/// (<see cref="ISupplierPaymentService.CreatePublicSessionAsync"/>, and the host's own without the link): the token is the only
/// access check and a wrong id, a wrong token and an expired link are the same 404; the PaymentIntent is a direct charge on the
/// supplier's account with the commission snapshotted, created when the payer opens the session, reused while it is still right,
/// canceled and made again when the amount, the commission or the account changed, and never created twice for a payment that
/// is paid or in progress. The races (two sessions together) need PostgreSQL and are in <c>ServicePaymentsPostgresTests</c>.
/// </summary>
public class SupplierPaymentSessionTests
{
    // ─── The page of the link ───

    [Fact]
    public async Task GetPublic_TheLink_ShowsWhoAsksForWhatAndHowMuch_AndNothingAboutTheCommission()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (payment, token) = await CompletedOnlineAsync(s);

        var page = await s.Payments.GetPublicAsync(payment.Id, token);

        Assert.Equal(payment.Id, page.Id);
        Assert.Equal("Supplier Org", page.SupplierName);
        Assert.Equal(ServiceRequestScenario.ServiceName, page.ServiceName);
        Assert.Equal(ServiceRequestScenario.PropertyName, page.PropertyName);
        Assert.Equal(6_000, page.AmountCents);
        Assert.Equal("EUR", page.Currency);
        Assert.Equal(PublicServicePaymentState.Payable, page.State);
        Assert.False(page.LastAttemptFailed);
        Assert.NotNull(page.CompletedAt);
        var line = Assert.Single(page.Lines);
        Assert.Equal(6_000, line.AmountCents);
        // The link is valid for 30 days from the email.
        Assert.Equal(ServiceRequestScenario.Instant.UtcDateTime.AddDays(30), page.ValidUntil);
        // The payer never sees the commission, a name, an address or a contact: the type has no such member.
        var members = typeof(PublicServicePayment).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToList();
        Assert.DoesNotContain(members, name => name.Contains("Fee", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Commission", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Net", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Email", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Phone", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Address", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Token", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Account", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetPublic_AWrongIdAWrongTokenAnEmptyTokenAndAnotherPaymentsToken_AreTheSame404()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (payment, token) = await CompletedOnlineAsync(s);
        var other = await s.CompletedOnlineAsync();
        var otherPayment = await s.OnlyPaymentOfAsync(other.Id);

        var wrongToken = await Assert.ThrowsAsync<NotFoundException>(() => s.Payments.GetPublicAsync(payment.Id, token + "x"));
        var wrongId = await Assert.ThrowsAsync<NotFoundException>(() => s.Payments.GetPublicAsync(Guid.NewGuid(), token));
        var emptyToken = await Assert.ThrowsAsync<NotFoundException>(() => s.Payments.GetPublicAsync(payment.Id, string.Empty));
        var oversized = await Assert.ThrowsAsync<NotFoundException>(() => s.Payments.GetPublicAsync(payment.Id, new string('a', 129)));
        var crossed = await Assert.ThrowsAsync<NotFoundException>(() => s.Payments.GetPublicAsync(otherPayment.Id, token));

        foreach (var ex in new[] { wrongToken, wrongId, emptyToken, oversized, crossed })
        {
            // Nothing tells a payment that exists from one that does not.
            Assert.Equal(ServicePaymentErrors.LinkInvalid, ex.Code);
            Assert.Equal("service_payment_link_invalid", ex.Code);
            Assert.Equal(ServicePaymentErrors.LinkInvalidMessageKey, ex.MessageKey);
        }
    }

    [Fact]
    public async Task GetPublic_APendingPayment_HasNoLinkToOpen()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnablePaymentsAsync();
        var taken = await s.TakenAsync();
        await s.ConnectSupplierAsync(ready: false);
        var completed = await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId);
        var payment = await s.OnlyPaymentOfAsync(completed.Id);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => s.Payments.GetPublicAsync(payment.Id, "anything"));

        Assert.Equal(ServicePaymentErrors.LinkInvalid, ex.Code);
    }

    [Fact]
    public async Task GetPublic_ALinkPastItsValidity_IsTheSame404_ForAPaymentStillToBeMade()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (payment, token) = await CompletedOnlineAsync(s);

        s.Clock.Advance(TimeSpan.FromDays(30).Subtract(TimeSpan.FromSeconds(1)));
        Assert.Equal(PublicServicePaymentState.Payable, (await s.Payments.GetPublicAsync(payment.Id, token)).State);

        s.Clock.Advance(TimeSpan.FromSeconds(1));
        var expired = await Assert.ThrowsAsync<NotFoundException>(() => s.Payments.GetPublicAsync(payment.Id, token));
        var session = await Assert.ThrowsAsync<NotFoundException>(() => s.Payments.CreatePublicSessionAsync(payment.Id, token));

        Assert.Equal(ServicePaymentErrors.LinkInvalid, expired.Code);
        Assert.Equal(ServicePaymentErrors.LinkInvalid, session.Code);
        Assert.Empty(s.Gateway.Created);
    }

    [Fact]
    public async Task GetPublic_ALinkPastItsValidity_StillShowsAPaymentThatIsPaid()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (payment, token) = await CompletedOnlineAsync(s);
        await SupplierPaymentRequestTests.ChangePaymentAsync(s, payment.ServiceRequestId, p =>
        {
            p.Status = ServicePaymentStatus.Paid;
            p.PaidAt = ServiceRequestScenario.Instant.UtcDateTime;
            p.PaidVia = ServicePaymentChannel.Stripe;
        });
        s.Clock.Advance(TimeSpan.FromDays(90));

        var page = await s.Payments.GetPublicAsync(payment.Id, token);

        Assert.Equal(PublicServicePaymentState.Paid, page.State);
        Assert.Null(page.ValidUntil);
    }

    [Theory]
    [InlineData(ServicePaymentStatus.Requested, PublicServicePaymentState.Payable, false)]
    [InlineData(ServicePaymentStatus.Failed, PublicServicePaymentState.Payable, true)]
    [InlineData(ServicePaymentStatus.Processing, PublicServicePaymentState.Processing, false)]
    [InlineData(ServicePaymentStatus.NeedsReview, PublicServicePaymentState.Processing, false)]
    [InlineData(ServicePaymentStatus.Paid, PublicServicePaymentState.Paid, false)]
    [InlineData(ServicePaymentStatus.PartiallyRefunded, PublicServicePaymentState.Paid, false)]
    [InlineData(ServicePaymentStatus.Refunded, PublicServicePaymentState.Paid, false)]
    [InlineData(ServicePaymentStatus.Canceled, PublicServicePaymentState.Unavailable, false)]
    public async Task GetPublic_TheStateOfThePayment_IsWhatThePayerSees(ServicePaymentStatus status, PublicServicePaymentState expected, bool failed)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (payment, token) = await CompletedOnlineAsync(s);
        await SupplierPaymentRequestTests.ChangePaymentAsync(s, payment.ServiceRequestId, p =>
        {
            p.Status = status;
            p.PaidAt = ServiceRequestScenario.Instant.UtcDateTime;
            p.PaidVia = ServicePaymentChannel.Stripe;
        });

        var page = await s.Payments.GetPublicAsync(payment.Id, token);

        Assert.Equal(expected, page.State);
        Assert.Equal(failed, page.LastAttemptFailed);
    }

    [Fact]
    public async Task GetPublic_TheSupplierCannotBePaidNow_IsUnavailable()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (payment, token) = await CompletedOnlineAsync(s);
        await s.ConnectSupplierAsync(ready: false);

        var page = await s.Payments.GetPublicAsync(payment.Id, token);

        Assert.Equal(PublicServicePaymentState.Unavailable, page.State);
        Assert.Null(page.ValidUntil);
    }

    // ─── The session: a direct charge with the commission ───

    [Fact]
    public async Task CreatePublicSession_CreatesTheDirectChargeOnTheSupplierAccountWithTheSnapshotCommission()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (payment, token) = await CompletedOnlineAsync(s);

        var session = await s.Payments.CreatePublicSessionAsync(payment.Id, token);

        var request = Assert.Single(s.Gateway.Created);
        Assert.Equal(ServiceRequestScenario.SupplierAccountId, request.ConnectedAccountId);
        Assert.Equal(6_000, request.AmountCents);
        Assert.Equal("eur", request.Currency);
        Assert.Equal(600, request.ApplicationFeeCents);
        Assert.Equal(ServiceCharges.CreationIdempotencyKey(payment.Id, 1), request.IdempotencyKey);
        Assert.Equal($"service-charge:{payment.Id:N}:1", request.IdempotencyKey);
        Assert.Equal(payment.Id.ToString(), request.Metadata[ServiceCharges.PaymentMetadataKey]);
        Assert.Equal(payment.ServiceRequestId.ToString(), request.Metadata[ServiceCharges.RequestMetadataKey]);
        Assert.Equal(s.SupplierOrgId.ToString(), request.Metadata[ServiceCharges.SupplierMetadataKey]);
        // No name, address or email goes to Stripe: ids only.
        Assert.DoesNotContain(request.Metadata.Values, value => value.Contains('@') || value.Contains("Casa"));
        Assert.DoesNotContain("Casa", request.Description ?? string.Empty);

        var intent = Assert.Single(s.Gateway.Intents);
        Assert.Equal(intent.ClientSecret, session.ClientSecret);
        Assert.Equal(ServiceRequestTestKit.PublishableKey, session.PublishableKey);
        Assert.Equal(ServiceRequestScenario.SupplierAccountId, session.StripeAccountId);
        Assert.Equal(payment.Id, session.PaymentId);
        Assert.Equal(6_000, session.AmountCents);
        Assert.Equal("EUR", session.Currency);

        // The payment keeps the PaymentIntent and the account it was created on; it is still waiting for the payer.
        var saved = await s.OnlyPaymentOfAsync(payment.ServiceRequestId);
        Assert.Equal(ServicePaymentStatus.Requested, saved.Status);
        Assert.Equal(intent.Id, saved.StripePaymentIntentId);
        Assert.Equal(ServiceRequestScenario.SupplierAccountId, saved.ConnectedAccountId);
        Assert.Equal(1, saved.PaymentIntentCount);
        Assert.Equal(600, intent.ApplicationFeeCents);
    }

    [Fact]
    public async Task CreatePublicSession_AGainWithTheSameAmount_ReusesThePaymentIntent()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (payment, token) = await CompletedOnlineAsync(s);

        var first = await s.Payments.CreatePublicSessionAsync(payment.Id, token);
        var second = await s.Payments.CreatePublicSessionAsync(payment.Id, token);

        Assert.Single(s.Gateway.Created);
        Assert.Equal(first.ClientSecret, second.ClientSecret);
        Assert.Equal(1, (await s.OnlyPaymentOfAsync(payment.ServiceRequestId)).PaymentIntentCount);
        Assert.Empty(s.Gateway.Canceled);
    }

    [Fact]
    public async Task CreatePublicSession_AFeeThatIsZeroOrNotBelowTheAmount_IsNeverSent()
    {
        using var s = await ServiceRequestScenario.CreateAsync(paymentOptions: new SupplierPaymentsOptions { CommissionPercent = 0m });
        var (payment, token) = await CompletedOnlineAsync(s);

        await s.Payments.CreatePublicSessionAsync(payment.Id, token);

        // Null, not 0: the parameter is left out of the request (A3-40).
        Assert.Null(Assert.Single(s.Gateway.Created).ApplicationFeeCents);
        Assert.Null(Assert.Single(s.Gateway.Intents).ApplicationFeeCents);
    }

    [Fact]
    public async Task CreatePublicSession_TheAmountChanged_CancelsTheOldPaymentIntentAndMakesANewOne()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (payment, token) = await CompletedOnlineAsync(s);
        await s.Payments.CreatePublicSessionAsync(payment.Id, token);
        var firstIntent = Assert.Single(s.Gateway.Intents).Id;
        await SupplierPaymentRequestTests.ChangePaymentAsync(s, payment.ServiceRequestId, p =>
        {
            p.AmountCents = 7_000;
            p.ApplicationFeeCents = 700;
            p.NetCents = 6_300;
        });

        var session = await s.Payments.CreatePublicSessionAsync(payment.Id, token);

        // The old one is canceled (it could still be paid for the old amount) and a new one is created with the next key.
        var canceled = Assert.Single(s.Gateway.Canceled);
        Assert.Equal(firstIntent, canceled.PaymentIntentId);
        Assert.Equal(ServiceCharges.CancellationIdempotencyKey(payment.Id, firstIntent), canceled.IdempotencyKey);
        Assert.Equal(2, s.Gateway.Created.Count);
        Assert.Equal(7_000, s.Gateway.Created[1].AmountCents);
        Assert.Equal(700, s.Gateway.Created[1].ApplicationFeeCents);
        Assert.Equal(ServiceCharges.CreationIdempotencyKey(payment.Id, 2), s.Gateway.Created[1].IdempotencyKey);
        Assert.Equal(7_000, session.AmountCents);
        var saved = await s.OnlyPaymentOfAsync(payment.ServiceRequestId);
        Assert.Equal(2, saved.PaymentIntentCount);
        Assert.NotEqual(firstIntent, saved.StripePaymentIntentId);
        Assert.Equal("canceled", s.Gateway.Intents.Single(i => i.Id == firstIntent).Status);
    }

    [Fact]
    public async Task CreatePublicSession_TheCommissionChanged_CancelsTheOldPaymentIntentAndMakesANewOne()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (payment, token) = await CompletedOnlineAsync(s);
        await s.Payments.CreatePublicSessionAsync(payment.Id, token);
        await SupplierPaymentRequestTests.ChangePaymentAsync(s, payment.ServiceRequestId, p =>
        {
            p.CommissionPercent = 0m;
            p.ApplicationFeeCents = 0;
            p.NetCents = p.AmountCents;
        });

        await s.Payments.CreatePublicSessionAsync(payment.Id, token);

        Assert.Single(s.Gateway.Canceled);
        Assert.Equal(2, s.Gateway.Created.Count);
        Assert.Null(s.Gateway.Created[1].ApplicationFeeCents);
    }

    [Fact]
    public async Task CreatePublicSession_TheSupplierReplacedItsStripeAccount_ChargesOnTheNewOneAndCancelsTheOld()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (payment, token) = await CompletedOnlineAsync(s);
        await s.Payments.CreatePublicSessionAsync(payment.Id, token);
        var oldIntent = Assert.Single(s.Gateway.Intents).Id;
        await s.ConnectSupplierAsync(ready: true, accountId: "acct_supplier_new");

        var session = await s.Payments.CreatePublicSessionAsync(payment.Id, token);

        var canceled = Assert.Single(s.Gateway.Canceled);
        Assert.Equal(oldIntent, canceled.PaymentIntentId);
        Assert.Equal(ServiceRequestScenario.SupplierAccountId, canceled.AccountId);
        Assert.Equal("acct_supplier_new", s.Gateway.Created[1].ConnectedAccountId);
        Assert.Equal("acct_supplier_new", session.StripeAccountId);
        Assert.Equal("acct_supplier_new", (await s.OnlyPaymentOfAsync(payment.ServiceRequestId)).ConnectedAccountId);
    }

    [Fact]
    public async Task CreatePublicSession_TheOldAccountWasDeleted_ANewPaymentIntentIsCreatedOnTheCurrentAccount()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (payment, token) = await CompletedOnlineAsync(s);
        await s.Payments.CreatePublicSessionAsync(payment.Id, token);
        var oldIntent = Assert.Single(s.Gateway.Intents).Id;
        // SP-14 replaces a supplier's Stripe account only when Stripe says the old one is gone: its PaymentIntent cannot be read.
        s.Gateway.DeleteAccount(ServiceRequestScenario.SupplierAccountId);
        await s.ConnectSupplierAsync(ready: true, accountId: "acct_supplier_new");

        var session = await s.Payments.CreatePublicSessionAsync(payment.Id, token);

        // Nothing to cancel on an account that does not exist; the payer is not left with a payment that can never be paid.
        Assert.Empty(s.Gateway.Canceled);
        Assert.Equal(2, s.Gateway.Created.Count);
        Assert.Equal("acct_supplier_new", s.Gateway.Created[1].ConnectedAccountId);
        Assert.Equal("acct_supplier_new", session.StripeAccountId);
        var saved = await s.OnlyPaymentOfAsync(payment.ServiceRequestId);
        Assert.Equal(ServicePaymentStatus.Requested, saved.Status);
        Assert.Equal("acct_supplier_new", saved.ConnectedAccountId);
        Assert.NotEqual(oldIntent, saved.StripePaymentIntentId);
        Assert.Equal(2, saved.PaymentIntentCount);
    }

    [Fact]
    public async Task CreatePublicSession_StripeFailsReadingThePaymentIntent_IsNotTakenForAGoneOne()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (payment, token) = await CompletedOnlineAsync(s);
        await s.Payments.CreatePublicSessionAsync(payment.Id, token);
        var firstIntent = Assert.Single(s.Gateway.Intents).Id;
        s.Gateway.FailNextGet(new StripeException(System.Net.HttpStatusCode.ServiceUnavailable, new StripeError { Type = "api_error" }, "down"));

        // Only a certain "gone" (resource_missing, account_invalid) lets a new PaymentIntent be made: an outage is a 503 and the
        // payment keeps the one it has, which the payer may be paying right now.
        await Assert.ThrowsAsync<StripeException>(() => s.Payments.CreatePublicSessionAsync(payment.Id, token));
        s.Db.ChangeTracker.Clear();

        Assert.Single(s.Gateway.Created);
        Assert.Equal(firstIntent, (await s.OnlyPaymentOfAsync(payment.ServiceRequestId)).StripePaymentIntentId);
    }

    [Theory]
    [InlineData("succeeded")]
    [InlineData("processing")]
    [InlineData("requires_capture")]
    public async Task CreatePublicSession_APaymentIntentPaidOrInProgress_IsNeverCreatedAgain(string stripeStatus)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (payment, token) = await CompletedOnlineAsync(s);
        await s.Payments.CreatePublicSessionAsync(payment.Id, token);
        s.Gateway.SetStatus(Assert.Single(s.Gateway.Intents).Id, stripeStatus);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => s.Payments.CreatePublicSessionAsync(payment.Id, token));

        Assert.Equal(ServicePaymentErrors.InFlight, ex.Code);
        Assert.Single(s.Gateway.Created);
        Assert.Empty(s.Gateway.Canceled);
        // The payment is shown as in progress while the webhook (SP-15b) settles it; the payer is not offered a second charge.
        var saved = await s.OnlyPaymentOfAsync(payment.ServiceRequestId);
        Assert.Equal(ServicePaymentStatus.Processing, saved.Status);
        var page = await s.Payments.GetPublicAsync(payment.Id, token);
        Assert.Equal(PublicServicePaymentState.Processing, page.State);
    }

    [Fact]
    public async Task CreatePublicSession_ThePaymentIntentWasCanceledOutsideCasaZen_ANewOneIsCreated()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (payment, token) = await CompletedOnlineAsync(s);
        await s.Payments.CreatePublicSessionAsync(payment.Id, token);
        var firstIntent = Assert.Single(s.Gateway.Intents).Id;
        s.Gateway.SetStatus(firstIntent, "canceled");

        var session = await s.Payments.CreatePublicSessionAsync(payment.Id, token);

        Assert.Equal(2, s.Gateway.Created.Count);
        Assert.Empty(s.Gateway.Canceled); // already canceled: nothing to cancel
        var saved = await s.OnlyPaymentOfAsync(payment.ServiceRequestId);
        Assert.Equal(2, saved.PaymentIntentCount);
        Assert.NotEqual(firstIntent, saved.StripePaymentIntentId);
        Assert.Equal(s.Gateway.Intents.Single(i => i.Id == saved.StripePaymentIntentId).ClientSecret, session.ClientSecret);
    }

    [Fact]
    public async Task CreatePublicSession_ThePreviousAttemptFailed_ThePayerCanTryAgainWithTheSamePaymentIntent()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (payment, token) = await CompletedOnlineAsync(s);
        var first = await s.Payments.CreatePublicSessionAsync(payment.Id, token);
        // The card was declined: Stripe leaves the PaymentIntent payable (requires_payment_method) with the error.
        s.Gateway.SetStatus(Assert.Single(s.Gateway.Intents).Id, "requires_payment_method", "card_declined");
        await SupplierPaymentRequestTests.ChangePaymentAsync(s, payment.ServiceRequestId, p =>
        {
            p.Status = ServicePaymentStatus.Failed;
            p.FailureCode = "card_declined";
        });

        var again = await s.Payments.CreatePublicSessionAsync(payment.Id, token);

        Assert.Equal(first.ClientSecret, again.ClientSecret);
        Assert.Single(s.Gateway.Created);
        Assert.Equal(ServicePaymentStatus.Failed, (await s.OnlyPaymentOfAsync(payment.ServiceRequestId)).Status);
    }

    [Fact]
    public async Task CreatePublicSession_TheSupplierCannotBePaidNow_Is409AndStripeIsNotCalled()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (payment, token) = await CompletedOnlineAsync(s);
        await s.ConnectSupplierAsync(ready: false);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => s.Payments.CreatePublicSessionAsync(payment.Id, token));

        Assert.Equal(ServicePaymentErrors.SupplierNotReady, ex.Code);
        Assert.Empty(s.Gateway.Created);
        Assert.Equal(0, s.Gateway.GetCount);
        Assert.Equal(0, (await s.OnlyPaymentOfAsync(payment.ServiceRequestId)).PaymentIntentCount);
    }

    [Theory]
    [InlineData(ServicePaymentStatus.Paid, "service_payment_not_payable", "ServicePaymentAlreadyPaid")]
    [InlineData(ServicePaymentStatus.PartiallyRefunded, "service_payment_not_payable", "ServicePaymentAlreadyPaid")]
    [InlineData(ServicePaymentStatus.Refunded, "service_payment_not_payable", "ServicePaymentAlreadyPaid")]
    [InlineData(ServicePaymentStatus.Canceled, "service_payment_not_payable", "ServicePaymentNotPayable")]
    [InlineData(ServicePaymentStatus.Processing, "service_payment_in_flight", "ServicePaymentInFlight")]
    [InlineData(ServicePaymentStatus.NeedsReview, "service_payment_in_flight", "ServicePaymentInFlight")]
    public async Task CreatePublicSession_APaymentThatCannotBePaid_Is409WithItsOwnMessage_AndStripeIsNotCalled(
        ServicePaymentStatus status, string code, string key)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (payment, token) = await CompletedOnlineAsync(s);
        await SupplierPaymentRequestTests.ChangePaymentAsync(s, payment.ServiceRequestId, p =>
        {
            p.Status = status;
            p.PaidAt = ServiceRequestScenario.Instant.UtcDateTime;
            p.PaidVia = ServicePaymentChannel.Stripe;
        });

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => s.Payments.CreatePublicSessionAsync(payment.Id, token));

        Assert.Equal(code, ex.Code);
        Assert.Equal(key, ex.MessageKey);
        Assert.Empty(s.Gateway.Created);
        Assert.Equal(0, s.Gateway.GetCount);
    }

    [Fact]
    public async Task CreatePublicSession_AWrongToken_IsThe404_AndStripeIsNotCalled()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (payment, _) = await CompletedOnlineAsync(s);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => s.Payments.CreatePublicSessionAsync(payment.Id, "not-the-token"));

        Assert.Equal(ServicePaymentErrors.LinkInvalid, ex.Code);
        Assert.Empty(s.Gateway.Created);
        Assert.Equal(0, s.Gateway.GetCount);
    }

    [Fact]
    public async Task CreatePublicSession_TheLinkIsReplacedByANewOne_TheOldTokenNoLongerOpensASession()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (payment, oldToken) = await CompletedOnlineAsync(s);
        s.Clock.Advance(TimeSpan.FromHours(24));
        await s.Service.RequestPaymentAsync(payment.ServiceRequestId, s.SupplierOrgId);
        var newToken = ServicePaymentTestSupport.LinkOf(Assert.Single(s.Emails.ReminderEmails()).Content).Token;

        await Assert.ThrowsAsync<NotFoundException>(() => s.Payments.CreatePublicSessionAsync(payment.Id, oldToken));
        var session = await s.Payments.CreatePublicSessionAsync(payment.Id, newToken);

        Assert.Equal(payment.Id, session.PaymentId);
        Assert.Single(s.Gateway.Created);
    }

    [Fact]
    public async Task CreatePublicSession_StripeRefuses_NothingIsSavedAndTheNextAttemptUsesTheSameKey()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (payment, token) = await CompletedOnlineAsync(s);
        s.Gateway.FailNextCreate(new StripeException(System.Net.HttpStatusCode.ServiceUnavailable, new StripeError { Type = "api_error" }, "down"));

        await Assert.ThrowsAsync<StripeException>(() => s.Payments.CreatePublicSessionAsync(payment.Id, token));
        // The count was not kept: the PaymentIntent did not exist, so the next attempt is still the first one.
        var afterFailure = await s.OnlyPaymentOfAsync(payment.ServiceRequestId);
        Assert.Equal(0, afterFailure.PaymentIntentCount);
        Assert.Null(afterFailure.StripePaymentIntentId);
        s.Db.ChangeTracker.Clear();

        var session = await s.Payments.CreatePublicSessionAsync(payment.Id, token);

        Assert.Equal(2, s.Gateway.Created.Count);
        Assert.Equal(s.Gateway.Created[0].IdempotencyKey, s.Gateway.Created[1].IdempotencyKey);
        Assert.Equal(1, (await s.OnlyPaymentOfAsync(payment.ServiceRequestId)).PaymentIntentCount);
        Assert.False(string.IsNullOrEmpty(session.ClientSecret));
    }

    [Fact]
    public async Task CreatePublicSession_ARetryWithTheSameKey_ReturnsTheSamePaymentIntentAtStripe()
    {
        // The idempotency key is what makes a retry after a lost answer safe: two creations with it are one PaymentIntent.
        var gateway = new FakeSupplierPaymentGateway();
        var request = new ServiceChargeIntentRequest(
            "acct_x", 6_000, "eur", 600, new Dictionary<string, string>(), ServiceCharges.CreationIdempotencyKey(Guid.NewGuid(), 1), null);

        var first = await gateway.CreateAsync(request);
        var second = await gateway.CreateAsync(request);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(gateway.Intents);
    }

    // ─── The host's own session ───

    [Fact]
    public async Task CreateHostSession_TheHostThatPays_GetsTheSameSessionWithoutTheLink()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (payment, _) = await CompletedOnlineAsync(s);

        var session = await s.Payments.CreateHostSessionAsync(payment.ServiceRequestId, s.HostOrgId);

        Assert.Equal(payment.Id, session.PaymentId);
        var request = Assert.Single(s.Gateway.Created);
        Assert.Equal(600, request.ApplicationFeeCents);
        Assert.Equal(ServiceRequestScenario.SupplierAccountId, session.StripeAccountId);
        // The same PaymentIntent whoever opens it: the link and the console never make two payable.
        var token = ServicePaymentTestSupport.LinkOf(Assert.Single(s.Emails.RequestEmails()).Content).Token;
        var viaLink = await s.Payments.CreatePublicSessionAsync(payment.Id, token);
        Assert.Equal(session.ClientSecret, viaLink.ClientSecret);
        Assert.Single(s.Gateway.Created);
    }

    [Fact]
    public async Task CreateHostSession_WorksEvenWhenThePaymentWasNeverSent_ThePaymentIsTheHostsToPay()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnablePaymentsAsync();
        var taken = await s.TakenAsync();
        s.Emails.Refuse = true;
        var completed = await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId);
        var pending = await s.OnlyPaymentOfAsync(completed.Id);
        Assert.Null(pending.PaymentTokenHash);

        var session = await s.Payments.CreateHostSessionAsync(completed.Id, s.HostOrgId);

        Assert.Equal(pending.Id, session.PaymentId);
    }

    [Fact]
    public async Task CreateHostSession_AnotherOrg_ARequestWithoutAPayment_AndACanceledPayment_AreNotFound()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (payment, _) = await CompletedOnlineAsync(s);
        var taken = await s.TakenAsync(); // taken, not completed: no payment

        var otherOrg = await Assert.ThrowsAsync<NotFoundException>(() => s.Payments.CreateHostSessionAsync(payment.ServiceRequestId, Guid.NewGuid()));
        var noPayment = await Assert.ThrowsAsync<NotFoundException>(() => s.Payments.CreateHostSessionAsync(taken.Id, s.HostOrgId));
        await SupplierPaymentRequestTests.ChangePaymentAsync(s, payment.ServiceRequestId, p => p.Status = ServicePaymentStatus.Canceled);
        var canceled = await Assert.ThrowsAsync<NotFoundException>(() => s.Payments.CreateHostSessionAsync(payment.ServiceRequestId, s.HostOrgId));

        Assert.All(new[] { otherOrg, noPayment, canceled }, ex => Assert.Equal(ServicePaymentErrors.NotFound, ex.Code));
        Assert.Empty(s.Gateway.Created);
    }

    [Fact]
    public async Task CreateHostSession_APaymentThatIsPaid_Is409()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (payment, _) = await CompletedOnlineAsync(s);
        await SupplierPaymentRequestTests.ChangePaymentAsync(s, payment.ServiceRequestId, p =>
        {
            p.Status = ServicePaymentStatus.Paid;
            p.PaidAt = ServiceRequestScenario.Instant.UtcDateTime;
            p.PaidVia = ServicePaymentChannel.Stripe;
        });

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => s.Payments.CreateHostSessionAsync(payment.ServiceRequestId, s.HostOrgId));

        Assert.Equal(ServicePaymentErrors.AlreadyPaidMessageKey, ex.MessageKey);
    }

    /// <summary>A request completed inside CasaZen, and the payment with the raw token of the link the host was sent.</summary>
    private static async Task<(ServiceRequestPayment Payment, string Token)> CompletedOnlineAsync(ServiceRequestScenario s)
    {
        var completed = await s.CompletedOnlineAsync();
        var payment = await s.OnlyPaymentOfAsync(completed.Id);
        var link = ServicePaymentTestSupport.LinkOf(Assert.Single(s.Emails.RequestEmails()).Content);
        Assert.Equal(payment.Id, link.PaymentId);
        s.Db.ChangeTracker.Clear();
        return (payment, link.Token);
    }
}
