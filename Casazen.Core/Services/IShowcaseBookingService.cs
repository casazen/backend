using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>
/// The card block of a booking that is paid inside CasaZen (SP-15c-a, decision VT-D1): what the page needs to ask the customer's card for it
/// with the Payment Element. Nothing about the customer.
/// </summary>
/// <param name="AmountCents">The total that is blocked on the card (the total the customer saw); nothing is charged until the supplier accepts.</param>
/// <param name="Currency">Uppercase ISO currency (<c>EUR</c>).</param>
/// <param name="ClientSecret">
/// A credential for the customer's browser, sent once and never logged or stored. <c>null</c> when the card can no longer be asked for this
/// block: it is already blocked (<paramref name="Authorized"/>), or the block was released.
/// </param>
/// <param name="PublishableKey">The platform's publishable key; <c>null</c> together with <paramref name="ClientSecret"/>.</param>
/// <param name="Authorized">The card is already blocked: only the e-mail is left to check.</param>
public sealed record ShowcaseBookingPayment(int AmountCents, string Currency, string? ClientSecret, string? PublishableKey, bool Authorized);

/// <summary>
/// What the customer gets back from a booking attempt (<c>POST api/public/suppliers/{slug}/bookings</c>): the id of the hold
/// that the e-mail link carries, when it lapses and, for a booking paid inside CasaZen, the block of the card to confirm. Nothing about
/// the customer or the code.
/// </summary>
/// <param name="Payment">The block of the customer's card; <c>null</c> when the booking is not paid inside CasaZen (the supplier is paid as always).</param>
public sealed record ShowcaseBookingHoldResult(Guid Id, DateTime ExpiresAt, ShowcaseBookingPayment? Payment = null);

/// <summary>
/// The booking once the e-mail is checked (<c>POST …/bookings/{id}/confirm-email</c>): what the customer needs to read on the
/// page that follows the link. A second click on the link answers the same, with the request as it is by then.
/// </summary>
/// <param name="PublicCode">The code of the booking, stored form (<see cref="BookingCodes"/>): the page shows it as <c>XXXXX-XXXXX</c>.</param>
/// <param name="Status">The request now (<c>Richiesto</c> the first time; the supplier may have answered by a second click).</param>
/// <param name="RespondBy">When the supplier has to answer by, while the request is new; <c>null</c> after.</param>
/// <param name="AlreadyConfirmed">True when the e-mail had already been checked (a second click).</param>
/// <param name="AwaitingPayment">
/// SP-15c-a: the e-mail is checked but the customer's card is not blocked yet, so the request does not exist and the supplier has been told
/// nothing. It is created as soon as the block succeeds (the page can ask again). <see cref="Status"/> and <see cref="RespondBy"/> mean nothing then.
/// </param>
public sealed record ShowcaseBookingConfirmation(
    string PublicCode,
    ServiceRequestStatus Status,
    string ServiceName,
    DateTime StartUtc,
    DateTime EndUtc,
    DateTime? RespondBy,
    bool AlreadyConfirmed,
    bool AwaitingPayment = false);

/// <summary>
/// What Stripe's verified webhook tells about the card block of a booking hold that has no request yet (SP-15c-a): the PaymentIntent is
/// <c>requires_capture</c>. The hold is found by <see cref="HoldId"/> and the supplier org the PaymentIntent was created for, and the facts
/// are checked against what the hold asked for before anything is believed.
/// </summary>
/// <param name="SupplierOrgId">The supplier org, from the PaymentIntent's metadata (the holds are keyed by it).</param>
/// <param name="HoldId">The hold, from the PaymentIntent's metadata.</param>
/// <param name="Observed">What Stripe reports for the PaymentIntent (platform, amount, capturable, currency, no fee).</param>
/// <param name="AuthorizedAt">When Stripe says the block succeeded; null when the event carries no time.</param>
public sealed record ShowcaseHoldAuthorization(
    Guid SupplierOrgId,
    Guid HoldId,
    HeldChargeObservation Observed,
    DateTime? AuthorizedAt);

/// <summary>What is sent once the transaction that made a request out of a hold has committed: the receipt to the customer, the news to the supplier.</summary>
public sealed record ShowcaseHoldCompletion(SupplierProfile Supplier, ServiceRequest Request, ServiceCustomer Customer);

/// <summary>What the booking service did with the card block of a hold.</summary>
/// <param name="Handled">
/// The PaymentIntent is the block of a hold that is still waiting (not yet a request). False: no such hold (it was consumed, expired or never
/// existed), and the caller treats the event as one of a payment.
/// </param>
/// <param name="Completion">Set when the block completed the booking and the request now exists: send it after the commit.</param>
public sealed record ShowcaseHoldAuthorizationResult(bool Handled, ShowcaseHoldCompletion? Completion = null)
{
    /// <summary>The PaymentIntent is not the block of a waiting hold.</summary>
    public static ShowcaseHoldAuthorizationResult NotAHold { get; } = new(false);
}

