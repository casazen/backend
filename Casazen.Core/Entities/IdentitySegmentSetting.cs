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
    /// <summary>Name of the unique index on (org, format, channel) of the rows that hold for every property of the org (<see cref="PropertyId"/> null).</summary>
    public const string OrgWideIndexName = "UIX_IdentitySegmentSettings_OrgId_FormatId_Channel";

    /// <summary>Name of the unique index on (org, property, format, channel) of the rows of one property.</summary>
    public const string PropertyIndexName = "UIX_IdentitySegmentSettings_OrgId_PropertyId_FormatId_Channel";

    /// <summary>Longest id of an account (<see cref="QualifiedByUserId"/>, <see cref="UpdatedByUserId"/>).</summary>
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row (TN-2).</summary>
    public Guid OrgId { get; set; }

    /// <summary>The property (FK, CASCADE); null = every property of the org.</summary>
    public Guid? PropertyId { get; set; }

    /// <summary>The id of the format of the registry (the Italian electronic identity card first).</summary>
    [Required, MaxLength(40)]
    public string FormatId { get; set; } = string.Empty;

    /// <summary>The channel, a code of <c>IdentityChannels</c>.</summary>
    [Required, MaxLength(40)]
    public string Channel { get; set; } = string.Empty;

    /// <summary>What the policy may do in the segment; <see cref="IdentitySegmentMode.Observation"/> by default.</summary>
    public IdentitySegmentMode Mode { get; set; } = IdentitySegmentMode.Observation;

    /// <summary>The level of document assurance the segment requires, a code of <see cref="IdentityAssuranceLevels"/>; <c>consistency</c> by default.</summary>
    [Required, MaxLength(20)]
    public string RequiredAssurance { get; set; } = IdentityAssuranceLevels.Consistency;

    /// <summary>The version of the policy file the segment was qualified for.</summary>
    [MaxLength(50)]
    public string? QualifiedPolicyVersion { get; set; }

    /// <summary>The canonical SHA-256 (hex) of that policy file.</summary>
    [MaxLength(64)]
    public string? QualifiedPolicyHash { get; set; }

    /// <summary>The SHA-256 (hex) of the set of the model weights the segment was qualified with.</summary>
    [MaxLength(64)]
    public string? QualifiedWeightsHash { get; set; }

    /// <summary>The SHA-256 (hex) of the report of the qualification.</summary>
    [MaxLength(64)]
    public string? QualificationReportSha256 { get; set; }

    /// <summary>When the segment was qualified (UTC).</summary>
    public DateTime? QualifiedAt { get; set; }

    /// <summary>Who recorded the qualification: the opaque id of the account.</summary>
    [MaxLength(UserIdMaxLength)]
    public string? QualifiedByUserId { get; set; }

    /// <summary>Why the segment is suspended: a short reason of the staff, never personal data.</summary>
    [MaxLength(200)]
    public string? SuspendedReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Who changed the row last: the opaque id of the account.</summary>
    [MaxLength(UserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }
}
