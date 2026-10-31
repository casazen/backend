using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-10b-b, second part: the offer that is paid inside CasaZen, on the real services over the in-memory database and a
/// <see cref="FakePlatformPaymentGateway"/> (Stripe is never reached). One test per rule where a mistake costs money: accepting asks for the card
/// for the total of the offer and takes <b>nothing</b> until the card is blocked; the card is charged <b>after</b> the time of the offer is judged
/// and <b>before</b> the request changes state, once, for the total; a time that is gone releases the block and charges nothing; an offer the supplier
/// replaced, the customer turned down or let lapse releases its card; a block that is not what was asked for is reviewed, never charged; a customer
/// who paid is never asked to pay again because an event was late; and an offer that is not paid inside CasaZen is taken at once as before.
/// </summary>
public class ServiceRequestOfferPaymentTests
{
    private const int Total = 12_000; // 2 × 45 + 30 euro: the commission of 10 % is 1,200, the net 10,800
    private const int Commission = 1_200;
    private const int Net = 10_800;

    private static readonly DateTime Start = ServiceRequestScenario.FridayAt10;

    private static ShowcaseBookingCredentials CredentialsOf(ServiceRequest request) =>
        ShowcaseManageScenario.CredentialsOf(request.PublicCode!);

    private static async Task<(ServiceRequestScenario S, ServiceRequest Request)> StartAsync()
    {
        var s = await ServiceRequestScenario.CreateAsync();
        await s.EnablePaymentsAsync();
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        return (s, request);
    }

    /// <summary>The supplier sends its offer (two hours at 45 and the materials at 30: 120 euro) and returns the version the customer repeats.</summary>
    private static async Task<DateTime> OfferAsync(ServiceRequestScenario s, ServiceRequest request, int hours = 2, DateTime? start = null)
    {
        var at = start ?? Start;
        await s.Service.SendOfferAsync(
            request.Id,
            s.SupplierOrgId,
            ServiceRequestScenario.SupplierUserId,
            new SendServiceRequestOfferCommand(
                [new ServiceRequestOfferLine("Posa del corrimano", hours, 4500), new ServiceRequestOfferLine("Materiali", 1, 3000)],
                at,
                at.AddHours(2),
                "Porto io tutto il materiale"));
        return (await s.ReadAsync(request.Id)).QuoteSentAt!.Value;
    }

    /// <summary>The customer confirmed the card in the browser: the PaymentIntent is a block, and Stripe tells it to the webhook.</summary>
    private static ServicePaymentIntentEvent CardBlocked(ServiceRequestScenario s, ServiceRequestPayment payment, long? capturableCents = null)
    {
        var charge = s.Kit.PlatformGateway.SetStatus(payment.StripePaymentIntentId!, "requires_capture");
        return new ServicePaymentIntentEvent(
            "evt_" + Guid.NewGuid().ToString("N"),
            "payment_intent.amount_capturable_updated",
            charge.Id,
            null,
            payment.Id,
            charge.AmountCents,
            charge.AmountReceivedCents,
            charge.Currency,
            charge.ApplicationFeeCents,
            null,
            null,
            capturableCents ?? charge.AmountCapturableCents,
            charge.LatestChargeId);
    }

    private static ServiceRequestExpiryService ExpiryJobWithFunds(ServiceRequestScenario s) =>
        new(
            s.Db,
            new ServiceRequestAutoCancelService(
                s.Db, s.Notifier, Mock.Of<IFeatureFlags>(), NullLogger<ServiceRequestAutoCancelService>.Instance, s.Clock, s.Payments),
            NullLogger<ServiceRequestExpiryService>.Instance,
            s.Clock);

    // ─── Asking for the card ───

