using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

/// <summary>
/// A reason why a guest does not pay the tourist tax (NF-46, ADR-037-R1): platform reference data, the same for every org, not
/// tenant-owned. <see cref="MinorAgeCode"/> is the only automatic one: the calculator applies it from the age and the host never
/// chooses it; every other reason is chosen by the host for one guest of one stay.
/// </summary>
/// <remarks>
/// <para>The reasons are the categories the regulation context lists as typically exempt (<c>.claude/context/regulations/imposta_soggiorno.md</c>,
/// "Esenzioni Comuni"); whether a comune grants them is the comune's rule, which the host knows and declares.</para>
/// <para><see cref="OctorateExemptionCode"/> is null until an administrator sets it: the 30 reasons of Octorate (<c>cityTaxExemption</c>)
/// are not in the repository, so no mapping is invented (Q-004).</para>
/// </remarks>
[Table("TouristTaxExemptionReasons")]
public class TouristTaxExemptionReason
{
    /// <summary>Length of <see cref="Code"/>, and of every column that stores a reason code.</summary>
    public const int CodeMaxLength = 40;

    /// <summary>Length of <see cref="LabelKey"/>.</summary>
    public const int LabelKeyMaxLength = 100;

    /// <summary>Length of <see cref="OctorateExemptionCode"/>.</summary>
    public const int OctorateCodeMaxLength = 50;

    /// <summary>Length of <see cref="UpdatedByUserId"/>: the one of <c>User.Id</c>.</summary>
    public const int UserIdMaxLength = 255;

    /// <summary>The automatic exemption of the guests younger than the minimum age of the rate.</summary>
    public const string MinorAgeCode = "minor_age";

    /// <summary>The guest lives in the comune.</summary>
    public const string ResidentCode = "resident";

    /// <summary>A stay for health or care reasons.</summary>
    public const string HealthCareCode = "health_care";

    /// <summary>The companion of a disabled person.</summary>
    public const string DisabilityCompanionCode = "disability_companion";

    /// <summary>Law enforcement or another public service on duty.</summary>
    public const string PublicServiceCode = "public_service";

    /// <summary>A member of the family of the manager.</summary>
    public const string HostFamilyCode = "host_family";

    /// <summary>Another exemption of the comune's regulation.</summary>
    public const string OtherCode = "other";

    [Key]
    public Guid Id { get; set; }

    /// <summary>Stable code (snake case), unique: what a stay guest stores and what the clients translate.</summary>
    [Required, MaxLength(CodeMaxLength)]
    public string Code { get; set; } = string.Empty;

    /// <summary>The i18n key of the plain-words label the clients show (<c>touristTax.exemption.&lt;code&gt;</c>).</summary>
    [Required, MaxLength(LabelKeyMaxLength)]
    public string LabelKey { get; set; } = string.Empty;

    /// <summary>True only for <see cref="MinorAgeCode"/>: applied by the calculator, never chosen by the host.</summary>
    public bool IsAutomatic { get; set; }

    /// <summary>The Octorate <c>cityTaxExemption</c> reason this one matches; null = no known match (set only by an administrator).</summary>
    [MaxLength(OctorateCodeMaxLength)]
    public string? OctorateExemptionCode { get; set; }

    /// <summary>Order of the reasons in the lists.</summary>
    public int SortOrder { get; set; }

    /// <summary>False: the reason can no longer be chosen (the rows that already carry it keep it).</summary>
    public bool IsActive { get; set; } = true;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The administrator who last changed the row; null for the seeded rows.</summary>
    [MaxLength(UserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }
}
