using Casazen.Core.OrgTeam;

namespace Casazen.Core.Services;

public interface IOrgRoleMatrixService
{
    Task<IReadOnlyList<OrgRoleView>> GetAsync(Guid orgId, string callerUserId, CancellationToken cancellationToken = default);
}
