using Casazen.Core.OrgTeam;

namespace Casazen.Core.Services;

/// <summary>
/// The matrix of the org roles for the team screens (AM-02c, <c>GET /api/orgs/me/roles</c>): what each of the five roles
/// holds in each context and how much of each capability that is (<see cref="OrgCapabilityCatalog"/>). Read-only, built from
/// the permissions of the seeded roles at the moment of the request.
/// </summary>
public interface IOrgRoleMatrixService
{
    /// <summary>
    /// The five roles, owner first. <c>Assignable</c> is relative to the caller (the owner gives the administrator role, an
    /// administrator does not, nobody gives the owner role). Any active member of the org may ask: the matrix is the product's,
    /// it names no person.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The caller is not an active member of the org (a generic 403).</exception>
    Task<IReadOnlyList<OrgRoleView>> GetAsync(Guid orgId, string callerUserId, CancellationToken cancellationToken = default);
}
