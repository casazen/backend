namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What one piece of the evidence kept for a reviewer is (<c>IdentityReviewEvidence</c>, E70/F01/US01/T01, NF-195, ADR-013-R20). Stored as an integer:
/// never renumber; a new value goes at the end with its number. Never an embedding: only pictures, encrypted in the private storage.
/// </summary>
public enum IdentityEvidenceKind
{
    /// <summary>The crop of the portrait of the identity document.</summary>
    DocumentPortrait = 0,

    /// <summary>One frame of the presence sequence, chosen by the server.</summary>
    Frame = 1,
}
