using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

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
    Task<OrgTeamView> ListAsync(Guid orgId, CancellationToken cancellationToken = default);

    Task<OrgMemberView> ChangeRoleAsync(Guid orgId, Guid memberId, OrgRole role, string actorUserId, CancellationToken cancellationToken = default);

    Task<OrgMemberView> SetAreasAsync(
        Guid orgId,
        Guid memberId,
        IReadOnlyCollection<string> areas,
        string actorUserId,
        CancellationToken cancellationToken = default);

    Task<OrgMemberView> DeactivateAsync(Guid orgId, Guid memberId, string actorUserId, CancellationToken cancellationToken = default);

    Task<OrgMemberView> ReactivateAsync(Guid orgId, Guid memberId, string actorUserId, CancellationToken cancellationToken = default);

    Task RemoveAsync(Guid orgId, Guid memberId, string actorUserId, CancellationToken cancellationToken = default);
}
