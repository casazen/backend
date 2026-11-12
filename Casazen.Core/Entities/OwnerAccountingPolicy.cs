using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// A versioned accounting and tax rule set of the agency (NF-109, NF-110, ADR-019, E06/F05/US02/T01): the jurisdiction, the rule code and version, when it
/// is valid, the official source it was taken from (address and SHA-256), its parameters (rates, applicability, tax codes, rounding, retention: data,
/// never literals in the code) and who approved it. Stored apart from the owners: an owner's fiscal profile points at the rule set it follows.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Approved before use.</b> A rule set without <see cref="ApprovedAt"/> is never used for a statement, a payout or a tax decision: the qualified
/// accountant approves it first (the approver's name is kept with the user who recorded it).</item>
/// <item><b>Immutable once approved.</b> A change is a new <see cref="Version"/>; the documents keep the exact version they used.</item>
/// <item><b>Tenant.</b> <see cref="ITenantOwned"/>: the rule sets of one agency.</item>
/// </list>
/// </remarks>
[Table("OwnerAccountingPolicies")]
public class OwnerAccountingPolicy : ITenantOwned
{
    /// <summary>Unique (<see cref="OrgId"/>, <see cref="RuleCode"/>, <see cref="Version"/>).</summary>
    public const string RuleVersionIndexName = "UIX_OwnerAccountingPolicies_OrgId_RuleCode_Version";

    public const int JurisdictionMaxLength = 8;
    public const int RuleCodeMaxLength = 64;
    public const int VersionMaxLength = 32;
    public const int SourceUrlMaxLength = 500;
    public const int SourceSha256Length = 64;
    public const int ParametersMaxLength = 8_000;
    public const int UserIdMaxLength = 255;
    public const int ApproverNameMaxLength = 200;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    /// <summary>Where the rule applies: an ISO 3166-1 country (<c>IT</c>, <c>CH</c>), or a country and a region (<c>CH-TI</c>).</summary>
    [Required, MaxLength(JurisdictionMaxLength)]
    public string Jurisdiction { get; set; } = string.Empty;

    /// <summary>The code of the rule set (<c>IT-STR-WITHHOLDING</c>...).</summary>
    [Required, MaxLength(RuleCodeMaxLength)]
    public string RuleCode { get; set; } = string.Empty;

    [Required, MaxLength(VersionMaxLength)]
    public string Version { get; set; } = string.Empty;

    /// <summary>The first day the rule applies (Europe/Rome calendar day).</summary>
    public DateOnly ValidFrom { get; set; }

    /// <summary>The last day the rule applies; <c>null</c> while it is in force.</summary>
    public DateOnly? ValidTo { get; set; }

    /// <summary>The official source of the rule (a law, a circular, the guidance of the tax office).</summary>
    [Required, MaxLength(SourceUrlMaxLength)]
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>The SHA-256 of the source document, 64 hexadecimal characters.</summary>
    [Required, MaxLength(SourceSha256Length)]
    public string SourceSha256 { get; set; } = string.Empty;

    /// <summary>The parameters of the rule as JSON (<c>OwnerAccountingRuleParameters</c>): the rates and the applicability are data.</summary>
    [Required, MaxLength(ParametersMaxLength)]
    public string ParametersJson { get; set; } = "{}";

    /// <summary>When the qualified accountant's approval was recorded (UTC); <c>null</c>: not usable.</summary>
    public DateTime? ApprovedAt { get; set; }

    /// <summary>Who recorded the approval.</summary>
    [MaxLength(UserIdMaxLength)]
    public string? ApprovedByUserId { get; set; }

    /// <summary>The qualified accountant who approved the rule (name and registration, as they gave it).</summary>
    [MaxLength(ApproverNameMaxLength)]
    public string? ApprovedByName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(UserIdMaxLength)]
    public string? CreatedByUserId { get; set; }
}
