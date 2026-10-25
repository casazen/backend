using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// Individual line item within an owner statement (ADR-017).
/// Per-booking revenue breakdown, OTA amounts (manual entry + CSV import), expenses, adjustments.
/// All amounts stored with full precision decimal(18,2) without intermediate rounding (ADR-017-R1).
/// </summary>
[Table("StatementLines")]
public class StatementLine : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key. Inherited from OwnerStatement; never client-supplied.</summary>
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    /// <summary>The statement this line belongs to.</summary>
    public Guid StatementId { get; set; }
    public virtual OwnerStatement Statement { get; set; } = null!;

    /// <summary>Type of line: booking, OTA revenue, expense, or adjustment (ADR-017-R5).</summary>
    public OwnerStatementLineType Type { get; set; }

    /// <summary>
    /// Booking this line refers to (for Type = Booking). Null for OTA revenue, expenses, and adjustments.
    /// </summary>
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

    /// <summary>
    /// Description for the line. Used for adjustments (reason text), expenses (itemized detail),
    /// or OTA revenue (booking reference from the OTA CSV).
    /// </summary>
    [MaxLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// Gross revenue for the line (before commission and expenses). Full precision (ADR-017-R1).
    /// Positive for revenue (Booking, OtaRevenue), negative for expenses, positive or negative for adjustments.
    /// </summary>
    [Precision(18, 2)]
    public decimal GrossRevenue { get; set; }

    /// <summary>
    /// Commission charged on this line (as per the owner's mandate at the time). Full precision (ADR-017-R1).
    /// Zero for expenses and adjustments. For OtaRevenue, the commission from the OTA CSV (ADR-017-R6).
    /// </summary>
    [Precision(18, 2)]
    public decimal Commission { get; set; }

    /// <summary>
    /// Expenses charged to the owner on this line (cleaning, linen, payment fees). Full precision (ADR-017-R1).
    /// Zero for adjustments. For Type = Expense, the full expense amount goes here (GrossRevenue is zero).
    /// </summary>
    [Precision(18, 2)]
    public decimal Expenses { get; set; }

    /// <summary>
    /// Withholding tax on this line (for owners under cedolare secca or similar regimes). Full precision (ADR-017-R1).
    /// Zero for adjustments. Calculated from the owner's tax regime and mandate.
    /// </summary>
    [Precision(18, 2)]
    public decimal Withholding { get; set; }

    /// <summary>
    /// Year the revenue was collected (cash year, ADR-017). For a booking, the year of the payment.
    /// For OTA revenue imported from CSV, the year from the NetCollected date in the CSV.
    /// For expenses and adjustments, the year the line was created.
    /// </summary>
    public int CashYear { get; set; }

    /// <summary>
    /// Year the stay occurred (stay year, ADR-017). For a booking, the year of the check-in.
    /// For OTA revenue, the year from the CSV (if provided) or the check-in year (if the booking is linked).
    /// Null for expenses and adjustments.
    /// </summary>
    public int? StayYear { get; set; }

    /// <summary>
    /// Reference to an attachment (expense receipt, OTA CSV file) stored in blob storage.
    /// URL of the file (PDF, image, CSV).
    /// </summary>
    [MaxLength(500)]
    public string? AttachmentUrl { get; set; }

    /// <summary>When this line was created (UTC).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
