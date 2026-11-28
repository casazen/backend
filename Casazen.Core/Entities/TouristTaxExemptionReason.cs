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
    public const int CodeMaxLength = 40;

    public const int LabelKeyMaxLength = 100;

    public const int OctorateCodeMaxLength = 50;

    public const int UserIdMaxLength = 255;

    public const string MinorAgeCode = "minor_age";

    public const string ResidentCode = "resident";

    public const string HealthCareCode = "health_care";

    public const string DisabilityCompanionCode = "disability_companion";

    public const string PublicServiceCode = "public_service";

    public const string HostFamilyCode = "host_family";

    public const string OtherCode = "other";

    [Key]
    public Guid Id { get; set; }

    [Required, MaxLength(CodeMaxLength)]
    public string Code { get; set; } = string.Empty;

    [Required, MaxLength(LabelKeyMaxLength)]
    public string LabelKey { get; set; } = string.Empty;

    public bool IsAutomatic { get; set; }

    [MaxLength(OctorateCodeMaxLength)]
    public string? OctorateExemptionCode { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(UserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }
}
