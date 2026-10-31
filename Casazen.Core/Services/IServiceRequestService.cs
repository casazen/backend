using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Microsoft.AspNetCore.Http;

namespace Casazen.Core.Services;

/// <summary>
/// A host request to a supplier. <paramref name="RentalContext"/> is the context the host opened it in (D2): a
/// short-rent request is for the property and, when the host names it, one of its stays (<paramref name="BookingId"/>, SR-08a: the stay
/// is optional); a long-rent request is for the property and takes no booking. See <see cref="ServiceRequestErrorCodes"/>.
/// </summary>
/// <param name="ServiceListingId">
/// The service of the supplier's catalog the request is for (SP-04): it must be an active service of that supplier, of the
/// request's category. Gives the name, the price and the duration of the work.
/// </param>
/// <param name="ScheduledStartUtc">
/// Start of the work (UTC). With a catalog service and no <paramref name="DurationMinutes"/>, the end is the service's
/// duration and the start must be a free slot. With <paramref name="DurationMinutes"/>, the end is the start plus that
/// duration. When omitted, the service assigns Europe/Rome 10:00 on the stay check-out day (or today) so no day-only
/// request is stored.
/// </param>
/// <param name="DurationMinutes">
/// How long the work lasts, in minutes (5 to 1,440). With <paramref name="ScheduledStartUtc"/> it sets the end when no
/// catalog service is chosen. The supplier sees it and can take another job the same day only when the intervals do not
/// overlap. A full working day is occupied only when this interval covers those hours, never by omitting the time.
/// </param>
/// <param name="UseBackupSuppliers">
/// SP-BKP: whether the request is also sent to up to three backup suppliers, declared as such. <c>null</c> (the default) follows the org's
/// choice (<c>Org.SupplierBackupsEnabled</c>, on by default); <c>false</c> sends it to the chosen supplier alone, as before. It only matters
/// while the feature flag <c>SupplierRequestBackups</c> is on, and never for a request accepted automatically (SP-04d).
/// </param>
/// <param name="LeaseContractId">
/// LR-03c: the lease contract a long-rent request is made for, when the landlord asks from the sheet of that contract. It must be a lease of
/// the host's org on the request's property (404 <see cref="ServiceRequestErrorCodes.LeaseNotFound"/> for another org's or a missing one, 422
/// <see cref="ServiceRequestErrorCodes.LeaseMismatch"/> for one of another property); a short-rent request takes none (422
/// <see cref="ServiceRequestErrorCodes.LeaseNotAllowed"/>). A link only: nothing of the tenant goes to the supplier.
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
    DateTime? ScheduledStartUtc = null,
    int? DurationMinutes = null,
    bool? UseBackupSuppliers = null,
    Guid? LeaseContractId = null);

