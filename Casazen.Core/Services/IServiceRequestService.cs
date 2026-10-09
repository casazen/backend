using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Microsoft.AspNetCore.Http;

namespace Casazen.Core.Services;

/// <summary>
/// A host request to a supplier. <paramref name="RentalContext"/> is the context the host opened it in (D2): a
/// short-rent request is for one stay and needs <paramref name="BookingId"/>; a long-rent request is for the property
/// and takes no booking. See <see cref="ServiceRequestErrorCodes"/>.
/// </summary>
/// <param name="ServiceListingId">
/// The service of the supplier's catalog the request is for (SP-04): it must be an active service of that supplier, of the
/// request's category. Gives the name, the price and the duration of the work.
/// </param>
/// <param name="ScheduledStartUtc">
/// The start of the work the host picked among the supplier's free slots (SP-04): needs <paramref name="ServiceListingId"/>
/// (the duration comes from the service) and is checked with the slot planner under the supplier's calendar lock. Without it
/// the request is "da concordare", as before SP-04.
/// </param>
public record CreateServiceRequestCommand(
    Guid OrgId,
    string UserId,
    Guid PropertyId,
    Guid? BookingId,
    Guid SupplierOrgId,
    string Category,
    ServiceRequestUrgency Urgency,
    string? Notes,
    bool ChargeToGuest,
    ServiceRequestRentalContext RentalContext = ServiceRequestRentalContext.ShortRent,
    Guid? ServiceListingId = null,
    DateTime? ScheduledStartUtc = null);

/// <summary>
/// What the supplier adds when it takes a request (SP-04): a time for a request that has none, and the price it commits to.
/// Everything is optional: a take without a body accepts the request as it is.
/// </summary>
/// <param name="ScheduledStartUtc">The start of the work, for a request that is still "da concordare" (a request that has a time keeps it: the supplier proposes another one with <c>propose-time</c>).</param>
/// <param name="ScheduledEndUtc">The end of the work; when left out, the start plus the duration of the request's service.</param>
/// <param name="QuotedAmountCents">The price the supplier commits to, in cents of euro.</param>
public record TakeServiceRequestCommand(
    DateTime? ScheduledStartUtc = null,
    DateTime? ScheduledEndUtc = null,
    int? QuotedAmountCents = null);

/// <summary>
/// What the supplier declares when it completes a request (SP-04). The notes go in their own column (the host's notes are
/// no longer replaced); the total and the extras make the final price (<see cref="ServiceRequestPricing.ComposeFinalPrice"/>).
/// </summary>
public record CompleteServiceRequestCommand(
    string? Notes = null,
    int? FinalAmountCents = null,
    IReadOnlyList<ServiceRequestExtra>? Extras = null);

/// <summary>
/// Another time for a new request, proposed by the supplier (SP-04).
/// </summary>
/// <param name="EndUtc">The end of the proposed time; when left out, the start plus the duration of the request's service.</param>
public record ProposeServiceRequestTimeCommand(DateTime StartUtc, DateTime? EndUtc = null, string? Message = null);

/// <summary>
/// The outcome for one request of a batch (<see cref="IServiceRequestService.AcceptManyAsync"/>): accepted, or the code and
/// the message of the error that stopped it (the other requests of the batch are not affected).
/// </summary>
public sealed record ServiceRequestBatchResult(
    Guid Id,
    bool Accepted,
    ServiceRequestStatus? Status,
    string? Code,
    string? MessageKey,
    IReadOnlyList<object> MessageArgs);

/// <summary>A photo of the work opened for reading from the private bucket; the caller disposes <see cref="Content"/>.</summary>
public sealed record ServiceRequestPhotoContent(Stream Content, string ContentType, string FileName);

/// <summary>
/// Stable codes (ProblemDetails <c>code</c>) of the service request errors: the D2 rules on what a request is tied to
/// (SU-07, HTTP 422), the lookups, rules and state machine of SU-10 (FD-05: 404 / 422 / 409), and the schedule, price and
/// lifecycle rules of SP-04.
/// </summary>
public static class ServiceRequestErrorCodes
{
    /// <summary>404: no request with that id is visible to the caller (another org's request answers the same).</summary>
    public const string NotFound = "service_request_not_found";

    /// <summary>404: the supplier org of the request has no supplier profile.</summary>
    public const string SupplierNotFound = "supplier_not_found";

    /// <summary>404: the property of the request is not in the host's org.</summary>
    public const string PropertyNotFound = "property_not_found";

