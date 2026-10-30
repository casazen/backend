using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>
/// What the customer gets back from a request on measure (<c>POST api/public/suppliers/{slug}/inquiries</c>): the id that the e-mail
/// link carries, and when the link lapses. Nothing about the customer, the code or the words.
/// </summary>
public sealed record ShowcaseInquiryResult(Guid Id, DateTime ExpiresAt);

/// <summary>
/// The request once the e-mail is checked (<c>POST …/inquiries/{id}/confirm-email</c>): what the customer needs to read on the page that
/// follows the link. A second click answers the same, with the request as it is by then.
/// </summary>
/// <param name="PublicCode">The code of the request, stored form (<see cref="BookingCodes"/>): the page shows it as <c>XXXXX-XXXXX</c>.</param>
/// <param name="Status">The request now (<c>Richiesto</c> the first time).</param>
/// <param name="ServiceName">The service it is about, when it is about one.</param>
/// <param name="RespondBy">When the supplier has to answer by, while nobody answered; <c>null</c> after.</param>
/// <param name="AlreadyConfirmed">True when the e-mail had already been checked (a second click).</param>
public sealed record ShowcaseInquiryConfirmation(
    string PublicCode,
    ServiceRequestStatus Status,
    string? ServiceName,
    DateTime? RespondBy,
    bool AlreadyConfirmed);

/// <summary>
/// The request on measure a customer with no account makes from a supplier's public showcase (SP-10b): the customer describes what it needs
/// — for a service that is not in the price list, or is but with variations — checks its e-mail and, only then, a
/// <see cref="ServiceRequest"/> of the <see cref="ServiceRequestKind.Custom"/> kind exists for the supplier to answer. It is the twin of
/// <see cref="IShowcaseBookingService"/> without a slot to hold: same checks of the address, same data of the customer, same promise that
/// nothing a stranger types reaches the supplier before the e-mail is checked.
/// </summary>
public interface IShowcaseInquiryService
{
    /// <summary>
    /// Keeps what the customer typed for <c>Suppliers:Showcase:EmailVerificationMinutes</c> and queues the e-mail with the link that checks
    /// the address. The same <see cref="ShowcaseInquiryInput.ClientRequestId"/> is the same inquiry (no second e-mail). The supplier found
    /// by <c>IPublicSupplierShowcaseService.FindActiveSupplierAsync</c> is the one asked.
    /// </summary>
    /// <exception cref="ShowcaseBookingRuleException">422 <see cref="ShowcaseBookingErrors.Invalid"/>: the fields that are not valid.</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="ShowcaseBookingErrors.ConsentRequired"/>, <see cref="ShowcaseBookingErrors.ConsentOutdated"/>,
    /// <see cref="ShowcaseBookingErrors.OutsideZone"/>.
    /// </exception>
    /// <exception cref="Exceptions.NotFoundException">The service named is not one of the supplier's published ones.</exception>
    /// <exception cref="ShowcaseBookingTooManyHoldsException">The address already has the most requests it may have waiting.</exception>
    Task<ShowcaseInquiryResult> CreateAsync(
        SupplierProfile supplier,
        ShowcaseInquiryInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks the e-mail with the token of the link: creates the customer, the request (<c>Kind = Custom</c>, <c>Richiesto</c>, no time,
    /// answer due in <c>Suppliers:Showcase:InquiryResponseMinutes</c>) and its first message, consumes the inquiry and tells the customer
    /// (receipt) and the supplier (new request, push). Used once: a second call with the same token answers the same without doing
    /// anything again.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="ShowcaseBookingErrors.LinkInvalid"/>: unknown inquiry, another supplier's, wrong token.</exception>
    /// <exception cref="Exceptions.DomainConflictException"><see cref="ShowcaseBookingErrors.LinkExpired"/>: the time to check has passed.</exception>
    /// <exception cref="Exceptions.DomainRuleException"><see cref="ShowcaseBookingErrors.SupplierUnavailable"/>: the supplier is not active anymore.</exception>
    Task<ShowcaseInquiryConfirmation> ConfirmEmailAsync(
        SupplierProfile supplier,
        Guid inquiryId,
        string? token,
        CancellationToken cancellationToken = default);
}
