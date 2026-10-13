using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>
/// What the customer gets back from a booking attempt (<c>POST api/public/suppliers/{slug}/bookings</c>): the id of the hold
/// that the e-mail link carries, and when it lapses. Nothing about the customer, the code or the price.
/// </summary>
public sealed record ShowcaseBookingHoldResult(Guid Id, DateTime ExpiresAt);

/// <summary>
/// The booking once the e-mail is checked (<c>POST …/bookings/{id}/confirm-email</c>): what the customer needs to read on the
/// page that follows the link. A second click on the link answers the same, with the request as it is by then.
/// </summary>
/// <param name="PublicCode">The code of the booking, stored form (<see cref="BookingCodes"/>): the page shows it as <c>XXXXX-XXXXX</c>.</param>
/// <param name="Status">The request now (<c>Richiesto</c> the first time; the supplier may have answered by a second click).</param>
/// <param name="RespondBy">When the supplier has to answer by, while the request is new; <c>null</c> after.</param>
/// <param name="AlreadyConfirmed">True when the e-mail had already been checked (a second click).</param>
public sealed record ShowcaseBookingConfirmation(
    string PublicCode,
    ServiceRequestStatus Status,
    string ServiceName,
    DateTime StartUtc,
    DateTime EndUtc,
    DateTime? RespondBy,
    bool AlreadyConfirmed);

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
}
