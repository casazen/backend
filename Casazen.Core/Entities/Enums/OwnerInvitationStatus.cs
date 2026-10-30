namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Status of an owner invitation (ADR-011-R4). Persisted as int, explicit values:
/// append-only, never reorder or reuse values.
/// </summary>
public enum OwnerInvitationStatus
{
    /// <summary>Sent and not answered yet.</summary>
    Pending = 1,

    /// <summary>Accepted by the owner: OwnerUser linked to PropertyOwner.</summary>
    Accepted = 2,

    /// <summary>Withdrawn by the agency: the link no longer works.</summary>
    Revoked = 3,

    /// <summary>Not accepted before expiry: the link no longer works.</summary>
    Expired = 4,
}
