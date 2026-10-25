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
    /// Tax code for withholding on short-term rental income (ADR-018-R8).
    /// Fixed value: "1919" per official AdE F24 form structure.
    /// </summary>
    [Required]
    [MaxLength(10)]
    public string TaxCode { get; set; } = "1919";

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
}
