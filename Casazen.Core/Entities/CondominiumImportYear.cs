using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The cents of one fiscal year in a staged snapshot. They are compared with the book only after the house is confirmed.
/// </summary>
[Table("CondominiumImportYears")]
public class CondominiumImportYear : ITenantOwned
{
    public const int SourceIdMaxLength = 80;
    public const int LabelMaxLength = 20;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid HouseId { get; set; }

    [Required]
    [MaxLength(SourceIdMaxLength)]
    public string SourceId { get; set; } = string.Empty;

    [Required]
    [MaxLength(LabelMaxLength)]
    public string Label { get; set; } = string.Empty;

    public DateOnly StartDate { get; set; }

    public DateOnly EndDate { get; set; }

    public long OrdinaryCents { get; set; }

    public long ExtraordinaryCents { get; set; }

    public long FinalCents { get; set; }

    public long PriorCents { get; set; }

    public long PaymentCents { get; set; }

    public long SettlementCents { get; set; }

    public int InstallmentCount { get; set; }

    public long InstallmentTotalCents { get; set; }

    public CondominiumInstallmentCadence? Cadence { get; set; }

    public string PaymentsJson { get; set; } = "[]";

    public CondominiumImportHouse House { get; set; } = null!;

    public Org Org { get; set; } = null!;
}
