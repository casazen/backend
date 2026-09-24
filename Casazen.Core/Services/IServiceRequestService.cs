using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public record CreateServiceRequestCommand(
    Guid OrgId,
    string UserId,
    Guid PropertyId,
    Guid? BookingId,
    Guid SupplierOrgId,
    string Category,
    ServiceRequestUrgency Urgency,
    string? Notes,
    bool ChargeToGuest);

public interface IServiceRequestService
{
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
    /// Code <c>invalid_service_category</c>: the category is not a <see cref="Casazen.Core.Services.ServiceCategories"/> code.
    /// </exception>
    Task<ServiceRequest> CreateAsync(CreateServiceRequestCommand command, CancellationToken cancellationToken = default);
    Task<ServiceRequest> TakeAsync(Guid id, Guid supplierOrgId, string userId, CancellationToken cancellationToken = default);
    Task<ServiceRequest> CompleteAsync(Guid id, Guid supplierOrgId, string? notes, CancellationToken cancellationToken = default);
    Task<ServiceRequest> RejectAsync(Guid id, Guid supplierOrgId, string reason, CancellationToken cancellationToken = default);
    /// <summary>Marks a completed request of <paramref name="hostOrgId"/> as paid; another org's request is not found.</summary>
    Task<ServiceRequest> MarkPaidAsync(Guid id, Guid hostOrgId, CancellationToken cancellationToken = default);

    /// <summary>A request visible in the host scope (org, and the owner's properties when set); <c>null</c> otherwise.</summary>
    Task<ServiceRequest?> GetByIdForHostAsync(Guid id, HostScope scope, CancellationToken cancellationToken = default);
    Task<ServiceRequest?> GetByIdForSupplierAsync(Guid id, Guid supplierOrgId, CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<ServiceRequest> Items, int Total)> ListForHostAsync(
        HostScope scope,
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