    /// <summary>422: the supplier is not active (pending or suspended) and cannot receive requests.</summary>
    public const string SupplierInactive = "service_request_supplier_inactive";

    /// <summary>422: the supplier does not operate in the comune of the property.</summary>
    public const string SupplierOutsideComune = "service_request_supplier_outside_comune";

    /// <summary>
    /// 422: the supplier that tries to take, complete or reject a request is not active (suspended by an admin, or not
    /// activated): a suspended supplier performs no action (SU-12, A4-29).
    /// </summary>
    public const string SupplierNotActive = "service_request_supplier_not_active";

    /// <summary>422: <c>chargeToGuest</c> is not available (the backend never charges the guest for a supplier).</summary>
    public const string ChargeToGuestNotAllowed = "service_request_charge_to_guest_not_allowed";

    /// <summary>
    /// 422: the transition is not allowed from the request's current status (see
    /// <see cref="Casazen.Core.Suppliers.ServiceRequestStateMachine"/>), e.g. "paid" before "completed", "start" before the
    /// request was taken, "cancel" once the work is done. The message key says which operation was refused.
    /// </summary>
    public const string InvalidTransition = "service_request_invalid_transition";

    /// <summary>
    /// 409: another operation changed the request between the moment it was read and the save (two members of the
    /// supplier org, or two tabs, taking and rejecting it together): nothing was saved and nobody was notified.
    /// </summary>
    public const string StateChanged = "service_request_state_changed";

    /// <summary>A short-rent request without <c>bookingId</c>: in short-term rental a request is for one stay.</summary>
    public const string BookingRequired = "service_request_booking_required";

    /// <summary>The <c>bookingId</c> is not a booking of the request's property in the host's org (or does not exist).</summary>
    public const string BookingMismatch = "service_request_booking_mismatch";

    /// <summary>A long-rent request with a <c>bookingId</c>: in long-term rental a request is for the property.</summary>
    public const string BookingNotAllowed = "service_request_booking_not_allowed";

    /// <summary>
    /// 409 (SP-04): the time asked is not a free slot of the supplier's agenda (it was taken by someone else meanwhile, it is
    /// outside the working hours, in a time off or too soon). The caller picks another slot.
    /// </summary>
    public const string SlotUnavailable = "supplier_slot_unavailable";

    /// <summary>404 (SP-04): the service of the catalog is not one of the supplier's, or was deleted (the catalog's own code).</summary>
    public const string ServiceNotFound = "supplier_service_not_found";

    /// <summary>422 (SP-04): the service of the catalog is a draft or paused, so it cannot be requested.</summary>
    public const string ServiceUnavailable = "service_request_service_unavailable";

    /// <summary>422 (SP-04): the category of the request is not the category of the service of the catalog.</summary>
    public const string ServiceCategoryMismatch = "service_request_service_category_mismatch";

    /// <summary>422 (SP-04): a time was sent without a service of the catalog, so the end of the work (the duration) is unknown.</summary>
    public const string TimeNeedsService = "service_request_time_needs_service";

    /// <summary>422 (SP-04): the time is not valid (no end for a request without a service, an end that is not after the start).</summary>
    public const string TimeInvalid = "service_request_time_invalid";

    /// <summary>422 (SP-04): the supplier tries to change the time of a request that has one (it proposes another with <c>propose-time</c>).</summary>
    public const string TimeAlreadySet = "service_request_time_already_set";

    /// <summary>422 (SP-04): an amount or an extra is outside its limits.</summary>
    public const string AmountInvalid = "service_request_amount_invalid";

    /// <summary>422 (SP-04): the final amount declared on completion is lower than its own extras.</summary>
    public const string FinalAmountInvalid = "service_request_final_amount_invalid";

    /// <summary>422 (SP-04): a second reminder before <see cref="Casazen.Core.Options.ServiceRequestOptions.RemindIntervalHours"/> hours.</summary>
    public const string RemindTooSoon = "service_request_remind_too_soon";

    /// <summary>422 (SP-04): the host answers a proposal that does not exist (never made, or already answered).</summary>
    public const string NoProposal = "service_request_no_proposal";

    /// <summary>422 (SP-04): a file of the work photos is not an accepted image.</summary>
    public const string PhotoInvalid = "service_request_photo_invalid";

    /// <summary>422 (SP-04): the request already has the most photos it can keep.</summary>
    public const string PhotoLimitReached = "service_request_photo_limit_reached";

    /// <summary>404 (SP-04): no photo with that id on the request, or the file is gone from the storage.</summary>
    public const string PhotoNotFound = "service_request_photo_not_found";