/// <summary>
/// The booking of a supplier from its public showcase, by a customer with no account (SP-10, decision D34 revised): a hold on
/// a free slot, then the check of the customer's e-mail, then — and only then — a <see cref="ServiceRequest"/> of the
/// <see cref="ServiceRequestRentalContext.Showcase"/> context that the supplier answers. Nothing a stranger types reaches the
/// supplier before the e-mail is checked.
/// </summary>
/// <remarks>
/// <para><b>Concurrency.</b> Both operations run under the supplier's calendar lock (<c>SupplierCalendarSync</c>, the lock of the
/// agenda and of the iCal sync), the slot is judged by the slot planner <b>after</b> the lock is taken and <b>without any cache</b>,
/// and the holds are part of what the planner counts: of any number of customers who book the same slot at the same moment, one
/// wins and the others get 409 <c>supplier_slot_unavailable</c>. Outside PostgreSQL nothing is locked.</para>
/// <para><b>What it never says.</b> Unknown, pending and suspended suppliers, and a link that is wrong, belongs to another
/// supplier or never existed, answer alike (404). The answers carry nothing personal; the log neither.</para>
/// </remarks>
public interface IShowcaseBookingService
{
    /// <summary>
    /// Holds the slot the customer picked for <c>Suppliers:Showcase:EmailVerificationMinutes</c> and queues the e-mail with the
    /// link that checks the address. The same <see cref="ShowcaseBookingInput.ClientRequestId"/> is the same hold. The supplier
    /// found by <c>IPublicSupplierShowcaseService.FindActiveSupplierAsync</c> is the one booked.
    /// </summary>
    /// <exception cref="ShowcaseBookingRuleException">422 <see cref="ShowcaseBookingErrors.Invalid"/>: the fields that are not valid.</exception>
    /// <exception cref="SupplierQuoteRuleException">422 <c>supplier_quote_invalid</c>: a choice that does not fit the service.</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="ShowcaseBookingErrors.ConsentRequired"/>, <see cref="ShowcaseBookingErrors.ConsentOutdated"/>,
    /// <see cref="ShowcaseBookingErrors.Offline"/>, <see cref="ShowcaseBookingErrors.OutsideZone"/>.
    /// </exception>
    /// <exception cref="Exceptions.NotFoundException">The service is not one of the supplier's published ones.</exception>
    /// <exception cref="Exceptions.DomainConflictException"><c>supplier_slot_unavailable</c>: the slot is not free (anymore).</exception>
    /// <exception cref="ShowcaseBookingTooManyHoldsException">The address already has three bookings waiting for the check.</exception>
    Task<ShowcaseBookingHoldResult> CreateHoldAsync(
        SupplierProfile supplier,
        ShowcaseBookingInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks the e-mail with the token of the link: creates the customer and the request (<c>Richiesto</c>, answer due in
    /// <c>SupplierSettings.RespondWithinMinutes</c>), consumes the hold and tells the customer (receipt) and the supplier (new
    /// request, push). Used once: a second call with the same token answers the same without doing anything again.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="ShowcaseBookingErrors.LinkInvalid"/>: unknown hold, another supplier's, wrong token.</exception>
    /// <exception cref="Exceptions.DomainConflictException"><see cref="ShowcaseBookingErrors.LinkExpired"/>: the time to check has passed.</exception>
    /// <exception cref="Exceptions.DomainRuleException"><see cref="ShowcaseBookingErrors.SupplierUnavailable"/>: the supplier is not active anymore.</exception>
    Task<ShowcaseBookingConfirmation> ConfirmEmailAsync(
        SupplierProfile supplier,
        Guid holdId,
        string? token,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// SP-15c-a: the customer's card is blocked for a booking hold (Stripe's <c>payment_intent.amount_capturable_updated</c>, already verified
    /// by the webhook). The block is recorded on the hold; if the e-mail was checked first the request is created now — customer, request and
    /// <see cref="ServicePaymentStatus.Authorized"/> payment, in one transaction under the supplier's calendar lock, exactly as when the e-mail is
    /// checked last. Runs inside the webhook's transaction; never throws for a business reason: a block that does not match the hold, or a hold
    /// that lapsed meanwhile, is logged and left to lapse (its block is then released), never turned into a request.
    /// </summary>
    Task<ShowcaseHoldAuthorizationResult> ApplyHoldAuthorizationAsync(
        ShowcaseHoldAuthorization authorization,
        CancellationToken cancellationToken = default);

    /// <summary>The receipt and the news of the request that <see cref="ApplyHoldAuthorizationAsync"/> created, after the webhook transaction committed. Never throws.</summary>
    Task CompleteHoldAuthorizationAsync(ShowcaseHoldCompletion completion, CancellationToken cancellationToken = default);

    /// <summary>
    /// SP-15c-a: a hold that lapsed and never became a request is released, the block on the customer's card first (Stripe cancels the
    /// PaymentIntent), then the hold is deleted, under the supplier's calendar lock: the check of the e-mail and the webhook take the same lock, so
    /// a hold is either a request before this or gone after it. <c>true</c> when the hold is gone; <c>false</c> when it was kept (Stripe could not
    /// be reached: the next run tries again, the customer's card is never left blocked for good, and a hold that has a request is not touched).
    /// </summary>
    Task<bool> ReleaseExpiredHoldAsync(Guid supplierOrgId, Guid holdId, CancellationToken cancellationToken = default);
}
