namespace Casazen.Core.Authorization;

/// <summary>
/// The caller's reach on host data for list and lookup queries (TN-3): the rows of <see cref="OrgId"/> and, when
/// <see cref="OwnerId"/> is set, only those bound to properties that user owns. Built by the web layer from the
/// authenticated principal (org-wide roles in <see cref="HostRoles.OrgWide"/>), never from client input, so services
/// filter in SQL without deciding on roles themselves.
/// </summary>
/// <param name="OrgId">The caller's org.</param>
/// <param name="OwnerId">Restrict to properties owned by this user; <c>null</c> = the whole org.</param>
public sealed record HostScope(Guid OrgId, string? OwnerId)
{
    public bool IsOrgWide => OwnerId is null;
}