    /// <summary>SharedResources keys of the messages of the codes above.</summary>
    public const string BookingRequiredMessageKey = "ServiceRequestBookingRequired";
    public const string BookingMismatchMessageKey = "ServiceRequestBookingMismatch";
    public const string BookingNotAllowedMessageKey = "ServiceRequestBookingNotAllowed";
    public const string NotFoundMessageKey = "ServiceRequestNotFound";
    public const string SupplierNotFoundMessageKey = "SupplierProfileNotFound";
    public const string PropertyNotFoundMessageKey = "PropertyNotFound";
    public const string SupplierInactiveMessageKey = "ServiceRequestSupplierInactive";
    public const string SupplierNotActiveMessageKey = "ServiceRequestSupplierNotActive";
    public const string SupplierOutsideComuneMessageKey = "ServiceRequestSupplierOutsideComune";
    public const string ChargeToGuestNotAllowedMessageKey = "ServiceRequestChargeToGuestNotAllowed";
    public const string CannotTakeMessageKey = "ServiceRequestCannotTake";
    public const string CannotRejectMessageKey = "ServiceRequestCannotReject";
    public const string CannotCompleteMessageKey = "ServiceRequestCannotComplete";
    public const string CannotMarkPaidMessageKey = "ServiceRequestCannotMarkPaid";
    public const string CannotStartMessageKey = "ServiceRequestCannotStart";
    public const string CannotCancelMessageKey = "ServiceRequestCannotCancel";
    public const string CannotProposeMessageKey = "ServiceRequestCannotPropose";
    public const string CannotRemindMessageKey = "ServiceRequestCannotRemind";
    public const string CannotAddPhotosMessageKey = "ServiceRequestCannotAddPhotos";
    public const string StateChangedMessageKey = "ServiceRequestStateChanged";
    public const string SlotUnavailableMessageKey = "SupplierSlotUnavailable";
    public const string ServiceNotFoundMessageKey = "SupplierServiceNotFound";
    public const string ServiceUnavailableMessageKey = "ServiceRequestServiceUnavailable";
    public const string ServiceCategoryMismatchMessageKey = "ServiceRequestServiceCategoryMismatch";
    public const string TimeNeedsServiceMessageKey = "ServiceRequestTimeNeedsService";
    public const string TimeInvalidMessageKey = "ServiceRequestTimeInvalid";
    public const string TimeAlreadySetMessageKey = "ServiceRequestTimeAlreadySet";
    public const string AmountInvalidMessageKey = "ServiceRequestAmountInvalid";
    public const string FinalAmountInvalidMessageKey = "ServiceRequestFinalAmountInvalid";
    public const string RemindTooSoonMessageKey = "ServiceRequestRemindTooSoon";
    public const string NoProposalMessageKey = "ServiceRequestNoProposal";
    public const string PhotoInvalidMessageKey = "ServiceRequestPhotoInvalid";
    public const string PhotoLimitReachedMessageKey = "ServiceRequestPhotoLimit";
    public const string PhotoNotFoundMessageKey = "ServiceRequestPhotoNotFound";

    /// <summary>Every <c>SharedResources</c> key the service raises (a test checks that each one exists in Italian and English).</summary>
    public static IReadOnlyList<string> MessageKeys { get; } =
    [
        BookingRequiredMessageKey,
        BookingMismatchMessageKey,
        BookingNotAllowedMessageKey,
        NotFoundMessageKey,
        SupplierNotFoundMessageKey,
        PropertyNotFoundMessageKey,
        SupplierInactiveMessageKey,
        SupplierNotActiveMessageKey,
        SupplierOutsideComuneMessageKey,
        ChargeToGuestNotAllowedMessageKey,
        CannotTakeMessageKey,
        CannotRejectMessageKey,
        CannotCompleteMessageKey,
        CannotMarkPaidMessageKey,
        CannotStartMessageKey,
        CannotCancelMessageKey,
        CannotProposeMessageKey,
        CannotRemindMessageKey,
        CannotAddPhotosMessageKey,
        StateChangedMessageKey,
        SlotUnavailableMessageKey,
        ServiceNotFoundMessageKey,
        ServiceUnavailableMessageKey,
        ServiceCategoryMismatchMessageKey,
        TimeNeedsServiceMessageKey,
        TimeInvalidMessageKey,
        TimeAlreadySetMessageKey,
        AmountInvalidMessageKey,
        FinalAmountInvalidMessageKey,
        RemindTooSoonMessageKey,
        NoProposalMessageKey,
        PhotoInvalidMessageKey,
        PhotoLimitReachedMessageKey,
        PhotoNotFoundMessageKey,
    ];
}

