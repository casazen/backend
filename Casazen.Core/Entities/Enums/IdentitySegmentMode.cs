namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What a segment of the self-hosted identity verification (property × format × channel, <c>IdentitySegmentSetting</c>) lets the policy do
/// (E70/F01/US01/T01, NF-194, ADR-013-R19). Stored as an integer: never renumber; a new value goes at the end with its number.
/// </summary>
public enum IdentitySegmentMode
{
    /// <summary>The decision of the policy is recorded as the shadow decision and a person decides: the mode of every segment until it is qualified.</summary>
    Observation = 0,

    /// <summary>
    /// The segment is qualified for a policy version, a policy hash and a weights hash: with <c>Features:CheckInIdentityAutoApproval</c> on and the same
    /// version and hashes in force, the policy approves automatically; any other version or hash is observation again.
    /// </summary>
    Automatic = 1,

    /// <summary>The segment was taken back to review by configuration (an anomaly, a regression): a person decides, and the reason is recorded.</summary>
    Suspended = 2,
}
