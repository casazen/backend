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
    Task<ServiceRequest> CreateAsync(CreateServiceRequestCommand command, CancellationToken cancellationToken = default);
    Task<ServiceRequest> TakeAsync(Guid id, Guid supplierOrgId, string userId, CancellationToken cancellationToken = default);
    Task<ServiceRequest> CompleteAsync(Guid id, Guid supplierOrgId, string? notes, CancellationToken cancellationToken = default);
    Task<ServiceRequest> RejectAsync(Guid id, Guid supplierOrgId, string reason, CancellationToken cancellationToken = default);
    Task<ServiceRequest> MarkPaidAsync(Guid id, Guid hostOrgId, string userId, CancellationToken cancellationToken = default);
    Task<ServiceRequest?> GetByIdForHostAsync(
        Guid id,
        Guid hostOrgId,
        string userId,
        IEnumerable<string> userRoles,
        CancellationToken cancellationToken = default);
    Task<ServiceRequest?> GetByIdForSupplierAsync(Guid id, Guid supplierOrgId, CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<ServiceRequest> Items, int Total)> ListForHostAsync(
        Guid orgId,
        string userId,
        IEnumerable<string> userRoles,
        ServiceRequestStatus? status,
        Guid? propertyId,
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