/// <remarks>
/// Errors (FD-05, SU-10): <see cref="Casazen.Core.Exceptions.NotFoundException"/> (404),
/// <see cref="Casazen.Core.Exceptions.DomainRuleException"/> (422) and
/// <see cref="Casazen.Core.Exceptions.DomainConflictException"/> (409) with the codes of
/// <see cref="ServiceRequestErrorCodes"/>; <see cref="UnauthorizedAccessException"/> (403) when a supplier acts on a
/// request of another supplier. The transitions follow <see cref="Casazen.Core.Suppliers.ServiceRequestStateMachine"/>
/// and are saved only if the request did not change since it was read: of two concurrent transitions one wins, the
/// other gets 409 <see cref="ServiceRequestErrorCodes.StateChanged"/>, and only the winner notifies the other party.
/// </remarks>
public interface IServiceRequestService
{
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
    /// Code <c>invalid_service_category</c>: the category is not a <see cref="Casazen.Core.Suppliers.ServiceCategories"/> code.
    /// Codes of <see cref="ServiceRequestErrorCodes"/>: the booking rules of the command's rental context (D2), a supplier
    /// that is not active or does not cover the property's comune, <c>chargeToGuest</c>, a service that is not requestable
    /// or not of the request's category, a time without a service.
    /// </exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException"><see cref="ServiceRequestErrorCodes.SlotUnavailable"/>: the time is not a free slot.</exception>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException">The property (of the host's org), the supplier or the service does not exist.</exception>
    Task<ServiceRequest> CreateAsync(CreateServiceRequestCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// The supplier takes a new request: <c>Richiesto → PresoInCarico</c>. With a <paramref name="command"/> it also sets the
    /// time of a request that has none (checked with the slot planner under the supplier's calendar lock) and the price it
    /// commits to; a pending proposal of another time is dropped.
    /// </summary>
    Task<ServiceRequest> TakeAsync(
        Guid id,
        Guid supplierOrgId,
        string userId,
        TakeServiceRequestCommand? command = null,
        CancellationToken cancellationToken = default);

    /// <summary>The supplier starts the work: <c>PresoInCarico → InCorso</c> (SP-04).</summary>
    Task<ServiceRequest> StartAsync(Guid id, Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The supplier completes the work: <c>PresoInCarico/InCorso → Completato</c>. Notes go to their own column; the final
    /// price is composed from the declared total and the extras, and flagged for the customer's confirmation when it is
    /// more than the tolerance above the quote (decision D7).
    /// </summary>
    Task<ServiceRequest> CompleteAsync(
        Guid id,
        Guid supplierOrgId,
        CompleteServiceRequestCommand? command = null,
        CancellationToken cancellationToken = default);

    Task<ServiceRequest> RejectAsync(Guid id, Guid supplierOrgId, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// The supplier cancels a request before the work started (<c>Richiesto/PresoInCarico → Annullato</c>, SP-04); the host
    /// is told. The slot it held is free again.
    /// </summary>
    Task<ServiceRequest> CancelAsSupplierAsync(Guid id, Guid supplierOrgId, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// The host cancels a request of its org up to and including the work in progress (<c>Richiesto/PresoInCarico/InCorso →
    /// Annullato</c>, SP-04); the supplier is told. Another org's request is not found.
    /// </summary>
    Task<ServiceRequest> CancelAsHostAsync(Guid id, Guid hostOrgId, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// The host reminds the supplier to answer a new request (SP-04): only while it is <c>Richiesto</c>, at most once every
    /// <see cref="Casazen.Core.Options.ServiceRequestOptions.RemindIntervalHours"/> hours; the supplier is told.
    /// </summary>
    Task<ServiceRequest> RemindAsync(Guid id, Guid hostOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The supplier proposes another time for a new request (SP-04). The request stays <c>Richiesto</c> with the proposal
    /// saved (a new proposal replaces the old one); the host is told and answers with <see cref="AcceptProposalAsync"/> or
    /// <see cref="RejectProposalAsync"/>. The proposed time is checked with the slot planner, as the time of a take is.
    /// </summary>
    Task<ServiceRequest> ProposeTimeAsync(
        Guid id,
        Guid supplierOrgId,
        string userId,
        ProposeServiceRequestTimeCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The host accepts the proposal (SP-04): the proposed time becomes the time of the request, which is taken by the
    /// supplier member who proposed it (<c>Richiesto → PresoInCarico</c>); the supplier is told. The time is checked again
    /// under the supplier's calendar lock.
    /// </summary>
    Task<ServiceRequest> AcceptProposalAsync(Guid id, Guid hostOrgId, CancellationToken cancellationToken = default);

    /// <summary>The host turns the proposal down (SP-04): the request stays <c>Richiesto</c> as it was; the supplier is told.</summary>
    Task<ServiceRequest> RejectProposalAsync(Guid id, Guid hostOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a completed request of <paramref name="hostOrgId"/> as paid; another org's request is not found. A request paid
    /// inside CasaZen (<see cref="ServiceRequestPaymentMode.Online"/>, SP-15a) cannot be marked by hand: 422
    /// <see cref="ServicePaymentErrors.OnlinePayment"/>.
    /// </summary>
    Task<ServiceRequest> MarkPaidAsync(Guid id, Guid hostOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The host confirms the final amount of a completed request that is above the quote by more than the tolerance (SP-15a,
    /// decision D7): the request no longer needs the confirmation and, when it is paid inside CasaZen, its payment is created and
    /// the link sent. Confirming twice is not an error; another org's request is not found; 422
    /// <see cref="ServicePaymentErrors.NoConfirmationNeeded"/> when there is nothing to confirm.
    /// </summary>
    Task<ServiceRequest> ConfirmFinalAmountAsync(Guid id, Guid hostOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The supplier sends the payment request of a completed request paid inside CasaZen, or reminds the payer (at most once a
    /// day), SP-15a. Behind the flag <c>SupplierOnlinePayments</c> at the endpoint. Returns the payment.
    /// </summary>
    Task<ServiceRequestPayment> RequestPaymentAsync(Guid id, Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The supplier records that it was paid outside CasaZen (SP-15a, decision D5): <c>Completato → Pagato</c>, no commission. On a
    /// request paid inside CasaZen the <paramref name="reason"/> is required (the trace of the exception). Returns the payment.
    /// </summary>
    Task<ServiceRequestPayment> RecordOfflinePaymentAsync(
        Guid id,
        Guid supplierOrgId,
        string userId,
        string? reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The supplier takes several new requests as they are (SP-04, at most <see cref="ServiceRequestLimits.MaxBatchAccept"/>):
    /// each one on its own, so one that cannot be taken (already answered, changed meanwhile, not the supplier's) does not stop
    /// the others. One result per distinct id, in the order received.
    /// </summary>
    Task<IReadOnlyList<ServiceRequestBatchResult>> AcceptManyAsync(
        Guid supplierOrgId,
        string userId,
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The supplier adds photos of the work to a request it took and has not completed (SP-04). Images only, at most
    /// <see cref="ServiceRequestLimits.MaxWorkPhotos"/> per request, in the private bucket.
    /// </summary>
    Task<ServiceRequest> AddWorkPhotosAsync(
        Guid id,
        Guid supplierOrgId,
        IReadOnlyList<IFormFile> photos,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a photo of the work of <paramref name="request"/> (already authorized by the caller) from the private bucket;
    /// <c>null</c> when the request has no such photo or the file is missing.
    /// </summary>
    Task<ServiceRequestPhotoContent?> OpenWorkPhotoAsync(ServiceRequest request, Guid photoId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A request of <paramref name="rentalContext"/> visible in the host scope (org, and the owner's properties when
    /// set); <c>null</c> otherwise, also for a request of the other rental context.
    /// </summary>
    Task<ServiceRequest?> GetByIdForHostAsync(
        Guid id,
        HostScope scope,
        ServiceRequestRentalContext rentalContext,
        CancellationToken cancellationToken = default);
    Task<ServiceRequest?> GetByIdForSupplierAsync(Guid id, Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The requests of <paramref name="rentalContext"/> in the host scope, newest first. Web and app use the same
    /// filters (SU-07): <paramref name="bookingId"/> for a stay (short-rent), <paramref name="propertyId"/> for a
    /// property (both contexts). The caller authorizes the booking or property it filters on.
    /// </summary>
    Task<(IReadOnlyList<ServiceRequest> Items, int Total)> ListForHostAsync(
        HostScope scope,
        ServiceRequestRentalContext rentalContext,
        ServiceRequestStatus? status,
        Guid? propertyId,
        Guid? bookingId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<ServiceRequest> Items, int Total)> ListForSupplierAsync(
        Guid supplierOrgId,
        bool openOnly,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}
