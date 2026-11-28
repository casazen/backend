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
/// <param name="AgreedAmountCents">
/// E24/F01/US01/T02: the price the host agreed with the supplier beforehand (the cleaning at check-out of a property with a default cleaner), in cents, from 1 cent
/// to <see cref="Casazen.Core.Suppliers.ServiceRequestLimits.MaxAmountCents"/> (422 <c>service_request_amount_invalid</c> otherwise). It is the estimated amount of the
/// request, in place of the one of a catalog service; <c>null</c> (the default) leaves the amount to the service, as before.
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
    Guid? LeaseContractId = null,
    int? AgreedAmountCents = null);

public record TakeServiceRequestCommand(
    DateTime? ScheduledStartUtc = null,
    DateTime? ScheduledEndUtc = null,
    int? QuotedAmountCents = null);

public record CompleteServiceRequestCommand(
    string? Notes = null,
    int? FinalAmountCents = null,
    IReadOnlyList<ServiceRequestExtra>? Extras = null);

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

public sealed record ServiceRequestBatchResult(
    Guid Id,
    bool Accepted,
    ServiceRequestStatus? Status,
    string? Code,
    string? MessageKey,
    IReadOnlyList<object> MessageArgs);

public sealed record ServiceRequestPhotoContent(Stream Content, string ContentType, string FileName);

public static class ServiceRequestErrorCodes
{
    public const string NotFound = "service_request_not_found";

    public const string SupplierNotFound = "supplier_not_found";

    public const string PropertyNotFound = "property_not_found";

    public const string SupplierInactive = "service_request_supplier_inactive";

    public const string SupplierOutsideComune = "service_request_supplier_outside_comune";

    public const string SupplierNotActive = "service_request_supplier_not_active";

    public const string ChargeToGuestNotAllowed = "service_request_charge_to_guest_not_allowed";

    public const string InvalidTransition = "service_request_invalid_transition";

    public const string StateChanged = "service_request_state_changed";

    public const string BookingMismatch = "service_request_booking_mismatch";

    public const string BookingNotAllowed = "service_request_booking_not_allowed";

    public const string LeaseNotFound = "lease_not_found";

    public const string LeaseMismatch = "service_request_lease_mismatch";

    public const string LeaseNotAllowed = "service_request_lease_not_allowed";

    public const string SlotUnavailable = "supplier_slot_unavailable";

    public const string CommitmentConflict = "supplier_commitment_conflict";

    public const string ServiceNotFound = "supplier_service_not_found";

    public const string ServiceUnavailable = "service_request_service_unavailable";

    public const string ServiceCategoryMismatch = "service_request_service_category_mismatch";

    public const string TimeNeedsService = "service_request_time_needs_service";

    public const string TimeInvalid = "service_request_time_invalid";

    public const string TimeAlreadySet = "service_request_time_already_set";

    public const string AmountInvalid = "service_request_amount_invalid";

    public const string FinalAmountInvalid = "service_request_final_amount_invalid";

    public const string RemindTooSoon = "service_request_remind_too_soon";

    public const string NoProposal = "service_request_no_proposal";

    public const string QuoteInvalid = "service_request_quote_invalid";

    public const string NoQuote = "service_request_no_quote";

    public const string QuoteChanged = "service_request_quote_changed";

    public const string PhotoInvalid = "service_request_photo_invalid";

    public const string PhotoLimitReached = "service_request_photo_limit_reached";

    public const string PhotoNotFound = "service_request_photo_not_found";

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
    public const string CannotRescheduleMessageKey = "ServiceRequestCannotReschedule";
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
        CannotRescheduleMessageKey,
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

    Task<ServiceRequest> StartAsync(Guid id, Guid supplierOrgId, CancellationToken cancellationToken = default);

    Task<ServiceRequest> CompleteAsync(
        Guid id,
        Guid supplierOrgId,
        CompleteServiceRequestCommand? command = null,
        CancellationToken cancellationToken = default);

    Task<ServiceRequest> RejectAsync(Guid id, Guid supplierOrgId, string reason, CancellationToken cancellationToken = default);

