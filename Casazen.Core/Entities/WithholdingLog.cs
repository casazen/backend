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

    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    public Guid OwnerId { get; set; }
    public virtual PropertyOwner Owner { get; set; } = null!;

    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    public Guid BookingId { get; set; }
    public virtual Booking Booking { get; set; } = null!;

    public int Month { get; set; }

    public int Year { get; set; }

    public int CashYear { get; set; }

    public int StayYear { get; set; }

    [Precision(18, 2)]
    public decimal GrossBase { get; set; }

    [Precision(18, 2)]
    public decimal WithholdingAmount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
