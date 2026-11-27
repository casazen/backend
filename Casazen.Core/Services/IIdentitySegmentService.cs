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
    /// <summary>The segments of the org with what each allows now, and what the page offers to create one: the formats of the registry, the channels, the policy in force.</summary>
    Task<IdentitySegmentOverview> ListAsync(Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the segment of <paramref name="formatId"/> (a format of the registry) and <paramref name="channel"/> (a code of <see cref="IdentityChannels"/>) for the
    /// property <paramref name="propertyId"/> of the org, or for every property of the org when null, in <see cref="IdentitySegmentMode.Observation"/>. A segment that
    /// exists already is answered as it is (nothing changes). 422 <c>identity_segment_invalid</c> (a <c>DomainRuleException</c>) for a format, a channel or a property that
    /// is not of the org.
    /// </summary>
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

    /// <summary>Removes the qualification of a segment of the org; an automatic segment returns to observation. Null when the segment is not of the org.</summary>
    Task<IdentitySegmentView?> RevokeQualificationAsync(Guid orgId, Guid segmentId, string adminUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// What the policy may do for a verification of the property <paramref name="propertyId"/>, the format <paramref name="formatId"/> and the channel
    /// <paramref name="channel"/>: the segment of the property first, then the one of every property of the org, none = observation. See <see cref="SegmentResolution"/>.
    /// </summary>
    Task<SegmentResolution> ResolveAsync(Guid orgId, Guid propertyId, string? formatId, string? channel, CancellationToken cancellationToken = default);
}

/// <summary>
/// What a segment allows for one verification (<see cref="IIdentitySegmentService.ResolveAsync"/>): the segment that applies (null: none, observation), its mode, whether it
/// is qualified for the policy and the weights in force, whether the policy may approve automatically and the level of assurance it requires. <paramref name="ReasonCode"/>
/// is the first condition that fails (a code of <see cref="IdentitySegmentReasonCodes"/>), null only when <paramref name="AutoApprovalAllowed"/> is true.
/// </summary>
public sealed record SegmentResolution(
    Guid? SegmentId,
    IdentitySegmentMode Mode,
    bool Qualified,
    bool AutoApprovalAllowed,
    string RequiredAssurance,
    string? ReasonCode);

/// <summary>
/// One segment as the administrators see it: its key, its mode, whether it is qualified for what is in force (and why not: <paramref name="QualificationReason"/>, a code of
/// <see cref="IdentitySegmentReasonCodes"/>), the policy version it was qualified for and when, whether automatic approval is allowed now (and the first condition that
/// fails: <paramref name="AutoApprovalReason"/>), the assurance it requires and the reason of a suspension. Ids, codes, versions and times only.
/// </summary>
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

/// <summary>
/// The segments of an org and what the page needs around them: the formats of the registry (id and label), the channels, the policy in force (its version, null while
/// none is usable, and whether it is provisional) and whether the platform permission of the automatic approval is on.
/// </summary>
public sealed record IdentitySegmentOverview(
    IReadOnlyList<IdentitySegmentView> Segments,
    IReadOnlyList<IdentitySegmentFormat> Formats,
    IReadOnlyList<string> Channels,
    string? ActivePolicyVersion,
    bool ActivePolicyProvisional,
    bool AutoApprovalEnabled);

/// <summary>A format of the registry as the page lists it.</summary>
public sealed record IdentitySegmentFormat(string Id, string Label);

/// <summary>The limits of a segment.</summary>
public static class IdentitySegmentRules
{
    /// <summary>The longest reason of a suspension (<c>IdentitySegmentSetting.SuspendedReason</c>).</summary>
    public const int SuspendedReasonMaxLength = 200;
}

/// <summary>
/// Why a segment does not allow the automatic approval (<see cref="SegmentResolution.ReasonCode"/>), in the order the conditions are checked: the first that fails is the
/// one answered. A code is never renamed or reused; a new one goes at the end of the class and of <see cref="All"/>.
/// </summary>
public static class IdentitySegmentReasonCodes
{
    /// <summary>The platform permission is off (<c>Features:CheckInIdentityAutoApproval</c>, or the self-hosted flow itself).</summary>
    public const string AutoApprovalDisabled = "auto_approval_disabled";

    /// <summary>The segment is in observation, or there is no segment: a person decides.</summary>
    public const string SegmentObservation = "segment_observation";

    /// <summary>The segment was suspended by configuration.</summary>
    public const string SegmentSuspended = "segment_suspended";

    /// <summary>The segment was never qualified, or its qualification was revoked.</summary>
    public const string SegmentNotQualified = "segment_not_qualified";

    /// <summary>The segment was qualified for another policy version, policy hash or weights hash than the ones in force (or the models are not loaded).</summary>
    public const string QualificationMismatch = "qualification_mismatch";

    /// <summary>The policy in force is provisional: nothing is approved automatically until a calibrated policy file is committed.</summary>
    public const string PolicyProvisional = "policy_provisional";

    /// <summary>No policy can be used (missing, invalid or not pinned).</summary>
    public const string PolicyUnavailable = "policy_unavailable";

    /// <summary>
    /// The format does not give the assurance the segment requires on its channel: a photo in a browser gives <c>consistency</c> only, so a segment that requires
    /// <c>cryptographic</c> (the chip) never approves automatically; a format that is no longer in the registry, or does not admit the channel, gives none.
    /// </summary>
    public const string AssuranceRequiresChip = "assurance_requires_chip";

    /// <summary>Every code, in the order of the checks.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        AutoApprovalDisabled, SegmentObservation, SegmentSuspended, SegmentNotQualified, QualificationMismatch, PolicyProvisional, PolicyUnavailable,
        AssuranceRequiresChip,
    ];
}

/// <summary>The codes of <see cref="IdentitySegmentMode"/> the endpoints speak (<c>observation</c>, <c>automatic</c>, <c>suspended</c>).</summary>
public static class IdentitySegmentModeCodes
{
    public const string Observation = "observation";
    public const string Automatic = "automatic";
    public const string Suspended = "suspended";

    /// <summary>Every code, in the order of the enum.</summary>
    public static IReadOnlyList<string> All { get; } = [Observation, Automatic, Suspended];

    /// <summary>The code of a mode; an unknown value of the enum reads as <c>observation</c>, the prudent one.</summary>
    public static string CodeOf(IdentitySegmentMode mode)
    {
        return mode switch
        {
            IdentitySegmentMode.Automatic => Automatic,
            IdentitySegmentMode.Suspended => Suspended,
            _ => Observation,
        };
    }

    /// <summary>The mode a client named, without regard to case or surrounding spaces; false (and observation) for anything else, nothing included.</summary>
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
