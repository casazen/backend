using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>A member of the org as the team page shows it (name and email come from the account).</summary>
/// <param name="Id">The member row (<c>OrgMember.Id</c>): what the endpoints address, not the account id.</param>
/// <param name="UserId">The account (<c>User.Id</c>).</param>
/// <param name="Areas">The rental contexts the person works in, from its memberships (<c>short-rent</c>, <c>long-rent</c>).</param>
public sealed record OrgMemberView(
    Guid Id,
    string UserId,
    string Email,
    string FirstName,
    string LastName,
    OrgRole Role,
    OrgMemberStatus Status,
    PropertyScope PropertyScope,
    IReadOnlyList<string> Areas,
    DateTime CreatedAt,
    DateTime? DeactivatedAt);

/// <summary>The org's people and its seats, in one read.</summary>
public sealed record OrgTeamView(IReadOnlyList<OrgMemberView> Members, OrgSeatUsage Seats);

/// <summary>
/// What the owner and the administrators do with the people already in the org (AM-02): list them, change a role,
/// deactivate, reactivate and remove. The invitations are <see cref="IOrgInvitationService"/>; the writes of the member and
/// of its memberships are <see cref="IOrgMembershipService"/>, which this service calls after the rules of who may do what.
/// </summary>
/// <remarks>
/// <para><b>Who may do what (no escalation, D15).</b> The owner manages everybody but itself; an administrator manages the
/// property managers, collaborators and accountants and cannot make or touch an administrator
/// (<see cref="OrgInvitationErrors.OwnerRequired"/>); nobody becomes owner and the owner keeps its role, stays active and
/// is never removed (<see cref="OrgMembershipErrors.LastOwner"/>).</para>
/// <para><b>Seats.</b> A deactivated member holds no seat; reactivating one takes it again, under the seats lock, and
/// answers 409 <see cref="OrgSeatErrors.LimitReached"/> when the plan has none left.</para>
/// </remarks>
public interface IOrgTeamService
{
    /// <summary>The org's members (owner first, then by name) with its seats.</summary>
    Task<OrgTeamView> ListAsync(Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>Gives the member another role, keeping the areas it works in.</summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException"><see cref="OrgInvitationErrors.MemberNotFound"/>.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainForbiddenException"><see cref="OrgInvitationErrors.OwnerRequired"/>.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException"><see cref="OrgMembershipErrors.OwnerNotAssignable"/>.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException"><see cref="OrgMembershipErrors.LastOwner"/>.</exception>
    Task<OrgMemberView> ChangeRoleAsync(Guid orgId, Guid memberId, OrgRole role, string actorUserId, CancellationToken cancellationToken = default);

    /// <summary>Switches the member's access off at once (<c>member_inactive</c> from the next request) and frees its seat.</summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException"><see cref="OrgInvitationErrors.MemberNotFound"/>.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainForbiddenException"><see cref="OrgInvitationErrors.OwnerRequired"/>.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException"><see cref="OrgMembershipErrors.LastOwner"/>.</exception>
    Task<OrgMemberView> DeactivateAsync(Guid orgId, Guid memberId, string actorUserId, CancellationToken cancellationToken = default);

    /// <summary>Gives the access back; takes a seat again.</summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException"><see cref="OrgInvitationErrors.MemberNotFound"/>.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainForbiddenException"><see cref="OrgInvitationErrors.OwnerRequired"/>.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException"><see cref="OrgSeatErrors.LimitReached"/>.</exception>
    Task<OrgMemberView> ReactivateAsync(Guid orgId, Guid memberId, string actorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes the person out of the org for good: the member row, every membership the org gave and the link of the account
    /// to the org (it can onboard its own org afterwards). Frees the seat.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException"><see cref="OrgInvitationErrors.MemberNotFound"/>.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainForbiddenException"><see cref="OrgInvitationErrors.OwnerRequired"/>.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException"><see cref="OrgMembershipErrors.LastOwner"/>.</exception>
    Task RemoveAsync(Guid orgId, Guid memberId, string actorUserId, CancellationToken cancellationToken = default);
}
