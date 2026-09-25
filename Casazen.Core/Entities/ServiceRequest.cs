using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

[Table("ServiceRequests")]
public class ServiceRequest
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    /// <summary>
    /// The stay the request is for: set for every short-rent request created since SU-07 (D2), null for long-rent
    /// requests and for older short-rent requests that could not be traced to a single stay.
    /// </summary>
    public Guid? BookingId { get; set; }

    /// <summary>Rental context the request was opened in (D2): short-rent (per stay) or long-rent (per property).</summary>
    public ServiceRequestRentalContext RentalContext { get; set; } = ServiceRequestRentalContext.ShortRent;

    public Guid PropertyId { get; set; }

    public Guid SupplierOrgId { get; set; }

    [Required, MaxLength(100)]
    public string Category { get; set; } = string.Empty;

    public ServiceRequestUrgency Urgency { get; set; } = ServiceRequestUrgency.Normal;

    [MaxLength(1000)]
    public string Notes { get; set; } = string.Empty;

    public ServiceRequestStatus Status { get; set; } = ServiceRequestStatus.Richiesto;

    public DateTime? TakenAt { get; set; }

    [MaxLength(255)]
    public string? TakenByUserId { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime? PaidAt { get; set; }

    public bool ChargeToGuest { get; set; }

    [MaxLength(500)]
    public string? RejectionReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optimistic concurrency token (A4-19): mapped to PostgreSQL's <c>xmin</c> system column, which changes with every
    /// update of the row (no column of its own). Two transitions that read the same state cannot both be saved: the
    /// second save fails and the caller gets 409 <c>service_request_state_changed</c>.
    /// </summary>
    public uint Version { get; set; }

    [ForeignKey(nameof(OrgId))]
    public Org Org { get; set; } = null!;

    [ForeignKey(nameof(BookingId))]
    public Booking? Booking { get; set; }

    [ForeignKey(nameof(PropertyId))]
    public Property Property { get; set; } = null!;

    [ForeignKey(nameof(SupplierOrgId))]
    public Org SupplierOrg { get; set; } = null!;
}
