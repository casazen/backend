using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Identity;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One segment of the self-hosted identity verification (E70/F01/US01/T02, NF-191, NF-194, ADR-013-R19): a format of the registry and a channel (the coarse
/// family of a browser, <c>IdentityChannels</c>) for one property of the org, or for every property of the org when <see cref="PropertyId"/> is null, and what
/// the policy may do there (<see cref="Mode"/>). A combination without a row is in observation: the decision of the policy is the shadow decision and a person
/// decides.
/// </summary>
/// <remarks>
/// <para><b>One row per key</b>, enforced by two filtered unique indexes: (org, format, channel) for the row of every property
/// (<see cref="OrgWideIndexName"/>) and (org, property, format, channel) for the row of one property (<see cref="PropertyIndexName"/>).</para>
/// <para><b>Qualified</b> only for the policy version, the policy hash and the weights hash it records, with the hash of the report of the qualification: any other
/// policy or weights in force is observation again, and a suspension (<see cref="IdentitySegmentMode.Suspended"/>, with <see cref="SuspendedReason"/>) takes the
/// segment back to a person by configuration.</para>
/// <para>No personal data: ids, codes, hashes and times; who changed the row is the opaque id of an account (the Auth0 subject), not a foreign key.</para>
/// </remarks>
[Table("IdentitySegmentSettings")]
public class IdentitySegmentSetting : ITenantOwned
{
    public const string OrgWideIndexName = "UIX_IdentitySegmentSettings_OrgId_FormatId_Channel";

    public const string PropertyIndexName = "UIX_IdentitySegmentSettings_OrgId_PropertyId_FormatId_Channel";

    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid? PropertyId { get; set; }

    [Required, MaxLength(40)]
    public string FormatId { get; set; } = string.Empty;

    [Required, MaxLength(40)]
    public string Channel { get; set; } = string.Empty;

    public IdentitySegmentMode Mode { get; set; } = IdentitySegmentMode.Observation;

    [Required, MaxLength(20)]
    public string RequiredAssurance { get; set; } = IdentityAssuranceLevels.Consistency;

    [MaxLength(50)]
    public string? QualifiedPolicyVersion { get; set; }

    [MaxLength(64)]
    public string? QualifiedPolicyHash { get; set; }

    [MaxLength(64)]
    public string? QualifiedWeightsHash { get; set; }

    [MaxLength(64)]
    public string? QualificationReportSha256 { get; set; }

    public DateTime? QualifiedAt { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? QualifiedByUserId { get; set; }

    /// <summary>Why the segment is suspended: a short reason of the staff, never personal data.</summary>
    [MaxLength(200)]
    public string? SuspendedReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(UserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }
}
