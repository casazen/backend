using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

[Table("AlloggiatiWebReports")]
public class AlloggiatiWebReport : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey("Booking")]
    public Guid BookingId { get; set; }
    public virtual Booking Booking { get; set; } = null!;

    /// <summary>Tenant of the row, copied from <see cref="Booking"/> when it is created (TN-2).</summary>
    public Guid OrgId { get; set; }

    [ForeignKey("Guest")]
    public Guid GuestId { get; set; }
    public virtual Guest Guest { get; set; } = null!;

    public DateTime ReportedAt { get; set; } = DateTime.UtcNow;

    public AlloggiatiWebStatus Status { get; set; } = AlloggiatiWebStatus.Pending;

    [MaxLength(100)]
    public string? ConfirmationNumber { get; set; }

    [MaxLength(2000)]
    public string? ErrorMessage { get; set; }

    public int RetryCount { get; set; }

    public bool ManuallyCompleted { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum AlloggiatiWebStatus
{
    Pending,
    Submitted,
    Confirmed,
    Failed
}
