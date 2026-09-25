using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// A host request to a supplier. <paramref name="RentalContext"/> is the context the host opened it in (D2): a
/// short-rent request is for one stay and needs <paramref name="BookingId"/>; a long-rent request is for the property
/// and takes no booking. See <see cref="ServiceRequestErrorCodes"/>.
/// </summary>
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
    ServiceRequestRentalContext RentalContext = ServiceRequestRentalContext.ShortRent);

/// <summary>
/// Stable codes (ProblemDetails <c>code</c>) of the service request errors: the D2 rules on what a request is tied to
/// (SU-07, HTTP 422), and the lookups, rules and state machine of SU-10 (FD-05: 404 / 422 / 409).
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

    /// <summary>422: <c>chargeToGuest</c> is not available (the backend never charges the guest for a supplier).</summary>
    public const string ChargeToGuestNotAllowed = "service_request_charge_to_guest_not_allowed";

    /// <summary>
    /// 422: the transition is not allowed from the request's current status (see
    /// <see cref="Casazen.Core.Suppliers.ServiceRequestStateMachine"/>), e.g. "paid" before "completed".
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

    /// <summary>SharedResources keys of the messages of the codes above.</summary>
    public const string BookingRequiredMessageKey = "ServiceRequestBookingRequired";
    public const string BookingMismatchMessageKey = "ServiceRequestBookingMismatch";
    public const string BookingNotAllowedMessageKey = "ServiceRequestBookingNotAllowed";
    public const string NotFoundMessageKey = "ServiceRequestNotFound";
    public const string SupplierNotFoundMessageKey = "SupplierProfileNotFound";
    public const string PropertyNotFoundMessageKey = "PropertyNotFound";
    public const string SupplierInactiveMessageKey = "ServiceRequestSupplierInactive";
    public const string SupplierOutsideComuneMessageKey = "ServiceRequestSupplierOutsideComune";
    public const string ChargeToGuestNotAllowedMessageKey = "ServiceRequestChargeToGuestNotAllowed";
    public const string CannotTakeMessageKey = "ServiceRequestCannotTake";
    public const string CannotRejectMessageKey = "ServiceRequestCannotReject";
    public const string CannotCompleteMessageKey = "ServiceRequestCannotComplete";
    public const string CannotMarkPaidMessageKey = "ServiceRequestCannotMarkPaid";
    public const string StateChangedMessageKey = "ServiceRequestStateChanged";
}

/// <remarks>
/// Errors (FD-05, SU-10): <see cref="Casazen.Core.Exceptions.NotFoundException"/> (404),
/// <see cref="Casazen.Core.Exceptions.DomainRuleException"/> (422) and
/// <see cref="Casazen.Core.Exceptions.DomainConflictException"/> (409) with the codes of
/// <see cref="ServiceRequestErrorCodes"/>; <see cref="UnauthorizedAccessException"/> (403) when a supplier acts on a
/// request of another supplier. The transitions follow <see cref="Casazen.Core.Suppliers.ServiceRequestStateMachine"/>
/// and are saved only if the request did not change since it was read: of two concurrent transitions one wins, the
/// other gets 409 <see cref="ServiceRequestErrorCodes.StateChanged"/>, and only the winner notifies the host.
/// </remarks>
public interface IServiceRequestService
{
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
    /// Code <c>invalid_service_category</c>: the category is not a <see cref="Casazen.Core.Suppliers.ServiceCategories"/> code.
    /// Codes of <see cref="ServiceRequestErrorCodes"/>: the booking rules of the command's rental context (D2), a supplier
    /// that is not active or does not cover the property's comune, <c>chargeToGuest</c>.
    /// </exception>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException">The property (of the host's org) or the supplier does not exist.</exception>
    Task<ServiceRequest> CreateAsync(CreateServiceRequestCommand command, CancellationToken cancellationToken = default);
    Task<ServiceRequest> TakeAsync(Guid id, Guid supplierOrgId, string userId, CancellationToken cancellationToken = default);
    Task<ServiceRequest> CompleteAsync(Guid id, Guid supplierOrgId, string? notes, CancellationToken cancellationToken = default);
    Task<ServiceRequest> RejectAsync(Guid id, Guid supplierOrgId, string reason, CancellationToken cancellationToken = default);
    /// <summary>Marks a completed request of <paramref name="hostOrgId"/> as paid; another org's request is not found.</summary>
    Task<ServiceRequest> MarkPaidAsync(Guid id, Guid hostOrgId, CancellationToken cancellationToken = default);

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
