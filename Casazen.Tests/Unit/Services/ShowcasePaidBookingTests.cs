using System.Net;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-15c-a (decision VT-D1): the booking from a supplier's public showcase that is paid inside CasaZen, on the real services over the in-memory
/// database and a <see cref="FakePlatformPaymentGateway"/> (Stripe is never reached). One test per rule where a mistake costs money: the
/// customer's card is blocked for the total <b>and the supplier is told nothing</b> until the e-mail is checked <b>and</b> the block succeeded, in
/// either order, creating <b>one</b> request with <b>one</b> payment; a block that is not the total creates nothing; a hold that lapses releases its
/// block, and a hold that became a request never has its block touched; the supplier's acceptance charges the card <b>before</b> the request is
/// taken, and a card that cannot be charged leaves the request new; the supplier cannot change the price; the customer's cancellation releases the
/// block; "all fine" pays the supplier once, the net of the commission; a request whose block is gone does not wait for an answer.
/// </summary>
public class ShowcasePaidBookingTests
{
    private const int Total = ServiceRequestScenario.ServicePriceCents; // 6_000: the commission of 10 % is 600, the net 5_400
    private const int Commission = 600;
    private const int Net = 5_400;

    private static async Task<(ServiceRequestScenario S, SupplierProfile Supplier)> StartAsync()
    {
        var s = await ServiceRequestScenario.CreateAsync();
        await s.EnablePaymentsAsync();
        var supplier = await s.EnableBookingAsync();
        return (s, supplier);
    }

    /// <summary>The customer confirmed the card in the browser: Stripe reports the block and the webhook hands it over.</summary>
    private static ShowcaseHoldAuthorization CardBlocked(ServiceRequestScenario s, ShowcaseBookingHold hold, long? capturableCents = null)
    {
        var charge = s.Kit.PlatformGateway.SetStatus(hold.StripePaymentIntentId!, "requires_capture");
        return new ShowcaseHoldAuthorization(
            hold.OrgId,
            hold.Id,
            new HeldChargeObservation(
                charge.Id,
                null,
                charge.AmountCents,
                capturableCents ?? charge.AmountCapturableCents,
                charge.AmountReceivedCents,
                charge.Currency,
                charge.ApplicationFeeCents),
            s.Clock.GetUtcNow().UtcDateTime);
    }

    /// <summary>A booking whose card is blocked and whose e-mail is checked (the block first): the request, new, with its payment.</summary>
    private static async Task<(ServiceRequest Request, ShowcaseBookingHold Hold)> BookedAndPaidAsync(ServiceRequestScenario s, SupplierProfile supplier)
    {
        var (held, token) = await s.HoldAsync(supplier: supplier);
        var hold = Assert.Single(await s.HoldsAsync());
        await s.Kit.Booking.ApplyHoldAuthorizationAsync(CardBlocked(s, hold));
        await s.Kit.Booking.ConfirmEmailAsync(supplier, held.Id, token);
        var request = Assert.Single(await s.ShowcaseRequestsAsync());
        return (request, hold);
    }

    private static void AssertAuthorizedPayment(ServiceRequestPayment payment, ShowcaseBookingHold hold)
    {
        Assert.Equal(ServicePaymentStatus.Authorized, payment.Status);
        Assert.Equal(ServiceFundsModel.PlatformHeld, payment.FundsModel);
        Assert.Equal(Total, payment.AmountCents);
        Assert.Equal(Commission, payment.ApplicationFeeCents); // the commission CasaZen keeps: never sent to Stripe as a fee
        Assert.Equal(Net, payment.NetCents);
        Assert.Equal(ServicePayerKind.Private, payment.PayerKind);
        Assert.Null(payment.PayerOrgId);
        Assert.Null(payment.ConnectedAccountId);
        Assert.Equal(hold.StripePaymentIntentId, payment.StripePaymentIntentId);
        Assert.Equal(ServiceCharges.HoldTransferGroup(hold.Id), payment.TransferGroup);
        Assert.NotNull(payment.AuthorizedAt);
        Assert.Null(payment.CapturedAt);
    }

