using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// Withholding tax log entry for an owner payment (ADR-018).
/// Agency acting as sostituto d'imposta withholds 21% tax on owner payments for short-term rentals.
/// One log entry per booking per owner. Tracks both cash year (when collected) and stay year (when occurred).
/// Regulatory references: D.L. 50/2017 art. 4 (current), D.Lgs. 117/2026 artt. 207-208 (expected 1/1/2027).
/// </summary>
[Table("WithholdingLogs")]
public class WithholdingLog : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key. Inherited from the org; never client-supplied.</summary>
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    /// <summary>The property owner subject to withholding.</summary>
    public Guid OwnerId { get; set; }
    public virtual PropertyOwner Owner { get; set; } = null!;

    /// <summary>The property for which withholding is calculated.</summary>
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    /// <summary>The booking this withholding applies to.</summary>
    public Guid BookingId { get; set; }
    public virtual Booking Booking { get; set; } = null!;

    /// <summary>Statement month (1-12) for reporting purposes.</summary>
    public int Month { get; set; }

    /// <summary>Statement year (e.g., 2026) for reporting purposes.</summary>
    public int Year { get; set; }

    /// <summary>Year of collection (cash year, ADR-018). When payment was received.</summary>
    public int CashYear { get; set; }

    /// <summary>Year of stay (stay year, ADR-018). When the stay occurred.</summary>
    public int StayYear { get; set; }

    /// <summary>
    /// Gross base for withholding calculation (ADR-018-R4).
    /// Includes gross revenue + flat-rate cleaning + flat-rate linen.
    /// Commission added or not per OwnerMandate.CommissionTreatment.
    /// Excludes deposits, down payments, and penalties (ADR-018-R6).
    /// </summary>
    [Precision(18, 2)]
    public decimal GrossBase { get; set; }

    /// <summary>
    /// Withholding amount (21% of GrossBase for Private regime, ADR-018-R5).
    /// Zero for VatHolder owners (ADR-018-R5).
    /// </summary>
    [Precision(18, 2)]
    public decimal WithholdingAmount { get; set; }

    /// <summary>When this log entry was created (UTC).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
