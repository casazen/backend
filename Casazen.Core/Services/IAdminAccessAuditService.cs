namespace Casazen.Core.Services;

public interface IAdminAccessAuditService
{
    Task LogPrivilegedPropertyAccessAsync(
        string actorUserId,
        Guid propertyId,
        string ownerId,
        string action,
        CancellationToken ct = default);
}
