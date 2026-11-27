using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// F24 tax form record for monthly withholding tax payment (ADR-018).
/// Aggregates withholding amounts for all owners in a given month.
/// Tax code 1919, payment deadline 16th of following month.
/// Regulatory references: D.L. 50/2017 art. 4 (current), D.Lgs. 117/2026 artt. 207-208 (expected 1/1/2027).
/// </summary>
[Table("F24Records")]
public class F24Record : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key. Inherited from the org; never client-supplied.</summary>
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    /// <summary>Reference month (1-12) for the withholding period.</summary>
    public int ReferenceMonth { get; set; }

    /// <summary>Reference year (e.g., 2026) for the withholding period.</summary>
    public int ReferenceYear { get; set; }

    /// <summary>
    /// Total amount to pay (sum of all owners' withholding for this period).
    /// Aggregated from WithholdingLog entries for this month/year.
    /// </summary>
    [Precision(18, 2)]
    public decimal TotalAmount { get; set; }

    /// <summary>
    /// The tax code of the F24, as the approved rule of the withholding evidence gives it (E06/F05/US02/T03): never written in the code.
    /// </summary>
    [Required]
    [MaxLength(10)]
    public string TaxCode { get; set; } = string.Empty;

    /// <summary>
    /// Payment deadline (16th of the month following ReferenceMonth, ADR-018-R8).
    /// Computed as 16th of (ReferenceMonth + 1).
    /// </summary>
    public DateTime DueDate { get; set; }

    /// <summary>
    /// When the F24 was paid (UTC). Null if not yet reconciled.
    /// Set via payment reconciliation endpoint after manual payment.
    /// </summary>
    public DateTime? PaidAt { get; set; }

    /// <summary>When this F24 record was created (UTC).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Where the F24 is (E06/F05/US02/T03): generated is never filed; submitted only through a controlled manual handoff.</summary>
    public Enums.TaxFilingState FilingState { get; set; } = Enums.TaxFilingState.Generated;

    /// <summary>The rule versions of the evidence it was built from (<c>{ruleCode}@{version}</c>, comma separated).</summary>
    [MaxLength(200)]
    public string? RuleVersion { get; set; }

    /// <summary>The reference of the controlled manual handoff.</summary>
    [MaxLength(200)]
    public string? HandoffReference { get; set; }

    /// <summary>The receipt once accepted.</summary>
    [MaxLength(200)]
    public string? ReceiptReference { get; set; }

    [MaxLength(200)]
    public string? RejectionReason { get; set; }

    public DateTime? FilingUpdatedAt { get; set; }

    [MaxLength(255)]
    public string? FilingUpdatedByUserId { get; set; }
}
