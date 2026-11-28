using Casazen.Core.Entities.Enums;
using Casazen.Core.Identity;

namespace Casazen.Core.Services;

/// <summary>
/// The segments of the self-hosted identity verification (E70/F02/US01/T02, NF-194, NF-191, ADR-013-R19, decision D4): a segment is a property (or every property of
/// the org) × a format of the registry × a channel (<see cref="IdentityChannels"/>), kept in <c>IdentitySegmentSettings</c>. <b>Observation is the behaviour
/// everywhere</b>: the decision of the policy is the shadow decision and a person decides. Automatic approval is allowed in a segment only when it is in
/// <see cref="IdentitySegmentMode.Automatic"/>, qualified for the policy version, the policy hash and the weights hash in force, the policy is calibrated, the format gives
/// the assurance the segment requires on its channel, and the platform permission (<c>Features:CheckInIdentityAutoApproval</c>, with the self-hosted flow on) is on.
/// A change of the policy or of a weight takes the segment back to observation by construction (no job, no write); a suspension takes it back by configuration.
/// </summary>
/// <remarks>
/// <para><b>One org, always named.</b> Every method works on the org it is given, with the org in the query: a segment of another org is not found (null, a uniform 404
/// at the endpoints). The callers decide which org that is: the org of the caller's context for its administrators, the org of the route for the platform staff.
/// The services never check a role.</para>
/// <para><b>Audit.</b> Every real change writes one <c>OrgSettingsChanged</c> line in the activity log of the org (the fact, never a value), like the biometric
/// settings; a change that changes nothing writes none.</para>
/// <para><b>Fail closed.</b> <see cref="ResolveAsync"/> answers <c>AutoApprovalAllowed</c> true only when every condition holds; an exception of the database is never
/// an approval: the caller hands the guest to a person.</para>
/// </remarks>
public interface IIdentitySegmentService
{
    Task<IdentitySegmentOverview> ListAsync(Guid orgId, CancellationToken cancellationToken = default);

    Task<IdentitySegmentView> CreateAsync(Guid orgId, Guid? propertyId, string? formatId, string? channel, string? actorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the mode of a segment of the org: <see cref="IdentitySegmentMode.Automatic"/> only when it is qualified for the policy and the weights in force (422
    /// <c>identity_segment_not_qualified</c> otherwise); <see cref="IdentitySegmentMode.Suspended"/> always, with a reason of 1 to
    /// <see cref="IdentitySegmentRules.SuspendedReasonMaxLength"/> characters (422 <c>identity_segment_invalid</c> otherwise), stored as given (trimmed);
    /// <see cref="IdentitySegmentMode.Observation"/> always. Null when the segment is not of the org.
    /// </summary>
    Task<IdentitySegmentView?> SetModeAsync(Guid orgId, Guid segmentId, IdentitySegmentMode mode, string? reason, string? actorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the qualification of a segment of the org (the platform staff, after the offline qualification of D12): the policy version, the policy hash and the weights
    /// hash must be the ones in force, the policy calibrated (not provisional), the report hash a SHA-256 and <paramref name="requiredAssurance"/> a code of
    /// <see cref="IdentityAssuranceLevels"/>, the level the segment then requires; 422 <c>identity_segment_invalid</c> otherwise. The mode does not change. Null when the
    /// segment is not of the org.
    /// </summary>
    Task<IdentitySegmentView?> QualifyAsync(
        Guid orgId,
        Guid segmentId,
        string? policyVersion,
        string? policyHash,
        string? weightsHash,
        string? reportSha256,
        string? requiredAssurance,
        string adminUserId,
        CancellationToken cancellationToken = default);

    Task<IdentitySegmentView?> RevokeQualificationAsync(Guid orgId, Guid segmentId, string adminUserId, CancellationToken cancellationToken = default);

    Task<SegmentResolution> ResolveAsync(Guid orgId, Guid propertyId, string? formatId, string? channel, CancellationToken cancellationToken = default);
}

public sealed record SegmentResolution(
    Guid? SegmentId,
    IdentitySegmentMode Mode,
    bool Qualified,
    bool AutoApprovalAllowed,
    string RequiredAssurance,
    string? ReasonCode);

public sealed record IdentitySegmentView(
    Guid Id,
    Guid? PropertyId,
    string? PropertyName,
    string FormatId,
    string Channel,
    IdentitySegmentMode Mode,
    bool Qualified,
    string? QualificationReason,
    string? QualifiedPolicyVersion,
    DateTime? QualifiedAt,
    bool AutoApprovalAllowed,
    string? AutoApprovalReason,
    string RequiredAssurance,
    string? SuspendedReason);

public sealed record IdentitySegmentOverview(
    IReadOnlyList<IdentitySegmentView> Segments,
    IReadOnlyList<IdentitySegmentFormat> Formats,
    IReadOnlyList<string> Channels,
    string? ActivePolicyVersion,
    bool ActivePolicyProvisional,
    bool AutoApprovalEnabled);

public sealed record IdentitySegmentFormat(string Id, string Label);

public static class IdentitySegmentRules
{
    public const int SuspendedReasonMaxLength = 200;
}

public static class IdentitySegmentReasonCodes
{
    public const string AutoApprovalDisabled = "auto_approval_disabled";

    public const string SegmentObservation = "segment_observation";

    public const string SegmentSuspended = "segment_suspended";

    public const string SegmentNotQualified = "segment_not_qualified";

    public const string QualificationMismatch = "qualification_mismatch";

    public const string PolicyProvisional = "policy_provisional";

    public const string PolicyUnavailable = "policy_unavailable";

    public const string AssuranceRequiresChip = "assurance_requires_chip";

    public static IReadOnlyList<string> All { get; } =
    [
        AutoApprovalDisabled, SegmentObservation, SegmentSuspended, SegmentNotQualified, QualificationMismatch, PolicyProvisional, PolicyUnavailable,
        AssuranceRequiresChip,
    ];
}

public static class IdentitySegmentModeCodes
{
    public const string Observation = "observation";
    public const string Automatic = "automatic";
    public const string Suspended = "suspended";

    public static IReadOnlyList<string> All { get; } = [Observation, Automatic, Suspended];

    public static string CodeOf(IdentitySegmentMode mode)
    {
        return mode switch
        {
            IdentitySegmentMode.Automatic => Automatic,
            IdentitySegmentMode.Suspended => Suspended,
            _ => Observation,
        };
    }

    public static bool TryParse(string? value, out IdentitySegmentMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case Automatic:
                mode = IdentitySegmentMode.Automatic;
                return true;
            case Suspended:
                mode = IdentitySegmentMode.Suspended;
                return true;
            case Observation:
                mode = IdentitySegmentMode.Observation;
                return true;
            default:
                mode = IdentitySegmentMode.Observation;
                return false;
        }
    }
}
