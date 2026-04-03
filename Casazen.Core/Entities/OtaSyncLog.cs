using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

/// <summary>
/// Audit log for OTA booking synchronization operations
/// Tracks which bookings were created/updated/cancelled during each sync
/// </summary>
[Table("OtaSyncLogs")]
public class OtaSyncLog
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey("Property")]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    [Required]
    [MaxLength(50)]
    public string Platform { get; set; } = string.Empty;

    [Required]
    public DateTime SyncStartedAt { get; set; } = DateTime.UtcNow;

    public DateTime? SyncCompletedAt { get; set; }

    public bool Success { get; set; }

    public int BookingsCreated { get; set; }
    public int BookingsUpdated { get; set; }
    public int BookingsCancelled { get; set; }

    [MaxLength(2000)]
    public string ErrorMessage { get; set; } = string.Empty;

    [MaxLength(5000)]
    public string Details { get; set; } = string.Empty;
}
