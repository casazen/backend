namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where an invitation to an org stands (<see cref="OrgInvitation.Status"/>, AM-02). Persisted as int, explicit values:
/// append-only, never reorder or reuse a value (the partial unique index of the pending invitations filters on
/// <see cref="Pending"/>).
/// </summary>
public enum OrgInvitationStatus
{
    /// <summary>Sent and not answered yet: holds a seat of the plan until it is accepted, revoked or expires.</summary>
    Pending = 1,

    /// <summary>Accepted by the person with the invited email: it became a member (<see cref="OrgMember"/>).</summary>
    Accepted = 2,

    /// <summary>Withdrawn by the org (or replaced by its owner): the link no longer works and the seat is free again.</summary>
    Revoked = 3,

    /// <summary>Not accepted before <see cref="OrgInvitation.ExpiresAt"/>: the link no longer works and the seat is free again.</summary>
    Expired = 4,
}
