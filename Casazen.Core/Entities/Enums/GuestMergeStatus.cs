namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where a proposed merge of two CRM profiles stands (NF-88, <see cref="GuestMergeCandidate"/>). Stored as an integer in
/// <c>GuestMergeCandidates.Status</c>, <b>append only</b>: never renumber a member.
/// </summary>
public enum GuestMergeStatus
{
    /// <summary>Found by the matching and waiting for a person to decide.</summary>
    Proposed = 1,

    /// <summary>A person merged the two profiles.</summary>
    Merged = 2,

    /// <summary>A person decided the two profiles are not the same guest: the pair is not proposed again.</summary>
    Dismissed = 3,
}