    // ─── The card is blocked for the total, and the supplier is told nothing ───

    [Fact]
    public async Task Hold_OfABookingPaidInsideCasaZen_BlocksTheCardForTheTotal_AndNothingReachesTheSupplier()
    {
        var (s, supplier) = await StartAsync();
        using var scope = s;

        var (hold, _) = await s.HoldAsync(supplier: supplier);

        Assert.NotNull(hold.Payment);
        var payment = hold.Payment!;
        Assert.Equal(Total, payment.AmountCents);
        Assert.Equal("EUR", payment.Currency);
        Assert.False(payment.Authorized);
        Assert.False(string.IsNullOrEmpty(payment.ClientSecret));
        Assert.Equal(ServiceRequestTestKit.PublishableKey, payment.PublishableKey);

        // On the platform, charged later (manual capture), for the total, with the transfer group of the hold: the same hold is the same block.
        var created = Assert.Single(s.Kit.PlatformGateway.Created);
        Assert.True(created.ManualCapture);
        Assert.Equal(Total, created.AmountCents);
        Assert.Equal(ServiceCharges.HoldTransferGroup(hold.Id), created.TransferGroup);
        Assert.Equal(ServiceCharges.HoldAuthorizationIdempotencyKey(hold.Id), created.IdempotencyKey);

        // The hold remembers the block and its amount; there is no request, no payment and no mail to the supplier.
        var row = Assert.Single(await s.HoldsAsync());
        Assert.NotNull(row.StripePaymentIntentId);
        Assert.Equal(Total, row.PaymentAmountCents);
        Assert.Null(row.PaymentAuthorizedAt);
        Assert.Empty(await s.ShowcaseRequestsAsync());
        Assert.Empty(s.Db.ServiceRequestPayments);
        Assert.DoesNotContain(s.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.SupplierBookingNewRequest);
    }

    [Fact]
    public async Task Hold_WithThePaymentsOff_IsMadeAsItAlwaysWas_NoCardIsAskedFor()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();

        var (hold, token) = await s.HoldAsync(supplier: supplier);
        var confirmation = await s.Kit.Booking.ConfirmEmailAsync(supplier, hold.Id, token);

