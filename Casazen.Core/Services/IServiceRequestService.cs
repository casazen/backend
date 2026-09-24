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

/// <summary>Stable codes (ProblemDetails <c>code</c>, HTTP 422) of the D2 rules on what a request is tied to (SU-07).</summary>
public static class ServiceRequestErrorCodes
{
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
}

public interface IServiceRequestService
{
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
    /// Code <c>invalid_service_category</c>: the category is not a <see cref="Casazen.Core.Suppliers.ServiceCategories"/> code.
    /// Codes of <see cref="ServiceRequestErrorCodes"/>: the booking rules of the command's rental context (D2).
    /// </exception>
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

public class ServiceRequestStateException(string message) : InvalidOperationException(message);