    [Fact]
    public async Task Accept_OfAnOfferPaidInsideCasaZen_AsksForTheCardForTheTotal_AndTakesNothingUntilItIsBlocked()
    {
        var (s, request) = await StartAsync();
        using var scope = s;
        var sentAt = await OfferAsync(s, request);
        s.ForgetNotifications();

        var before = await s.Kit.Manager.LookupAsync(CredentialsOf(request));
        Assert.True(before.OfferPaymentRequired);

        var asked = await s.Kit.Manager.AcceptOfferAsync(CredentialsOf(request), sentAt);

        // The request waits for the card: still new, with its offer, and the answer carries what the browser needs.
        Assert.Equal(ServiceRequestStatus.Richiesto, asked.Status);
        Assert.Equal(ServiceRequestNegotiation.AwaitingPayment, asked.Negotiation);
        Assert.NotNull(asked.Offer);
        Assert.True(asked.Actions.CanRespondToOffer);
        var session = Assert.IsType<OfferPaymentSession>(asked.PaymentSession);
        Assert.Equal(Total, session.AmountCents);
        Assert.Equal("EUR", session.Currency);
        Assert.Equal(ServiceRequestTestKit.PublishableKey, session.PublishableKey);
        Assert.False(string.IsNullOrEmpty(session.ClientSecret));
        Assert.Equal("awaiting_card", asked.Payment!.Status);
        Assert.Equal(Total, asked.Payment.AmountCents);

        // On the platform, for the total, blocked and charged later (manual capture), with the transfer group of the request.
        var created = Assert.Single(s.Kit.PlatformGateway.Created);
        Assert.True(created.ManualCapture);
        Assert.Equal(Total, created.AmountCents);
        Assert.Equal(ServiceCharges.TransferGroup(request.Id), created.TransferGroup);
        Assert.Equal(request.Id.ToString(), created.Metadata[ServiceCharges.RequestMetadataKey]);

        // One payment, waiting for the card, with the commission CasaZen keeps (never sent to Stripe as a fee).
        var payment = Assert.Single(await s.PaymentsOfAsync(request.Id));
        Assert.Equal(ServicePaymentStatus.Requested, payment.Status);
        Assert.Equal(ServiceFundsModel.PlatformHeld, payment.FundsModel);
        Assert.Equal(ServicePayerKind.Private, payment.PayerKind);
        Assert.Equal((Total, Commission, Net), (payment.AmountCents, payment.ApplicationFeeCents, payment.NetCents));
        Assert.NotNull(payment.StripePaymentIntentId);
        Assert.Null(payment.CapturedAt);

        // And nothing else changed: the request is not taken, nothing is charged, nobody is told.
        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Richiesto, saved.Status);
        Assert.Equal(ServiceRequestPaymentMode.Manual, saved.PaymentMode);
        Assert.Equal(sentAt, saved.QuoteSentAt);
        Assert.Null(saved.ScheduledStartUtc);
        Assert.Empty(s.Kit.PlatformGateway.Captures);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);

        // Asked again before the card is blocked, it is the same PaymentIntent: no second one, no second payment.
        var again = await s.Kit.Manager.AcceptOfferAsync(CredentialsOf(request), sentAt);
        Assert.Equal(session.ClientSecret, again.PaymentSession!.ClientSecret);
        Assert.Single(s.Kit.PlatformGateway.Created);
        Assert.Single(await s.PaymentsOfAsync(request.Id));
    }

    // ─── The card is blocked: the request is taken, the card charged once for the total ───

    [Fact]
    public async Task TheBlock_TakesTheRequestAtTheTimeAndTheTotal_ChargingTheCardOnce_AfterTheTimeIsJudged()
    {
        var (s, request) = await StartAsync();
        using var scope = s;
        var sentAt = await OfferAsync(s, request);
        await s.Kit.Manager.AcceptOfferAsync(CredentialsOf(request), sentAt);
        var payment = Assert.Single(await s.PaymentsOfAsync(request.Id));
        s.ForgetNotifications();

        var notices = await s.Payments.ApplyPaymentIntentEventAsync(CardBlocked(s, payment));

        // The webhook records the block and says the request is to be taken; it takes nothing by itself.
        var notice = Assert.Single(notices);
        Assert.Equal((ServicePaymentNoticeKind.OfferBlocked, request.Id), (notice.Kind, notice.RequestId));
        var blocked = Assert.Single(await s.PaymentsOfAsync(request.Id));
        Assert.Equal(ServicePaymentStatus.Authorized, blocked.Status);
        Assert.NotNull(blocked.AuthorizedAt);
        Assert.NotNull(blocked.AuthorizationExpiresAt);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status);
        Assert.Empty(s.Kit.PlatformGateway.Captures);

        await s.Service.CompleteOfferAcceptanceAsync(request.Id);

        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, saved.Status);
        Assert.Equal(ServiceRequestPaymentMode.Online, saved.PaymentMode);
        Assert.Equal(Total, saved.QuotedAmountCents);
        Assert.Equal(Start, saved.ScheduledStartUtc);
        Assert.Equal(ServiceRequestScenario.SupplierUserId, saved.TakenByUserId);
        Assert.Null(saved.QuoteSentAt);
        Assert.Null(saved.ResponseDueAt);

        // Charged once, with the key of the payment, for the whole block: the amount charged is the total of the offer.
        var capture = Assert.Single(s.Kit.PlatformGateway.Captures);
        Assert.Equal((payment.StripePaymentIntentId, ServiceCharges.CaptureIdempotencyKey(payment.Id)), capture);
        Assert.Equal(Total, s.Kit.PlatformGateway.Intent(payment.StripePaymentIntentId!).AmountReceivedCents);
        var held = Assert.Single(await s.PaymentsOfAsync(request.Id));
        Assert.Equal(ServicePaymentStatus.Held, held.Status);
        Assert.NotNull(held.CapturedAt);
        Assert.Equal(Total, held.AmountCents);
        Assert.Null(held.ReleaseAt);

        // The customer is told its card was charged, with the price of the offer; the supplier is told with "Nome C." and a push.
        var toCustomer = Assert.Single(s.Emails.Snapshot(), e => e.To == ShowcaseScenario.CustomerEmail);
        Assert.Equal(EmailTemplates.Names.SupplierBookingAccepted, toCustomer.Template);
        Assert.Contains("120,00 €", toCustomer.Content.HtmlBody);
        var toSupplier = Assert.Single(s.Emails.Snapshot(), e => e.To == "supplier@test.com");
        Assert.Equal(EmailTemplates.Names.ServiceRequestQuoteAnswered, toSupplier.Template);
        Assert.Equal(PushTypes.ServiceRequestQuoteAccepted, Assert.Single(s.Pushes).Payload.Type);

        // The customer reads the money as held, and the negotiation as agreed.
        var view = await s.Kit.Manager.LookupAsync(CredentialsOf(request));
        Assert.Equal(("held", Total), (view.Payment!.Status, view.Payment.AmountCents));
        Assert.Equal(ServiceRequestNegotiation.Agreed, view.Negotiation);

        // A second completion (the event delivered twice, a retry) charges nothing again and tells nobody again.
        s.ForgetNotifications();
        await s.Service.CompleteOfferAcceptanceAsync(request.Id);
        Assert.Single(s.Kit.PlatformGateway.Captures);
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task Accept_AgainOnceTheCardIsBlocked_TakesTheRequest_WithoutWaitingForTheWebhook()
    {
        var (s, request) = await StartAsync();
        using var scope = s;
        var sentAt = await OfferAsync(s, request);
        await s.Kit.Manager.AcceptOfferAsync(CredentialsOf(request), sentAt);
        var payment = Assert.Single(await s.PaymentsOfAsync(request.Id));

        // The card is blocked on Stripe and the event is late: it has not reached the payment (still waiting for the card).
        s.Kit.PlatformGateway.SetStatus(payment.StripePaymentIntentId!, "requires_capture");

        var taken = await s.Kit.Manager.AcceptOfferAsync(CredentialsOf(request), sentAt);

        // A customer who paid is not asked to pay again: the block is recorded with the checks of the webhook and the request is taken.
        Assert.Equal(ServiceRequestStatus.PresoInCarico, taken.Status);
        Assert.Null(taken.PaymentSession);
        Assert.Equal("held", taken.Payment!.Status);
        Assert.Single(s.Kit.PlatformGateway.Created);
        Assert.Single(s.Kit.PlatformGateway.Captures);
        var saved = await s.ReadAsync(request.Id);
        Assert.Equal((ServiceRequestStatus.PresoInCarico, ServiceRequestPaymentMode.Online, Total), (saved.Status, saved.PaymentMode, saved.QuotedAmountCents));

        // And a second acceptance finds nothing to accept.
        var again = await Assert.ThrowsAsync<DomainRuleException>(() => s.Kit.Manager.AcceptOfferAsync(CredentialsOf(request), sentAt));
        Assert.Equal(ServiceRequestOfferErrors.NoOffer, again.Code);
        Assert.Single(s.Kit.PlatformGateway.Captures);
    }

    // ─── The time is gone: nothing is charged and the block is released ───

    [Fact]
    public async Task TheTimeGone_AfterTheCardIsBlocked_ReleasesTheBlock_ChargesNothing_AndTellsTheCustomer()
    {
        var (s, request) = await StartAsync();
        using var scope = s;
        var sentAt = await OfferAsync(s, request);
        await s.Kit.Manager.AcceptOfferAsync(CredentialsOf(request), sentAt);
        var payment = Assert.Single(await s.PaymentsOfAsync(request.Id));
        await s.Payments.ApplyPaymentIntentEventAsync(CardBlocked(s, payment));

        // The supplier takes another job at that very time before the request is taken.
        await s.TakenAsync(Start);
        s.ForgetNotifications();

        await s.Service.CompleteOfferAcceptanceAsync(request.Id);

        // Nothing was charged, the block is released, the request is as it was and the offer stands.
        Assert.Empty(s.Kit.PlatformGateway.Captures);
        Assert.Equal(payment.StripePaymentIntentId, Assert.Single(s.Kit.PlatformGateway.Cancellations).PaymentIntentId);
        Assert.Equal(ServicePaymentStatus.Canceled, Assert.Single(await s.PaymentsOfAsync(request.Id)).Status);
        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Richiesto, saved.Status);
        Assert.Equal(sentAt, saved.QuoteSentAt);
        Assert.Null(saved.QuotedAmountCents);

        // The customer is told, and told that it paid nothing.
        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Equal((ShowcaseScenario.CustomerEmail, EmailTemplates.Names.SupplierInquiryOfferNotCompleted), (email.To, email.Template));
        Assert.Contains("non è più disponibile", email.Content.HtmlBody);
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task Accept_WhenTheTimeIsGoneAndTheCardIsBlocked_Is409_AndTheBlockIsReleased()
    {
        var (s, request) = await StartAsync();
        using var scope = s;
        var sentAt = await OfferAsync(s, request);
        await s.Kit.Manager.AcceptOfferAsync(CredentialsOf(request), sentAt);
        var payment = Assert.Single(await s.PaymentsOfAsync(request.Id));
        await s.Payments.ApplyPaymentIntentEventAsync(CardBlocked(s, payment));
        await s.TakenAsync(Start);

        var gone = await Assert.ThrowsAsync<DomainConflictException>(() => s.Kit.Manager.AcceptOfferAsync(CredentialsOf(request), sentAt));

        Assert.Equal(ServiceRequestErrorCodes.SlotUnavailable, gone.Code);
        Assert.Empty(s.Kit.PlatformGateway.Captures);
        Assert.Equal(payment.StripePaymentIntentId, Assert.Single(s.Kit.PlatformGateway.Cancellations).PaymentIntentId);
        Assert.Equal(ServicePaymentStatus.Canceled, Assert.Single(await s.PaymentsOfAsync(request.Id)).Status);
        Assert.Equal(sentAt, (await s.ReadAsync(request.Id)).QuoteSentAt);
    }

    [Fact]
    public async Task Accept_WhenTheTimeIsAlreadyGone_AsksForNoCard()
    {
        var (s, request) = await StartAsync();
        using var scope = s;
        var sentAt = await OfferAsync(s, request);
        await s.TakenAsync(Start);

        var gone = await Assert.ThrowsAsync<DomainConflictException>(() => s.Kit.Manager.AcceptOfferAsync(CredentialsOf(request), sentAt));

        // Nobody is asked to pay for a time that is already gone: no PaymentIntent was made.
        Assert.Equal(ServiceRequestErrorCodes.SlotUnavailable, gone.Code);
        Assert.Empty(s.Kit.PlatformGateway.Created);
        Assert.Empty(await s.PaymentsOfAsync(request.Id));
    }

    // ─── The offer ends without being taken: its card is released ───

    [Fact]
    public async Task ANewOffer_ReleasesTheCardOfTheOldOne_AndTheNextAcceptanceBlocksTheNewTotal()
    {
        var (s, request) = await StartAsync();
        using var scope = s;
        var firstSentAt = await OfferAsync(s, request);
        await s.Kit.Manager.AcceptOfferAsync(CredentialsOf(request), firstSentAt);
        var first = Assert.Single(await s.PaymentsOfAsync(request.Id));
        await s.Payments.ApplyPaymentIntentEventAsync(CardBlocked(s, first));
        s.Clock.Advance(TimeSpan.FromMinutes(5));

        var secondSentAt = await OfferAsync(s, request, hours: 1);

        // The card blocked for the old offer is released, never charged for the new one.
        Assert.Equal(first.StripePaymentIntentId, Assert.Single(s.Kit.PlatformGateway.Cancellations).PaymentIntentId);
        Assert.Equal(ServicePaymentStatus.Canceled, Assert.Single(await s.PaymentsOfAsync(request.Id)).Status);
        await s.Service.CompleteOfferAcceptanceAsync(request.Id);
        Assert.Empty(s.Kit.PlatformGateway.Captures);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status);

        // The customer who accepts the new offer is asked for a new card, for the new total (1 × 45 + 30 euro).
        var asked = await s.Kit.Manager.AcceptOfferAsync(CredentialsOf(request), secondSentAt);
        Assert.Equal(7_500, asked.PaymentSession!.AmountCents);
        Assert.Equal(2, s.Kit.PlatformGateway.Created.Count);
        var payments = await s.PaymentsOfAsync(request.Id);
        Assert.Equal(
            new[] { (ServicePaymentStatus.Canceled, Total), (ServicePaymentStatus.Requested, 7_500) },
            payments.Select(p => (p.Status, p.AmountCents)).ToArray());
    }

    [Fact]
    public async Task Decline_ReleasesTheCardOfTheOffer()
    {
        var (s, request) = await StartAsync();
        using var scope = s;
        var sentAt = await OfferAsync(s, request);
        await s.Kit.Manager.AcceptOfferAsync(CredentialsOf(request), sentAt);
        var payment = Assert.Single(await s.PaymentsOfAsync(request.Id));

        var declined = await s.Kit.Manager.DeclineOfferAsync(CredentialsOf(request), sentAt);

        Assert.Equal(ServiceRequestNegotiation.OfferDeclined, declined.Negotiation);
        Assert.Null(declined.Payment);
        Assert.Equal(payment.StripePaymentIntentId, Assert.Single(s.Kit.PlatformGateway.Cancellations).PaymentIntentId);
        Assert.Equal(ServicePaymentStatus.Canceled, Assert.Single(await s.PaymentsOfAsync(request.Id)).Status);
    }

    [Fact]
    public async Task TheOfferThatLapses_ClosesTheRequest_AndReleasesTheCard()
    {
        var (s, request) = await StartAsync();
        using var scope = s;
        var sentAt = await OfferAsync(s, request);
        await s.Kit.Manager.AcceptOfferAsync(CredentialsOf(request), sentAt);
        var payment = Assert.Single(await s.PaymentsOfAsync(request.Id));
        await s.Payments.ApplyPaymentIntentEventAsync(CardBlocked(s, payment));
        s.Clock.Advance(TimeSpan.FromHours(24));

        var run = await ExpiryJobWithFunds(s).RunAsync();

        Assert.Equal(1, run.RequestsCancelled);
        Assert.Equal(ServiceRequestStatus.Annullato, (await s.ReadAsync(request.Id)).Status);
        Assert.Equal(payment.StripePaymentIntentId, Assert.Single(s.Kit.PlatformGateway.Cancellations).PaymentIntentId);
        Assert.Equal(ServicePaymentStatus.Canceled, Assert.Single(await s.PaymentsOfAsync(request.Id)).Status);
        Assert.Empty(s.Kit.PlatformGateway.Captures);
    }

    [Fact]
    public async Task TheCustomerWhoCancels_WhileTheCardWaits_ReleasesIt()
    {
        var (s, request) = await StartAsync();
        using var scope = s;
        var sentAt = await OfferAsync(s, request);
        await s.Kit.Manager.AcceptOfferAsync(CredentialsOf(request), sentAt);
        var payment = Assert.Single(await s.PaymentsOfAsync(request.Id));

        await s.Kit.Manager.CancelAsync(CredentialsOf(request), "Ho cambiato idea");

        Assert.Equal(ServiceRequestStatus.Annullato, (await s.ReadAsync(request.Id)).Status);
        Assert.Equal(payment.StripePaymentIntentId, Assert.Single(s.Kit.PlatformGateway.Cancellations).PaymentIntentId);
        Assert.Equal(ServicePaymentStatus.Canceled, Assert.Single(await s.PaymentsOfAsync(request.Id)).Status);
    }

    // ─── A block that is not what was asked for ───

    [Fact]
    public async Task ABlockThatIsNotTheTotal_GoesToReview_AndTheRequestIsNotTaken()
    {
        var (s, request) = await StartAsync();
        using var scope = s;
        var sentAt = await OfferAsync(s, request);
        await s.Kit.Manager.AcceptOfferAsync(CredentialsOf(request), sentAt);
        var payment = Assert.Single(await s.PaymentsOfAsync(request.Id));

        var notices = await s.Payments.ApplyPaymentIntentEventAsync(CardBlocked(s, payment, capturableCents: 5_000));

        // An alert for the admins, and no notice to take the request.
        Assert.DoesNotContain(notices, n => n.Kind == ServicePaymentNoticeKind.OfferBlocked);
        Assert.Contains(notices, n => n.Kind == ServicePaymentNoticeKind.FundsAlert);
        Assert.Equal(ServicePaymentStatus.NeedsReview, Assert.Single(await s.PaymentsOfAsync(request.Id)).Status);

        await s.Service.CompleteOfferAcceptanceAsync(request.Id);

        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status);
        Assert.Empty(s.Kit.PlatformGateway.Captures);
    }

    // ─── Not paid inside CasaZen: taken at once, as in the first part ───

    [Fact]
    public async Task AnOfferBelowTheMinimum_IsTakenAtOnce_WithNoCard()
    {
        var (s, request) = await StartAsync();
        using var scope = s;
        await s.Service.SendOfferAsync(
            request.Id,
            s.SupplierOrgId,
            ServiceRequestScenario.SupplierUserId,
            new SendServiceRequestOfferCommand([new ServiceRequestOfferLine("Un caffè", 1, 40)], Start, Start.AddHours(2)));
        var sentAt = (await s.ReadAsync(request.Id)).QuoteSentAt!.Value;

        var view = await s.Kit.Manager.LookupAsync(CredentialsOf(request));
        Assert.False(view.OfferPaymentRequired);

        var taken = await s.Kit.Manager.AcceptOfferAsync(CredentialsOf(request), sentAt);

        Assert.Equal(ServiceRequestStatus.PresoInCarico, taken.Status);
        Assert.Null(taken.PaymentSession);
        Assert.Equal(ServiceRequestPaymentMode.Manual, (await s.ReadAsync(request.Id)).PaymentMode);
        Assert.Empty(s.Kit.PlatformGateway.Created);
        Assert.Empty(await s.PaymentsOfAsync(request.Id));
    }

    [Fact]
    public async Task WithThePaymentsOff_TheOfferIsTakenAtOnce_AndTheViewSaysNoCardIsNeeded()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var request = await s.InquiredAsync(supplier);
        var sentAt = await OfferAsync(s, request);

        var view = await s.Kit.Manager.LookupAsync(CredentialsOf(request));
        Assert.False(view.OfferPaymentRequired);

        var taken = await s.Kit.Manager.AcceptOfferAsync(CredentialsOf(request), sentAt);

        Assert.Equal(ServiceRequestStatus.PresoInCarico, taken.Status);
        Assert.Null(taken.PaymentSession);
        Assert.Empty(s.Kit.PlatformGateway.Created);
    }
}