        Assert.Null(hold.Payment);
        Assert.Empty(s.Kit.PlatformGateway.Created);
        Assert.False(confirmation.AwaitingPayment);
        var request = Assert.Single(await s.ShowcaseRequestsAsync());
        Assert.Equal(ServiceRequestPaymentMode.Manual, request.PaymentMode);
        Assert.Empty(s.Db.ServiceRequestPayments);
    }

    [Fact]
    public async Task Hold_WhenTheSameBookingIsSentAgain_AnswersTheSameBlock_AndAsksStripeForNoSecondOne()
    {
        var (s, supplier) = await StartAsync();
        using var scope = s;
        var input = await s.InputAsync();

        var (first, _) = await s.HoldAsync(input, supplier);
        var again = await s.Kit.Booking.CreateHoldAsync(supplier, input); // a retry after a lost answer: no second e-mail either

        Assert.Equal(first.Id, again.Id);
        Assert.NotNull(again.Payment);
        Assert.False(string.IsNullOrEmpty(again.Payment!.ClientSecret)); // the card can still be asked for it
        Assert.Single(s.Kit.PlatformGateway.Created);
        Assert.Single(await s.HoldsAsync());
    }

    [Fact]
    public async Task Hold_WhenStripeCannotBlockTheCard_TheHoldGoesAndNoEmailIsSent()
    {
        var (s, supplier) = await StartAsync();
        using var scope = s;
        s.Kit.PlatformGateway.FailNextCreate(new StripeException(HttpStatusCode.ServiceUnavailable, new StripeError { Type = "api_error" }, "down"));

        await Assert.ThrowsAsync<StripeException>(async () => await s.HoldAsync(supplier: supplier));

        Assert.Empty(await s.HoldsAsync()); // nobody is left waiting for a booking that cannot be paid
        Assert.DoesNotContain(s.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.SupplierBookingVerification);
    }

    // ─── Both conditions, in either order, one request ───

    [Fact]
    public async Task TheRequest_IsBorn_WhenTheCardIsBlockedFirstAndTheEmailIsCheckedAfter()
    {
        var (s, supplier) = await StartAsync();
        using var scope = s;
        var (held, token) = await s.HoldAsync(supplier: supplier);
        var hold = Assert.Single(await s.HoldsAsync());

        var block = await s.Kit.Booking.ApplyHoldAuthorizationAsync(CardBlocked(s, hold));

        // The card is blocked and the e-mail is not checked: the supplier has nothing.
        Assert.True(block.Handled);
        Assert.Null(block.Completion);
        Assert.Empty(await s.ShowcaseRequestsAsync());
        Assert.NotNull(Assert.Single(await s.HoldsAsync()).PaymentAuthorizedAt);

        var confirmation = await s.Kit.Booking.ConfirmEmailAsync(supplier, held.Id, token);

        Assert.False(confirmation.AwaitingPayment);
        Assert.Equal(ServiceRequestStatus.Richiesto, confirmation.Status);
        var request = Assert.Single(await s.ShowcaseRequestsAsync());
        Assert.Equal(ServiceRequestPaymentMode.Online, request.PaymentMode);
        Assert.Equal(Total, request.EstimatedAmountCents);
        AssertAuthorizedPayment(Assert.Single(await s.PaymentsOfAsync(request.Id)), Assert.Single(await s.HoldsAsync()));

        // The receipt says the amount is blocked, not "you pay nothing now"; the supplier is told the amount is already blocked.
        var receipt = Assert.Single(s.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.SupplierBookingReceipt);
        Assert.Contains("bloccato", receipt.Content.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("non paghi nulla", receipt.Content.HtmlBody, StringComparison.OrdinalIgnoreCase);
        var news = Assert.Single(s.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.SupplierBookingNewRequest);
        Assert.Contains("bloccato sulla carta", news.Content.HtmlBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheRequest_IsBorn_WhenTheEmailIsCheckedFirstAndTheCardIsBlockedAfter()
    {
        var (s, supplier) = await StartAsync();
        using var scope = s;
        var (held, token) = await s.HoldAsync(supplier: supplier);
        var hold = Assert.Single(await s.HoldsAsync());

        var waiting = await s.Kit.Booking.ConfirmEmailAsync(supplier, held.Id, token);

        // The address is checked but the card is not blocked: no request, the supplier knows nothing, the page is told to wait for the card.
        Assert.True(waiting.AwaitingPayment);
        Assert.False(waiting.AlreadyConfirmed);
        Assert.Empty(await s.ShowcaseRequestsAsync());
        Assert.DoesNotContain(s.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.SupplierBookingNewRequest);
        Assert.DoesNotContain(s.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.SupplierBookingReceipt);

        var secondClick = await s.Kit.Booking.ConfirmEmailAsync(supplier, held.Id, token);
        Assert.True(secondClick.AwaitingPayment);
        Assert.True(secondClick.AlreadyConfirmed);

        var block = await s.Kit.Booking.ApplyHoldAuthorizationAsync(CardBlocked(s, hold));

        Assert.True(block.Handled);
        Assert.NotNull(block.Completion);
        var request = Assert.Single(await s.ShowcaseRequestsAsync());
        Assert.Equal(ServiceRequestPaymentMode.Online, request.PaymentMode);
        AssertAuthorizedPayment(Assert.Single(await s.PaymentsOfAsync(request.Id)), Assert.Single(await s.HoldsAsync()));

        // The mails go once the webhook transaction committed, not before.
        Assert.DoesNotContain(s.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.SupplierBookingReceipt);
        await s.Kit.Booking.CompleteHoldAuthorizationAsync(block.Completion!);
        Assert.Single(s.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.SupplierBookingReceipt);
        Assert.Single(s.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.SupplierBookingNewRequest);

        // A third click on the link answers the request as it is.
        var replay = await s.Kit.Booking.ConfirmEmailAsync(supplier, held.Id, token);
        Assert.False(replay.AwaitingPayment);
        Assert.True(replay.AlreadyConfirmed);
        Assert.Equal(ServiceRequestStatus.Richiesto, replay.Status);
    }

    [Fact]
    public async Task TheSameBlockDeliveredTwice_CreatesOneRequestAndOnePayment()
    {
        var (s, supplier) = await StartAsync();
        using var scope = s;
        var (held, token) = await s.HoldAsync(supplier: supplier);
        var hold = Assert.Single(await s.HoldsAsync());
        await s.Kit.Booking.ConfirmEmailAsync(supplier, held.Id, token);
        var authorization = CardBlocked(s, hold);

        var first = await s.Kit.Booking.ApplyHoldAuthorizationAsync(authorization);
        var second = await s.Kit.Booking.ApplyHoldAuthorizationAsync(authorization);

        Assert.NotNull(first.Completion);
        Assert.False(second.Handled); // the hold is a request now: the event is about its payment, which handles it
        Assert.Single(await s.ShowcaseRequestsAsync());
        Assert.Single(s.Db.ServiceRequestPayments);
    }

    [Fact]
    public async Task ABlockThatIsNotTheTotal_CreatesNoRequest_EvenWithTheEmailChecked()
    {
        var (s, supplier) = await StartAsync();
        using var scope = s;
        var (held, token) = await s.HoldAsync(supplier: supplier);
        var hold = Assert.Single(await s.HoldsAsync());
        await s.Kit.Booking.ConfirmEmailAsync(supplier, held.Id, token);

        var result = await s.Kit.Booking.ApplyHoldAuthorizationAsync(CardBlocked(s, hold, capturableCents: Total - 1));

        Assert.True(result.Handled);
        Assert.Null(result.Completion);
        Assert.Empty(await s.ShowcaseRequestsAsync());
        Assert.Empty(s.Db.ServiceRequestPayments);
        Assert.Null(Assert.Single(await s.HoldsAsync()).PaymentAuthorizedAt);
    }

    [Fact]
    public async Task ABlockOfAnotherPaymentIntent_IsNotTheBlockOfTheHold()
    {
        var (s, supplier) = await StartAsync();
        using var scope = s;
        await s.HoldAsync(supplier: supplier);
        var hold = Assert.Single(await s.HoldsAsync());
        var authorization = CardBlocked(s, hold);

        var other = authorization with { Observed = authorization.Observed with { PaymentIntentId = "pi_somebody_else" } };
        var result = await s.Kit.Booking.ApplyHoldAuthorizationAsync(other);

        Assert.False(result.Handled);
        Assert.Null(Assert.Single(await s.HoldsAsync()).PaymentAuthorizedAt);
    }

    // ─── A hold that lapses releases its block; a request's block is never touched ───

    private static ServiceRequestExpiryService ExpiryJob(ServiceRequestScenario s) =>
        new(
            s.Db,
            new ServiceRequestAutoCancelService(
                s.Db, s.Notifier, Mock.Of<IFeatureFlags>(), NullLogger<ServiceRequestAutoCancelService>.Instance, s.Clock, s.Payments),
            NullLogger<ServiceRequestExpiryService>.Instance,
            s.Clock,
            s.Kit.Booking);

    [Fact]
    public async Task AnExpiredHold_ReleasesTheBlockOnTheCard_BeforeItIsDeleted()
    {
        var (s, supplier) = await StartAsync();
        using var scope = s;
        await s.HoldAsync(supplier: supplier);
        var hold = Assert.Single(await s.HoldsAsync());
        await s.Kit.Booking.ApplyHoldAuthorizationAsync(CardBlocked(s, hold)); // the card is blocked, the e-mail never checked
        s.Clock.Advance(TimeSpan.FromMinutes(s.Kit.ShowcaseOptions.EmailVerificationMinutes + 1));

        var run = await ExpiryJob(s).RunAsync();

        Assert.Equal(1, run.HoldsDeleted);
        Assert.Equal("canceled", s.Kit.PlatformGateway.Intent(hold.StripePaymentIntentId!).Status);
        var cancellation = Assert.Single(s.Kit.PlatformGateway.Cancellations);
        Assert.Equal(hold.StripePaymentIntentId, cancellation.PaymentIntentId);
        Assert.Empty(await s.HoldsAsync());
        Assert.Empty(await s.ShowcaseRequestsAsync());
    }

    [Fact]
    public async Task TheBlockOfAHoldThatBecameARequest_IsNeverCanceledByTheExpiry()
    {
        var (s, supplier) = await StartAsync();
        using var scope = s;
        var (request, hold) = await BookedAndPaidAsync(s, supplier);
        s.Clock.Advance(TimeSpan.FromMinutes(s.Kit.ShowcaseOptions.EmailVerificationMinutes + 1)); // the hold lapsed; the block lives 48 hours

        var released = await s.Kit.Booking.ReleaseExpiredHoldAsync(supplier.OrgId, hold.Id);
        var run = await ExpiryJob(s).RunAsync();

        Assert.False(released);
        Assert.Equal(1, run.HoldsDeleted); // the hold of a request is just deleted, as before
        Assert.Empty(s.Kit.PlatformGateway.Cancellations);
        Assert.Equal("requires_capture", s.Kit.PlatformGateway.Intent(hold.StripePaymentIntentId!).Status);
        Assert.Equal(ServicePaymentStatus.Authorized, Assert.Single(await s.PaymentsOfAsync(request.Id)).Status);
    }

    // ─── The supplier's acceptance charges the card first ───

    [Fact]
    public async Task Take_ChargesTheCard_AndKeepsTheBookingPaidInsideCasaZen()
    {
        var (s, supplier) = await StartAsync();
        using var scope = s;
        var (request, _) = await BookedAndPaidAsync(s, supplier);

        var taken = await s.TakeAsSupplierAsync(request.Id);

        Assert.Equal(ServiceRequestStatus.PresoInCarico, taken.Status);
        Assert.Equal(ServiceRequestPaymentMode.Online, taken.PaymentMode); // a take never turns a paid booking into a manual one
        var payment = Assert.Single(await s.PaymentsOfAsync(request.Id));
        Assert.Equal(ServicePaymentStatus.Held, payment.Status);
        Assert.NotNull(payment.CapturedAt);
        Assert.Single(s.Kit.PlatformGateway.Captures);

        // The customer is told the card was charged and that CasaZen holds the amount.
        var accepted = Assert.Single(s.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.SupplierBookingAccepted);
        Assert.Contains("addebitato", accepted.Content.HtmlBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Take_WhenTheBankRefusesTheCharge_TheRequestStaysNew_AndTheBlockIsReleased()
    {
        var (s, supplier) = await StartAsync();
        using var scope = s;
        var (request, hold) = await BookedAndPaidAsync(s, supplier);
        s.Kit.PlatformGateway.FailNextCapture(
            new StripeException(HttpStatusCode.PaymentRequired, new StripeError { Type = "card_error", Code = "card_declined" }, "declined"));

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(async () => await s.TakeAsSupplierAsync(request.Id));

        Assert.Equal("service_payment_authorization_expired", refusal.Code);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status); // never accepted with no money behind it
        Assert.Equal("canceled", s.Kit.PlatformGateway.Intent(hold.StripePaymentIntentId!).Status);
    }

    [Fact]
    public async Task Take_WhenStripeIsDown_TheRequestStaysNew_AndTheSupplierCanTryAgain()
    {
        var (s, supplier) = await StartAsync();
        using var scope = s;
        var (request, _) = await BookedAndPaidAsync(s, supplier);
        s.Kit.PlatformGateway.FailNextCapture(new StripeException(HttpStatusCode.ServiceUnavailable, new StripeError { Type = "api_error" }, "down"));

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(async () => await s.TakeAsSupplierAsync(request.Id));
        Assert.Equal("service_payment_capture_failed", refusal.Code);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status);

        var taken = await s.TakeAsSupplierAsync(request.Id);

        Assert.Equal(ServiceRequestStatus.PresoInCarico, taken.Status);
        Assert.Equal(2, s.Kit.PlatformGateway.Captures.Count);
        Assert.Single(s.Kit.PlatformGateway.Captures.Select(c => c.Key).Distinct()); // the same key: Stripe charges once
    }

    [Fact]
    public async Task TheSupplier_CannotChangeThePriceOfABookingWhoseCardIsBlocked()
    {
        var (s, supplier) = await StartAsync();
        using var scope = s;
        var (request, _) = await BookedAndPaidAsync(s, supplier);

        var quote = await Assert.ThrowsAsync<DomainRuleException>(async () => await s.Service.SendQuoteAsync(
            request.Id,
            s.SupplierOrgId,
            ServiceRequestScenario.SupplierUserId,
            new SendServiceRequestQuoteCommand([new ServiceRequestQuoteLine("Pulizia", Total - 1_000)])));
        var take = await Assert.ThrowsAsync<DomainRuleException>(
            async () => await s.TakeAsSupplierAsync(request.Id, new TakeServiceRequestCommand(QuotedAmountCents: Total - 1_000)));

        Assert.Equal("service_payment_amount_locked", quote.Code);
        Assert.Equal("service_payment_amount_locked", take.Code);
        Assert.Empty(s.Kit.PlatformGateway.Captures);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status);
    }

    // ─── Closing the request gives the customer's money back ───

    [Fact]
    public async Task TheCustomersCancellation_ReleasesTheBlock_BeforeAnythingIsCharged()
    {
        var (s, supplier) = await StartAsync();
        using var scope = s;
        var (request, hold) = await BookedAndPaidAsync(s, supplier);

        await s.Kit.Manager.CancelAsync(ShowcaseManageScenario.CredentialsOf(request.PublicCode!), reason: null);

        Assert.Equal(ServicePaymentStatus.Canceled, Assert.Single(await s.PaymentsOfAsync(request.Id)).Status);
        Assert.Equal("canceled", s.Kit.PlatformGateway.Intent(hold.StripePaymentIntentId!).Status);
        Assert.Empty(s.Kit.PlatformGateway.Captures);
    }

    [Fact]
    public async Task TheSuppliersRefusal_ReleasesTheBlock()
    {
        var (s, supplier) = await StartAsync();
        using var scope = s;
        var (request, hold) = await BookedAndPaidAsync(s, supplier);

        await s.Service.RejectAsync(request.Id, s.SupplierOrgId, "Non posso quel giorno");

        Assert.Equal(ServicePaymentStatus.Canceled, Assert.Single(await s.PaymentsOfAsync(request.Id)).Status);
        Assert.Equal("canceled", s.Kit.PlatformGateway.Intent(hold.StripePaymentIntentId!).Status);
    }

    [Fact]
    public async Task ACancellationAfterTheCharge_GivesEverythingBack_TheCommissionIncluded()
    {
        var (s, supplier) = await StartAsync();
        using var scope = s;
        var (request, _) = await BookedAndPaidAsync(s, supplier);
        await s.TakeAsSupplierAsync(request.Id);

        await s.Kit.Manager.CancelAsync(ShowcaseManageScenario.CredentialsOf(request.PublicCode!), reason: null);

        var refund = Assert.Single(s.Kit.PlatformGateway.Refunds);
        Assert.Equal(Total, refund.AmountCents); // cancelling is always free (D6): the commission goes back too
        Assert.Equal(ServicePaymentStatus.Refunded, Assert.Single(await s.PaymentsOfAsync(request.Id)).Status);
        Assert.Empty(s.Kit.PlatformGateway.Transfers);
    }

    [Fact]
    public async Task ANewRequestWhoseBlockIsGone_IsClosedByTheJob_SoNobodyKeepsWaiting()
    {
        var (s, supplier) = await StartAsync();
        using var scope = s;
        var (request, _) = await BookedAndPaidAsync(s, supplier);
        var payment = await s.Db.ServiceRequestPayments.SingleAsync(p => p.ServiceRequestId == request.Id);
        payment.Status = ServicePaymentStatus.Canceled; // the block lapsed and was released
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();

        var run = await new ServiceRequestAutoCancelService(
            s.Db, s.Notifier, Mock.Of<IFeatureFlags>(), NullLogger<ServiceRequestAutoCancelService>.Instance, s.Clock, s.Payments)
            .CancelUnansweredShowcaseAsync();

        Assert.Equal(1, run.Cancelled);
        var closed = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Annullato, closed.Status);
        Assert.Equal(ServiceRequestCancellationReasons.NoResponse, closed.CancellationReason);
    }

    [Fact]
    public async Task TheAnswerTime_OfABookingWhoseCardIsBlocked_NeverReachesPastTheBlock()
    {
        var (s, supplier) = await StartAsync();
        using var scope = s;
        var settings = await s.Db.SupplierSettings.SingleAsync(x => x.OrgId == s.SupplierOrgId);
        settings.RespondWithinMinutes = 7 * 24 * 60; // a supplier that gives itself a week; the block lives 48 hours
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();

        var (request, _) = await BookedAndPaidAsync(s, supplier);

        var payment = Assert.Single(await s.PaymentsOfAsync(request.Id));
        var limit = HeldPaymentRules.LatestResponseAt(payment.AuthorizedAt!.Value, s.Kit.PaymentOptions.AuthorizationValidity);
        Assert.Equal(limit, request.ResponseDueAt);
        Assert.True(request.ResponseDueAt < payment.AuthorizationExpiresAt);
    }

    // ─── What the customer sees, and "all fine" ───

    [Fact]
    public async Task Lookup_ShowsTheMoney_AndAllFineReleasesTheSupplierOnce_TheNetOfTheCommission()
    {
        var (s, supplier) = await StartAsync();
        using var scope = s;
        var (request, _) = await BookedAndPaidAsync(s, supplier);
        var credentials = ShowcaseManageScenario.CredentialsOf(request.PublicCode!);

        var view = await s.Kit.Manager.LookupAsync(credentials);
        Assert.Equal("authorized", view.Payment!.Status);
        Assert.Equal(Total, view.Payment.AmountCents);
        Assert.False(view.Actions.CanConfirmOk);

        await s.TakeAsSupplierAsync(request.Id);
        view = await s.Kit.Manager.LookupAsync(credentials);
        Assert.Equal("held", view.Payment!.Status);
        Assert.Null(view.Payment.ReleaseAtUtc); // the work is not done: no time is running
        Assert.False(view.Actions.CanConfirmOk);

        // "All fine" before the work is done is refused: there is nothing to release.
        var early = await Assert.ThrowsAsync<DomainRuleException>(async () => await s.Kit.Manager.ConfirmOkAsync(credentials));
        Assert.Equal("service_payment_not_confirmable", early.Code);
        Assert.Empty(s.Kit.PlatformGateway.Transfers);

        await s.Service.CompleteAsync(request.Id, s.SupplierOrgId, new CompleteServiceRequestCommand());
        view = await s.Kit.Manager.LookupAsync(credentials);
        Assert.Equal("held", view.Payment!.Status);
        Assert.NotNull(view.Payment.ReleaseAtUtc);
        Assert.True(view.Actions.CanConfirmOk);

        var confirmed = await s.Kit.Manager.ConfirmOkAsync(credentials);
        var again = await s.Kit.Manager.ConfirmOkAsync(credentials); // a double click is not an error and pays nobody twice

        Assert.Equal("released", confirmed.Payment!.Status);
        Assert.Equal("released", again.Payment!.Status);
        Assert.False(again.Actions.CanConfirmOk);
        var transfer = Assert.Single(s.Kit.PlatformGateway.Transfers);
        Assert.Equal(Net, transfer.AmountCents);
        Assert.Equal(SupplierAccountId, transfer.DestinationAccountId);
    }

    private const string SupplierAccountId = ServiceRequestScenario.SupplierAccountId;

    [Fact]
    public async Task AllFine_OfABookingThatIsNotPaidInsideCasaZen_HasNothingToConfirm()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var (request, credentials) = await s.BookedForManagementAsync();

        var refusal = await Assert.ThrowsAsync<DomainRuleException>(async () => await s.Kit.Manager.ConfirmOkAsync(credentials));

        Assert.Equal("service_payment_not_confirmable", refusal.Code);
        Assert.Null((await s.Kit.Manager.LookupAsync(credentials)).Payment);
        Assert.Equal(ServiceRequestPaymentMode.Manual, request.PaymentMode);
    }

    // ─── The pure rules of what the customer reads ───

    [Theory]
    [InlineData(ServicePaymentStatus.Authorized, "authorized")]
    [InlineData(ServicePaymentStatus.Held, "held")]
    [InlineData(ServicePaymentStatus.TransferFailed, "held")]
    [InlineData(ServicePaymentStatus.Disputed, "held")]
    [InlineData(ServicePaymentStatus.Released, "released")]
    [InlineData(ServicePaymentStatus.Refunded, "refunded")]
    [InlineData(ServicePaymentStatus.PartiallyRefunded, "refunded")]
    [InlineData(ServicePaymentStatus.Canceled, "canceled")]
    [InlineData(ServicePaymentStatus.NeedsReview, "review")]
    public void PaymentStatusOf_IsWhatTheCustomerReads(ServicePaymentStatus status, string expected) =>
        Assert.Equal(expected, ShowcaseBookingManagementRules.PaymentStatusOf(status));

    [Theory]
    [InlineData(ServiceRequestStatus.Completato, ServicePaymentStatus.Held, Net, 0, true)]
    [InlineData(ServiceRequestStatus.Completato, ServicePaymentStatus.TransferFailed, Net, 0, true)]
    [InlineData(ServiceRequestStatus.PresoInCarico, ServicePaymentStatus.Held, Net, 0, false)] // the work is not done
    [InlineData(ServiceRequestStatus.Completato, ServicePaymentStatus.Authorized, Net, 0, false)] // nothing was charged
    [InlineData(ServiceRequestStatus.Completato, ServicePaymentStatus.Released, Net, 0, false)] // the supplier was paid
    [InlineData(ServiceRequestStatus.Completato, ServicePaymentStatus.Disputed, Net, 0, false)] // a dispute stopped it
    [InlineData(ServiceRequestStatus.Completato, ServicePaymentStatus.Held, Net, Net, false)] // nothing is left to release
    public void CanConfirmOk_NeedsTheWorkDone_AndMoneyThatCanStillBeReleased(
        ServiceRequestStatus request, ServicePaymentStatus payment, int net, int refunded, bool expected) =>
        Assert.Equal(expected, ShowcaseBookingManagementRules.CanConfirmOk(request, payment, net, refunded));

    [Fact]
    public void LatestResponseAt_IsBeforeTheBlockLapses_ByAQuarterOfItsLifeAtMostAnHour()
    {
        var at = new DateTime(2026, 10, 30, 8, 0, 0, DateTimeKind.Utc);

        Assert.Equal(at.AddHours(47), HeldPaymentRules.LatestResponseAt(at, TimeSpan.FromHours(48)));
        Assert.Equal(at.AddHours(3), HeldPaymentRules.LatestResponseAt(at, TimeSpan.FromHours(4))); // 4 h - min(1 h, 1 h)
        Assert.Equal(at.AddMinutes(90), HeldPaymentRules.LatestResponseAt(at, TimeSpan.FromHours(2)));
        Assert.True(HeldPaymentRules.LatestResponseAt(at, TimeSpan.FromHours(1)) < HeldPaymentRules.AuthorizationExpiry(at, TimeSpan.FromHours(1)));
    }
}
