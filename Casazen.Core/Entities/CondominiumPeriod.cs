using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One fiscal year of a condominium (CondoLedger <c>fiscal_periods</c>). It need not match the calendar year.
/// Two active years of the same condominium do not overlap.
/// </summary>
[Table("CondominiumPeriods")]
public class CondominiumPeriod : ITenantOwned
{
    public const int LabelMaxLength = 20;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid CondominiumId { get; set; }

    public DateOnly StartDate { get; set; }

    public DateOnly EndDate { get; set; }

    [Required]
    [MaxLength(LabelMaxLength)]
    public string Label { get; set; } = string.Empty;

    public CondominiumPeriodStatus Status { get; set; } = CondominiumPeriodStatus.Draft;

    /// <summary>Bumps when a statement revision is confirmed. Issued installments are not rebuilt because of it.</summary>
    public int Version { get; set; } = 1;

    public DateTime? ClosedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Condominium Condominium { get; set; } = null!;

    public Org Org { get; set; } = null!;

    public ICollection<CondominiumStatement> Statements { get; set; } = [];

    public ICollection<CondominiumInstallment> Installments { get; set; } = [];

    public CondominiumPriorBalance? PriorBalance { get; set; }
}
