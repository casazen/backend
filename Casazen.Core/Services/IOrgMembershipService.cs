using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The one service that writes the org membership (AM-01): the <see cref="OrgMember"/> rows (who belongs to which org
/// and as what) <b>and</b>, in the same <c>SaveChanges</c> (one transaction), the <see cref="UserContextMembership"/> rows
/// that project the member's role into permissions (<see cref="Casazen.Core.Authorization.OrgRoleCatalog"/>). After every
/// write the user's authorization cache is invalidated: the other API instances converge within the cache duration
/// (60 s); the deactivation does not wait for it, the request tenant reads the member's status on every request.
/// </summary>
/// <remarks>
/// <para>Every write of an org takes the same advisory lock (one org at a time), so two requests never interleave on the
/// same people (the last-owner rule, AM-02's seats). Nothing here sends an email or talks to Auth0: the people of an org
/// have no Auth0 role (their rights are the DB memberships).</para>
/// <para>The owner's own host memberships (<c>property_owner</c>, <c>long_term_landlord</c>) are written by the onboarding
/// from the rental type (<see cref="IUserContextMembershipService"/>), not by this service: it adds the owner's account
/// membership and the <see cref="OrgMember"/> row, and never touches the owner's host rows.</para>
/// </remarks>
public interface IOrgMembershipService
{
    /// <summary>
    /// Makes <paramref name="userId"/> the owner of <paramref name="orgId"/> (all properties): the <see cref="OrgMember"/> row
    /// and the <c>account/org_owner</c> membership. Idempotent: an owner that already has them is left as is. Called by the
    /// onboarding right after the org is created.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException">
    /// The user is already a member of another org, or a non-owner member of this one
    /// (<see cref="OrgMembershipErrors.AlreadyMember"/>).
    /// </exception>
    Task<OrgMember> EnsureOwnerAsync(string userId, Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds <paramref name="userId"/> to <paramref name="orgId"/> as <paramref name="role"/>, working in
    /// <paramref name="rentalContexts"/> (the areas): the <see cref="OrgMember"/> row and the memberships the role implies,
    /// all or nothing. The user's <c>OrgId</c> becomes <paramref name="orgId"/> when it has none. The member gets every
    /// property of the org (the per-property scope is AM-03).
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
    /// The role is <see cref="OrgRole.Owner"/> (<see cref="OrgMembershipErrors.OwnerNotAssignable"/>: the ownership is not
    /// transferable, D15), or no valid area was given (<see cref="OrgMembershipErrors.AreaRequired"/>).
    /// </exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException">
    /// The user is already a member (<see cref="OrgMembershipErrors.AlreadyMember"/>) or belongs to another org
    /// (<see cref="OrgMembershipErrors.OtherOrg"/>).
    /// </exception>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException">The user or the org does not exist.</exception>
    Task<OrgMember> AddMemberAsync(
        string userId,
        Guid orgId,
        OrgRole role,
        IReadOnlyCollection<string> rentalContexts,
        string? createdByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gives <paramref name="userId"/> another role, keeping the contexts it works in: the account membership of the
    /// role and the role key of every rental membership are re-pointed together with the <see cref="OrgMember"/> row.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">The new role is <see cref="OrgRole.Owner"/>.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException">
    /// The member is the owner (<see cref="OrgMembershipErrors.LastOwner"/>): the owner keeps its role.
    /// </exception>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException">The user is not a member of any org.</exception>
    Task<OrgMember> ChangeRoleAsync(string userId, OrgRole role, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deactivates the member: from the next request on every call of the user answers 403 <c>member_inactive</c>
    /// (the user's account, memberships and org stay as they are, the reactivation gives everything back). Not
    /// <c>IUserService.DeactivateUserAsync</c>: that blocks the Auth0 account and is the staff's. Idempotent.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException">The member is the owner (<see cref="OrgMembershipErrors.LastOwner"/>).</exception>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException">The user is not a member of any org.</exception>
    Task<OrgMember> DeactivateAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Gives the access back to a deactivated member. Idempotent.</summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException">The user is not a member of any org.</exception>
    Task<OrgMember> ReactivateAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes the member out of the org: the <see cref="OrgMember"/> row and every membership the org gave (account and
    /// rental contexts). The user's account and its <c>OrgId</c> are not touched here.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException">The member is the owner (<see cref="OrgMembershipErrors.LastOwner"/>).</exception>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException">The user is not a member of any org.</exception>
    Task RemoveAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The reconcile command (admin, idempotent): gives an <see cref="OrgMember"/> owner row to the single owner of every
    /// host org that has none (the same rule as the backfill of the migration <c>AddOrgMembership</c>), then re-aligns the
    /// memberships with the org roles (account row, role keys of the rental rows). What it cannot decide (an org with
    /// several owner candidates, a member of a different org than its user's, a member with no area) is reported, never
    /// guessed. With <paramref name="dryRun"/> nothing is saved: the report lists what a real run would do.
    /// </summary>
    Task<OrgMembershipReconcileReport> ReconcileAsync(bool dryRun, CancellationToken cancellationToken = default);
}

/// <summary>Stable codes of the refusals of <see cref="IOrgMembershipService"/> (422 and 409), translated by the clients.</summary>
public static class OrgMembershipErrors
{
    /// <summary>409: the owner cannot be deactivated, removed or given another role: an org always keeps its owner.</summary>
    public const string LastOwner = "org_last_owner";

    /// <summary>422: the ownership is assigned only at the onboarding and is not transferable in this version (D15).</summary>
    public const string OwnerNotAssignable = "org_owner_not_assignable";

    /// <summary>422: a member works in at least one area (short-rent, long-rent).</summary>
    public const string AreaRequired = "org_member_area_required";

    /// <summary>409: the user already is a member of an org (a user belongs to one org only).</summary>
    public const string AlreadyMember = "org_member_already_member";

    /// <summary>409: the user belongs to another org (<c>User.OrgId</c>): changing org is AM-02's flow, not an add.</summary>
    public const string OtherOrg = "org_member_other_org";
}

/// <summary>Codes of <see cref="OrgMembershipFix"/> and <see cref="OrgMembershipIssue"/> (listed in the runbook).</summary>
public static class OrgMembershipReconcileCodes
{
    // Fixes: what a real run changes.
    public const string OwnerCreated = "owner_member_created";
    public const string AccountMembershipAdded = "account_membership_added";
    public const string AccountMembershipChanged = "account_membership_changed";
    public const string AccountMembershipRemoved = "account_membership_removed";
    public const string HostMembershipChanged = "host_membership_changed";

    // Issues: what needs a decision, left untouched.
    public const string OwnerAmbiguous = "org_owner_ambiguous";
    public const string UserWithoutMember = "org_user_without_member";
    public const string SeveralOwners = "org_several_owners";
    public const string OrgMismatch = "org_member_org_mismatch";
    public const string OwnerWithoutHostMembership = "owner_without_host_membership";
    public const string MemberWithoutHostMembership = "member_without_host_membership";
}

/// <summary>One change the reconcile makes (or, in a dry run, would make). Ids only, no names or emails.</summary>
public sealed record OrgMembershipFix(string Code, string UserId, Guid? OrgId, string Detail);

/// <summary>One situation the reconcile cannot decide and leaves as it is, with the users involved.</summary>
public sealed record OrgMembershipIssue(string Code, Guid? OrgId, IReadOnlyList<string> UserIds, string Detail);

/// <summary>Outcome of <see cref="IOrgMembershipService.ReconcileAsync"/>.</summary>
/// <param name="DryRun">Nothing was saved.</param>
/// <param name="HostOrgsScanned">Host orgs with at least one user.</param>
/// <param name="MembersScanned"><see cref="OrgMember"/> rows read after the owners were created.</param>
public sealed record OrgMembershipReconcileReport(
    bool DryRun,
    int HostOrgsScanned,
    int MembersScanned,
    IReadOnlyList<OrgMembershipFix> Fixes,
    IReadOnlyList<OrgMembershipIssue> Issues);
