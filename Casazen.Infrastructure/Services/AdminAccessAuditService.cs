using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class AdminAccessAuditService(ILogger<AdminAccessAuditService> logger) : IAdminAccessAuditService
{
    public Task LogPrivilegedPropertyAccessAsync(
        string actorUserId,
        Guid propertyId,
        string ownerId,
        string action,
        CancellationToken ct = default)
    {
        logger.LogWarning(
            "Privileged property access: {Event} ActorUserId={ActorUserId} PropertyId={PropertyId} OwnerId={OwnerId} Action={Action} Timestamp={Timestamp}",
            "PrivilegedPropertyAccess",
            actorUserId,
            propertyId,
            ownerId,
            action,
            DateTime.UtcNow);

        return Task.CompletedTask;
    }
}
