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

    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    public int ReferenceMonth { get; set; }

    public int ReferenceYear { get; set; }

    [Precision(18, 2)]
    public decimal TotalAmount { get; set; }

    /// <summary>
    /// The tax code of the F24, as the approved rule of the withholding evidence gives it (E06/F05/US02/T03): never written in the code.
    /// </summary>
    [Required]
    [MaxLength(10)]
    public string TaxCode { get; set; } = string.Empty;

    public DateTime DueDate { get; set; }

    /// <summary>
    /// When the F24 was paid (UTC). Null if not yet reconciled.
    /// Set via payment reconciliation endpoint after manual payment.
    /// </summary>
    public DateTime? PaidAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Where the F24 is (E06/F05/US02/T03): generated is never filed; submitted only through a controlled manual handoff.</summary>
    public Enums.TaxFilingState FilingState { get; set; } = Enums.TaxFilingState.Generated;

    [MaxLength(200)]
    public string? RuleVersion { get; set; }

    [MaxLength(200)]
    public string? HandoffReference { get; set; }

    [MaxLength(200)]
    public string? ReceiptReference { get; set; }

    [MaxLength(200)]
    public string? RejectionReason { get; set; }

    public DateTime? FilingUpdatedAt { get; set; }

    [MaxLength(255)]
    public string? FilingUpdatedByUserId { get; set; }
}
