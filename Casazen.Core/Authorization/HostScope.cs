namespace Casazen.Core.Authorization;

/// <summary>
/// The caller's reach on host data for list and lookup queries (TN-3): the rows of <see cref="OrgId"/> and, when the scope
/// is restricted, only those bound to the properties the caller reaches. Built by <see cref="IHostScopeResolver"/> from the
/// caller's org membership (AM-03), never from client input, so services filter in SQL without deciding on roles
/// themselves (<c>HostScopeQueryExtensions.InScope</c>).
/// </summary>
/// <remarks>
/// <para>Three shapes, never combined by the resolver: <b>org-wide</b> (both restrictions <c>null</c>: the owner, the
/// administrators, the property managers, the accountants, a collaborator with every property, and a legacy account with
/// an org-wide JWT role); <b>owned</b> (<see cref="OwnerId"/>: the properties the user created, the reach of an account
/// that is in no org team, as it has always been); <b>granted</b> (<see cref="GrantedToUserId"/>: the properties that have a
/// <see cref="Casazen.Core.Entities.PropertyMemberAccess"/> row for the user, the collaborator «Solo alcuni»). If both were
/// set a row would have to satisfy both.</para>
/// <para><b>Granted and area (AM-02c).</b> The properties a collaborator «Solo alcuni» was given are reached only while their
/// mode corresponds to an area the collaborator works in: with <see cref="GrantedMode"/> set, a granted property of the other
/// mode stays given (the rows are kept, so giving the area back gives the properties back) and is not reached. It applies to this
/// shape only: the other roles reach every property of the org, as they always did, and what they may do on it is decided by the
/// permissions of their contexts.</para>
/// </remarks>
/// <param name="OrgId">The caller's org.</param>
/// <param name="OwnerId">Restrict to properties created by this user; <c>null</c> = no such restriction.</param>
/// <param name="GrantedToUserId">Restrict to properties granted to this member; <c>null</c> = no such restriction.</param>
/// <param name="GrantedMode">
/// With <paramref name="GrantedToUserId"/>: only the granted properties of this rental mode (the member works in the area of
/// that mode and not in the other one); <c>null</c> = both modes (the member works in both areas, or in none that is known).
/// </param>
public sealed record HostScope(
    Guid OrgId,
    string? OwnerId = null,
    string? GrantedToUserId = null,
    Casazen.Core.Entities.Enums.RentalMode? GrantedMode = null)
{
    /// <summary>The whole org: no property restriction at all.</summary>
    public bool IsOrgWide => OwnerId is null && GrantedToUserId is null;
}