/// <summary>
/// What the supplier adds when it takes a request (SP-04): a time for a request that has none, and the price it commits to.
/// Everything is optional: a take without a body accepts the request as it is.
/// </summary>
/// <param name="ScheduledStartUtc">The start of the work when setting it at take (a request that already has a time keeps it: the supplier proposes another one with <c>propose-time</c>).</param>
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
/// A quote for a new request, sent by the supplier (SP-04b): the lines (the total is what they add up to), an optional note, and optionally
/// another time instead of the one the request already has.
/// </summary>
/// <param name="Lines">From one to twenty lines (<see cref="ServiceRequestQuoteRules.Normalize"/>).</param>
/// <param name="ScheduledStartUtc">
/// Another time for the work, offered with the quote: it replaces the time of the request only if the customer accepts. Without it the quote
/// leans on the time the request already has.
/// </param>
/// <param name="ScheduledEndUtc">The end of that other time; when left out, the start plus the duration of the request's service.</param>
public record SendServiceRequestQuoteCommand(
    IReadOnlyList<ServiceRequestQuoteLine> Lines,
    string? Note = null,
    DateTime? ScheduledStartUtc = null,
    DateTime? ScheduledEndUtc = null);

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

    /// <summary>The <c>bookingId</c> is not a booking of the request's property in the host's org (or does not exist).</summary>
    public const string BookingMismatch = "service_request_booking_mismatch";

    /// <summary>A long-rent request with a <c>bookingId</c>: in long-term rental a request is for the property.</summary>
    public const string BookingNotAllowed = "service_request_booking_not_allowed";

    /// <summary>404 (LR-03c): the <c>leaseId</c> is not a lease of the host's org (or does not exist): another org's answers like a missing one.</summary>
    public const string LeaseNotFound = "lease_not_found";

    /// <summary>422 (LR-03c): the <c>leaseId</c> is a lease of the host's org, but of another property than the request's.</summary>
    public const string LeaseMismatch = "service_request_lease_mismatch";

    /// <summary>422 (LR-03c): a request that is not a long-rent one with a lease: only a long-term rental request can be made for a contract.</summary>
    public const string LeaseNotAllowed = "service_request_lease_not_allowed";

    /// <summary>
    /// 409 (SP-04): the time asked is not a free slot of the supplier's agenda (it was taken by someone else meanwhile, it is
    /// outside the working hours, in a time off or too soon). The caller picks another slot.
    /// </summary>
    public const string SlotUnavailable = "supplier_slot_unavailable";

    /// <summary>
    /// 409: the supplier already accepted a job that overlaps this one. Two jobs on the same day are allowed when both have a
    /// time and the intervals do not overlap. A job without a time occupies its whole day, so it conflicts with anything else
    /// that day.
    /// </summary>
    public const string CommitmentConflict = "supplier_commitment_conflict";

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

    /// <summary>
    /// 422 (SP-04b): the quote is not valid: not from one to twenty lines, a line without a description or with an amount outside the bounds
    /// of a request, or a total above them.
    /// </summary>
    public const string QuoteInvalid = "service_request_quote_invalid";

    /// <summary>422 (SP-04b): the request has no quote waiting to be answered or withdrawn (never sent, withdrawn, already answered, or the request moved on).</summary>
    public const string NoQuote = "service_request_no_quote";

    /// <summary>
    /// 409 (SP-04b): the supplier replaced or withdrew the quote after the customer read it, so the answer is not about the quote that waits:
    /// nothing was changed, and the customer reads the quote again.
    /// </summary>
    public const string QuoteChanged = "service_request_quote_changed";

    /// <summary>422 (SP-04b): a file of the work photos is not an accepted image.</summary>
    public const string PhotoInvalid = "service_request_photo_invalid";

    /// <summary>422 (SP-04): the request already has the most photos it can keep.</summary>
    public const string PhotoLimitReached = "service_request_photo_limit_reached";

    /// <summary>404 (SP-04): no photo with that id on the request, or the file is gone from the storage.</summary>
    public const string PhotoNotFound = "service_request_photo_not_found";

    /// <summary>SharedResources keys of the messages of the codes above.</summary>
    public const string BookingMismatchMessageKey = "ServiceRequestBookingMismatch";
    public const string BookingNotAllowedMessageKey = "ServiceRequestBookingNotAllowed";
    public const string LeaseNotFoundMessageKey = "ServiceRequestLeaseNotFound";
    public const string LeaseMismatchMessageKey = "ServiceRequestLeaseMismatch";
    public const string LeaseNotAllowedMessageKey = "ServiceRequestLeaseNotAllowed";
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
    public const string CommitmentConflictMessageKey = "SupplierCommitmentConflict";
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
    public const string CannotQuoteMessageKey = "ServiceRequestCannotQuote";
    public const string QuoteInvalidMessageKey = "ServiceRequestQuoteInvalid";
    public const string NoQuoteMessageKey = "ServiceRequestNoQuote";
    public const string QuoteChangedMessageKey = "ServiceRequestQuoteChanged";
    public const string PhotoInvalidMessageKey = "ServiceRequestPhotoInvalid";
    public const string PhotoLimitReachedMessageKey = "ServiceRequestPhotoLimit";
    public const string PhotoNotFoundMessageKey = "ServiceRequestPhotoNotFound";

    /// <summary>Every <c>SharedResources</c> key the service raises (a test checks that each one exists in Italian and English).</summary>
    public static IReadOnlyList<string> MessageKeys { get; } =
    [
        BookingMismatchMessageKey,
        BookingNotAllowedMessageKey,
        LeaseNotFoundMessageKey,
        LeaseMismatchMessageKey,
        LeaseNotAllowedMessageKey,
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
        CommitmentConflictMessageKey,
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
        CannotQuoteMessageKey,
        QuoteInvalidMessageKey,
        NoQuoteMessageKey,
        QuoteChangedMessageKey,
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
    /// <summary>
    /// A host's request to a supplier: <c>Richiesto</c>, waiting for the supplier's answer. <b>Unless</b> the supplier turned on the
    /// automatic acceptance of its regular hosts and the request qualifies (SP-04d, <see cref="Casazen.Core.Suppliers.ServiceRequestAutoAccept"/>):
    /// then it is saved already <c>PresoInCarico</c> with <see cref="ServiceRequest.AutoAccepted"/>, in the same transaction that
    /// checked the slot, and the host and the supplier are told of the take.
    /// </summary>
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
    /// The supplier answers a new request with a quote (SP-04b): the request stays <c>Richiesto</c> with the quote waiting for the customer
    /// (the host, or the private customer of a showcase request), who is told. A new quote replaces the one that waited, and takes the place
    /// of a proposed time (one offer at a time). Another time offered with the quote is checked with the planner under the supplier's calendar
    /// lock, as the time of a proposal is, and does not hold the slot. A showcase customer has
    /// <c>Suppliers:Showcase:ProposalResponseMinutes</c> to answer; a host has no deadline. 422 <see cref="ServiceRequestErrorCodes.QuoteInvalid"/>.
    /// </summary>
    Task<ServiceRequest> SendQuoteAsync(
        Guid id,
        Guid supplierOrgId,
        string userId,
        SendServiceRequestQuoteCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The supplier takes its quote back (SP-04b): the request is "to answer" again. 422 <see cref="ServiceRequestErrorCodes.NoQuote"/> when none waits.
    /// </summary>
    Task<ServiceRequest> WithdrawQuoteAsync(Guid id, Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The host accepts the quote that waits (SP-04b): the request is taken on the supplier's behalf (<c>Richiesto → PresoInCarico</c>) at the
    /// time of the quote, with its total as the agreed price and its lines as the price lines (kind <c>quote</c>); the supplier is told. The
    /// time is checked again under the supplier's calendar lock: if it is gone, 409 and the quote stays. <paramref name="quoteSentAt"/> is
    /// the version the host read: another one is 409 <see cref="ServiceRequestErrorCodes.QuoteChanged"/>. Another org's request is not found.
    /// </summary>
    Task<ServiceRequest> AcceptQuoteAsync(Guid id, Guid hostOrgId, DateTime quoteSentAt, CancellationToken cancellationToken = default);

    /// <summary>The host turns the quote down (SP-04b): it goes, the request stays <c>Richiesto</c> and the supplier starts again; the supplier is told.</summary>
    Task<ServiceRequest> RejectQuoteAsync(Guid id, Guid hostOrgId, DateTime quoteSentAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a completed request of <paramref name="hostOrgId"/> as paid; another org's request is not found. A request paid
    /// inside CasaZen (<see cref="ServiceRequestPaymentMode.Online"/>, SP-15a) cannot be marked by hand: 422
    /// <see cref="ServicePaymentErrors.OnlinePayment"/>.
    /// </summary>
    Task<ServiceRequest> MarkPaidAsync(Guid id, Guid hostOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// SP-15c-b, "Tutto a posto": the host (the org that asked for the work) says the work is fine, and the money CasaZen holds for it goes to the
    /// supplier at once, without waiting for the time after the charge (decision VT-D1). Only for a completed request paid inside CasaZen whose payer
    /// has paid: 422 <see cref="ServicePaymentErrors.NotConfirmable"/> otherwise (nothing to release yet, already closed, stopped by a dispute). The
    /// host's word stands even if the transfer cannot be made right now: the job makes it. Asking again once the supplier was paid answers the
    /// request as it is and does nothing. Another org's request, and one from a supplier's showcase, are not found.
    /// </summary>
    Task<ServiceRequest> ConfirmOkAsHostAsync(Guid id, Guid hostOrgId, CancellationToken cancellationToken = default);

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
    /// property (both contexts). The caller authorizes the booking or property it filters on. <paramref name="view"/>
    /// (LR-08a) keeps the open or the closed requests (<see cref="ServiceRequestHostView"/>); <c>null</c> keeps all of them,
    /// and it narrows <paramref name="status"/> when both are given. <paramref name="leaseContractId"/> (LR-03c) keeps the requests made
    /// for one lease: it only narrows the list already limited to the scope, so a lease of another org gives an empty one.
    /// </summary>
    Task<(IReadOnlyList<ServiceRequest> Items, int Total)> ListForHostAsync(
        HostScope scope,
        ServiceRequestRentalContext rentalContext,
        ServiceRequestStatus? status,
        Guid? propertyId,
        Guid? bookingId,
        int page,
        int pageSize,
        ServiceRequestHostView? view = null,
        Guid? leaseContractId = null,
        CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<ServiceRequest> Items, int Total)> ListForSupplierAsync(
        Guid supplierOrgId,
        bool openOnly,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    // ─── Backup suppliers (SP-BKP) ───

    /// <summary>
    /// A backup supplier says it would take the job if the primary does not (the pre-approval, <c>POST api/service-requests/{id}/accept-as-backup</c>):
    /// the request stays <c>Richiesto</c> and holds no slot, but if the primary does not take the job in its window this backup, the earliest
    /// to accept, has it. Accepting twice is not an error. 422 <see cref="ServiceRequestGroupErrorCodes.NotAWaitingBackup"/> for a request that is
    /// not a backup waiting; 422 <see cref="ServiceRequestGroupErrorCodes.BackupAlreadyActive"/> in the second window (take the job instead); 409
    /// <see cref="ServiceRequestErrorCodes.SlotUnavailable"/> when its time is no longer free for the supplier.
    /// </summary>
    Task<ServiceRequest> AcceptAsBackupAsync(
        Guid id,
        Guid supplierOrgId,
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One pass over a group of backup suppliers, under its lock: closes it if the primary took the job or the host cancelled, hands the job
    /// to the earliest backup that accepted when the primary is out (its slot checked again; the next one when it is not free), opens the second
    /// window when none could, and lapses the requests when it has passed. Idempotent; called by the recurring job <c>service-request-groups</c>
    /// and right after a refusal or a cancellation of a member. Never throws for a group it cannot handle: it answers
    /// <see cref="ServiceRequestGroupResolution.Conflict"/> and the next pass looks again.
    /// </summary>
    Task<ServiceRequestGroupResolution> ResolveGroupAsync(Guid groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The groups with the given ids as the host org <paramref name="hostOrgId"/> sees them: where each is, the primary and the backups with
    /// their state, and who took the job. Only groups of that org (another org's id is simply missing from the answer).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ServiceRequestGroupView>> GetGroupViewsAsync(
        Guid hostOrgId,
        IReadOnlyCollection<Guid> groupIds,
        CancellationToken cancellationToken = default);
}