    Task<ServiceRequest> CancelAsSupplierAsync(Guid id, Guid supplierOrgId, string reason, CancellationToken cancellationToken = default);

    Task<ServiceRequest> CancelAsHostAsync(Guid id, Guid hostOrgId, string reason, CancellationToken cancellationToken = default);

    Task<ServiceRequest> RemindAsync(Guid id, Guid hostOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The host moves a request of its org that nobody answered to another time (E24/F01/US01/T02: the dates of the stay of an automatic cleaning changed): only
    /// while it is <c>Richiesto</c>, with no backup group, no proposed time and no quote waiting (422 <c>service_request_invalid_transition</c> otherwise: the host
    /// then decides with the existing actions). The new interval is checked as a take is, under the supplier's calendar lock: it must not overlap a job the supplier
    /// already took (409 <c>supplier_commitment_conflict</c>). The request stays as it is, with the new time; saved only if nobody changed it since it was read
    /// (409 <c>service_request_state_changed</c>). Another org's request, and one from a supplier's showcase, is not found.
    /// </summary>
    Task<ServiceRequest> RescheduleAsHostAsync(
        Guid id,
        Guid hostOrgId,
        DateTime startUtc,
        DateTime endUtc,
        CancellationToken cancellationToken = default);

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

    Task<ServiceRequest> WithdrawQuoteAsync(Guid id, Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The host accepts the quote that waits (SP-04b): the request is taken on the supplier's behalf (<c>Richiesto → PresoInCarico</c>) at the
    /// time of the quote, with its total as the agreed price and its lines as the price lines (kind <c>quote</c>); the supplier is told. The
    /// time is checked again under the supplier's calendar lock: if it is gone, 409 and the quote stays. <paramref name="quoteSentAt"/> is
    /// the version the host read: another one is 409 <see cref="ServiceRequestErrorCodes.QuoteChanged"/>. Another org's request is not found.
    /// </summary>
    Task<ServiceRequest> AcceptQuoteAsync(Guid id, Guid hostOrgId, DateTime quoteSentAt, CancellationToken cancellationToken = default);

    Task<ServiceRequest> RejectQuoteAsync(Guid id, Guid hostOrgId, DateTime quoteSentAt, CancellationToken cancellationToken = default);

    Task<ServiceRequest> MarkPaidAsync(Guid id, Guid hostOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// SP-15c-b, "Tutto a posto": the host (the org that asked for the work) says the work is fine, and the money CasaZen holds for it goes to the
    /// supplier at once, without waiting for the time after the charge (decision VT-D1). Only for a completed request paid inside CasaZen whose payer
    /// has paid: 422 <see cref="ServicePaymentErrors.NotConfirmable"/> otherwise (nothing to release yet, already closed, stopped by a dispute). The
    /// host's word stands even if the transfer cannot be made right now: the job makes it. Asking again once the supplier was paid answers the
    /// request as it is and does nothing. Another org's request, and one from a supplier's showcase, are not found.
    /// </summary>
    Task<ServiceRequest> ConfirmOkAsHostAsync(Guid id, Guid hostOrgId, CancellationToken cancellationToken = default);

    Task<ServiceRequest> ConfirmFinalAmountAsync(Guid id, Guid hostOrgId, CancellationToken cancellationToken = default);

    Task<ServiceRequestPayment> RequestPaymentAsync(Guid id, Guid supplierOrgId, CancellationToken cancellationToken = default);

    Task<ServiceRequestPayment> RecordOfflinePaymentAsync(
        Guid id,
        Guid supplierOrgId,
        string userId,
        string? reason,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ServiceRequestBatchResult>> AcceptManyAsync(
        Guid supplierOrgId,
        string userId,
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default);

    Task<ServiceRequest> AddWorkPhotosAsync(
        Guid id,
        Guid supplierOrgId,
        IReadOnlyList<IFormFile> photos,
        CancellationToken cancellationToken = default);

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

    Task<IReadOnlyDictionary<Guid, ServiceRequestGroupView>> GetGroupViewsAsync(
        Guid hostOrgId,
        IReadOnlyCollection<Guid> groupIds,
        CancellationToken cancellationToken = default);
}
