using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

[Table("GuestCheckInSessions")]
public class GuestCheckInSession
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The booking this session belongs to.</summary>
    public Guid BookingId { get; set; }
    public virtual Booking Booking { get; set; } = null!;

    /// <summary>Tenant key — copied from booking at creation time.</summary>
    public Guid OrgId { get; set; }

    /// <summary>SHA-256 hex hash of the raw token sent to the guest.</summary>
    [Required, MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public GuestCheckInSessionStatus Status { get; set; } = GuestCheckInSessionStatus.Inviato;

    public DateTime? SentAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum GuestCheckInSessionStatus
{
    /// <summary>Email sent to guest, not yet opened.</summary>
    Inviato,
    /// <summary>Guest opened the link; form in progress.</summary>
    InCompilazione,
    /// <summary>Guest submitted all data; pending Alloggiati enqueue.</summary>
    Completo,
    /// <summary>AlloggiatiWebReportJob has been enqueued successfully.</summary>
    AlloggiatiInviato,
    /// <summary>Token expired or manually invalidated (resend-link flow).</summary>
    Scaduto,
}
