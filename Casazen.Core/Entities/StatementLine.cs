using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

[Table("StatementLines")]
public class StatementLine : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    public Guid StatementId { get; set; }
    public virtual OwnerStatement Statement { get; set; } = null!;

    public OwnerStatementLineType Type { get; set; }

    public Guid? BookingId { get; set; }
    public virtual Booking? Booking { get; set; }

    /// <summary>
    /// For Type = OtaRevenue: channel name (e.g., "Booking.com", "Airbnb"). Max 200 chars.
    /// For Type = Expense: expense category (e.g., "Cleaning", "Linen", "Utilities").
    /// For Type = Adjustment: reason for the adjustment.
    /// Null for Type = Booking (channel label is on the Booking entity).
    /// </summary>
    [MaxLength(200)]
    public string? Label { get; set; }

    [MaxLength(1000)]
    public string? Description { get; set; }

    [Precision(18, 2)]
    public decimal GrossRevenue { get; set; }

    [Precision(18, 2)]
    public decimal Commission { get; set; }

    [Precision(18, 2)]
    public decimal Expenses { get; set; }

    /// <summary>
    /// Withholding tax on this line (for owners under cedolare secca or similar regimes). Full precision (ADR-017-R1).
    /// Zero for adjustments. Calculated from the owner's tax regime and mandate.
    /// </summary>
    [Precision(18, 2)]
    public decimal Withholding { get; set; }

    public int CashYear { get; set; }

    public int? StayYear { get; set; }

    [MaxLength(500)]
    public string? AttachmentUrl { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
