namespace Casazen.Core.Authorization;

/// <summary>
/// What the caller reaches on the host data of its org (AM-03): the one answer to «which properties?», read from the
/// database (the authorization snapshot, cached for <c>Authorization:UserCacheSeconds</c>, 60 s by default) and not from the
/// token. Replaces <c>GetHostScope</c>, which looked at the JWT roles alone, so a member who exists only in the database saw
/// nothing and the owner saw only the properties it had created.
/// </summary>
/// <remarks>
/// <para><b>The rule.</b> A person with an <see cref="Casazen.Core.Entities.OrgMember"/> row is decided by that row and
/// never by the token: the owner, the administrators, the property managers and the accountants reach every property of the
/// org; a collaborator with <see cref="Casazen.Core.Entities.Enums.PropertyScope.All"/> too; a collaborator with
/// <see cref="Casazen.Core.Entities.Enums.PropertyScope.Selected"/> («Solo alcuni») only the properties it was given
/// (<see cref="Casazen.Core.Entities.PropertyMemberAccess"/>), none while it was given none. A deactivated member, or one of
/// another org, reaches nothing. A person with no <see cref="Casazen.Core.Entities.OrgMember"/> row (an owner from before the
/// team, a test account) keeps the old rule: org-wide with a <see cref="HostRoles.OrgWide"/> token role, otherwise the
/// properties it created.</para>
/// <para><b>Freshness.</b> The writes of the grants invalidate the cache of the member on the instance that handles them; the
/// other API instances converge within the cache duration (60 s). What may be that late on them is the decision itself (is the
/// member «Solo alcuni» or not) and the single-resource checks (<see cref="CanReachPropertyAsync"/>, which reads the granted
/// ids from the snapshot too). The lists read the grants in SQL (<c>InScope</c>): once the scope is decided as restricted, a
/// grant given or taken away is seen by the very next query.</para>
/// </remarks>
public interface IHostScopeResolver
{
    /// <summary>
    /// The scope of <paramref name="userId"/> on the host data of <paramref name="orgId"/>, or <c>null</c> when it has
    /// none: the account is inactive, a deactivated member, a member of another org. <c>null</c> is never widened to the
    /// whole org by the callers.
    /// </summary>
    /// <param name="jwtRoles">The roles of the token: only the people with no org membership row are decided by them.</param>
    Task<HostScope?> ResolveAsync(
        string userId,
        IReadOnlySet<string> jwtRoles,
        Guid orgId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// True when <paramref name="userId"/> reaches the property of a row: every property of the org, the ones it was given,
    /// or, for an account in no org team, the ones it created (<paramref name="propertyOwnerId"/>). A member «Solo alcuni»
    /// needs <paramref name="propertyId"/> and is refused without it (fail closed).
    /// </summary>
    Task<bool> CanReachPropertyAsync(
        string userId,
        IReadOnlySet<string> jwtRoles,
        Guid orgId,
        Guid? propertyId,
        string? propertyOwnerId,
        CancellationToken cancellationToken = default);
}
