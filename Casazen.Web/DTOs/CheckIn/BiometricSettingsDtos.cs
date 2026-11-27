using Casazen.Core.Services;

namespace Casazen.Web.DTOs.CheckIn;

/// <summary>
/// The biometric settings of an organization as the page shows them (E22/F01/US01/T03, <c>GET/PUT api/settings/biometrics</c>): whether the face check is
/// on, the two thresholds of the similarity, the attempts, and whether the thresholds are still <c>provisional</c> (never calibrated on real data, or changed
/// since) with the time a platform administrator <c>calibratedAt</c> says they were. <c>isDefault</c> is true while the organization has no row of its own:
/// today's defaults, enabled only on the Agency plan.
/// </summary>
public sealed class BiometricSettingsDto
{
    public bool Enabled { get; set; }

    /// <summary>From this similarity the face is accepted without a person.</summary>
    public decimal AcceptThreshold { get; set; }

    /// <summary>Between this and the accept threshold an operator decides; at or under it the attempt fails.</summary>
    public decimal EscalateThreshold { get; set; }

    public int MaxAttempts { get; set; }

    public bool Provisional { get; set; }

    public DateTime? CalibratedAt { get; set; }

    public bool IsDefault { get; set; }

    public static BiometricSettingsDto From(BiometricSettingsView view) => new()
    {
        Enabled = view.Enabled,
        AcceptThreshold = view.AcceptThreshold,
        EscalateThreshold = view.EscalateThreshold,
        MaxAttempts = view.MaxAttempts,
        Provisional = view.Provisional,
        CalibratedAt = view.CalibratedAt,
        IsDefault = view.IsDefault,
    };
}

/// <summary>
/// What an organization administrator may send (<c>PUT api/settings/biometrics</c>): a member left out keeps its value. Bounds: 0.50 &lt;= escalate &lt; accept
/// &lt;= 0.95, attempts 1 to 5, or 422 <c>biometric_thresholds_out_of_range</c>, <c>biometric_thresholds_order_invalid</c>, <c>biometric_attempts_out_of_range</c>
/// and nothing is saved. There is no calibration member: a change always leaves the thresholds provisional, and only a platform administrator marks a
/// calibration (<c>PUT api/admin/biometrics/{orgId}/calibrated</c>).
/// </summary>
public sealed class UpdateBiometricSettingsDto
{
    public bool? Enabled { get; set; }

    public decimal? AcceptThreshold { get; set; }

    public decimal? EscalateThreshold { get; set; }

    public int? MaxAttempts { get; set; }
}

/// <summary>
/// The segments of the self-hosted identity verification of the caller's organization (E70/F02/US01/T02, NF-194, <c>GET api/settings/biometrics/segments</c>): every segment
/// with what it allows now, the formats of the registry and the channels a new segment can name, the policy in force (<c>version</c> null while none is usable) and whether
/// the platform permission of the automatic approval is on (<c>autoApprovalEnabled</c>).
/// </summary>
public sealed class IdentitySegmentsDto
{
    public IReadOnlyList<IdentitySegmentDto> Segments { get; set; } = [];

    public IReadOnlyList<IdentitySegmentFormatDto> Formats { get; set; } = [];

    public IReadOnlyList<string> Channels { get; set; } = [];

    public IdentityActivePolicyDto ActivePolicy { get; set; } = new();

    public bool AutoApprovalEnabled { get; set; }

    public static IdentitySegmentsDto From(IdentitySegmentOverview overview) => new()
    {
        Segments = overview.Segments.Select(IdentitySegmentDto.From).ToList(),
        Formats = overview.Formats.Select(format => new IdentitySegmentFormatDto { Id = format.Id, Label = format.Label }).ToList(),
        Channels = overview.Channels,
        ActivePolicy = new IdentityActivePolicyDto { Version = overview.ActivePolicyVersion, Provisional = overview.ActivePolicyProvisional },
        AutoApprovalEnabled = overview.AutoApprovalEnabled,
    };
}

/// <summary>
/// One segment (property × format × channel): <c>propertyId</c> null for every property of the organization; <c>mode</c> <c>observation</c>, <c>automatic</c> or
/// <c>suspended</c>; <c>qualified</c> for the policy and the weights in force, or <c>qualificationReason</c> says why not (<c>segment_not_qualified</c>,
/// <c>qualification_mismatch</c>, <c>policy_unavailable</c>); <c>autoApprovalAllowed</c> and, when false, <c>autoApprovalReason</c>, the first condition that fails.
/// </summary>
public sealed class IdentitySegmentDto
{
    public Guid Id { get; set; }

    public Guid? PropertyId { get; set; }

    public string? PropertyName { get; set; }

    public string FormatId { get; set; } = string.Empty;

    public string Channel { get; set; } = string.Empty;

    public string Mode { get; set; } = IdentitySegmentModeCodes.Observation;

    public bool Qualified { get; set; }

    public string? QualificationReason { get; set; }

    public string? QualifiedPolicyVersion { get; set; }

    public DateTime? QualifiedAt { get; set; }

    public bool AutoApprovalAllowed { get; set; }

    public string? AutoApprovalReason { get; set; }

    public string RequiredAssurance { get; set; } = string.Empty;

    public string? SuspendedReason { get; set; }

    public static IdentitySegmentDto From(IdentitySegmentView view) => new()
    {
        Id = view.Id,
        PropertyId = view.PropertyId,
        PropertyName = view.PropertyName,
        FormatId = view.FormatId,
        Channel = view.Channel,
        Mode = IdentitySegmentModeCodes.CodeOf(view.Mode),
        Qualified = view.Qualified,
        QualificationReason = view.QualificationReason,
        QualifiedPolicyVersion = view.QualifiedPolicyVersion,
        QualifiedAt = view.QualifiedAt,
        AutoApprovalAllowed = view.AutoApprovalAllowed,
        AutoApprovalReason = view.AutoApprovalReason,
        RequiredAssurance = view.RequiredAssurance,
        SuspendedReason = view.SuspendedReason,
    };
}

/// <summary>A format of the registry a segment can name: its id and its label for the staff.</summary>
public sealed class IdentitySegmentFormatDto
{
    public string Id { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;
}

/// <summary>The decision policy in force: its version (null while none is usable) and whether it is provisional (no automatic approval is possible then).</summary>
public sealed class IdentityActivePolicyDto
{
    public string? Version { get; set; }

    public bool Provisional { get; set; } = true;
}

/// <summary>A new segment (<c>POST api/settings/biometrics/segments</c>): <c>propertyId</c> null for every property of the organization; it starts in observation.</summary>
public sealed class CreateIdentitySegmentDto
{
    public Guid? PropertyId { get; set; }

    public string? FormatId { get; set; }

    public string? Channel { get; set; }
}

/// <summary>
/// The mode of a segment (<c>PUT api/settings/biometrics/segments/{id}</c>): <c>observation</c>, <c>automatic</c> (only when qualified for the policy and the weights in force,
/// 422 <c>identity_segment_not_qualified</c> otherwise) or <c>suspended</c> with a <c>reason</c> of at most 200 characters (422 <c>identity_segment_invalid</c> otherwise).
/// There is no qualification member: only a platform administrator records one (<c>PUT api/admin/biometrics/{orgId}/segments/{segmentId}/qualification</c>).
/// </summary>
public sealed class UpdateIdentitySegmentDto
{
    public string? Mode { get; set; }

    public string? Reason { get; set; }
}
