using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

[Table("SupplierJobs")]
public class SupplierJob
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SupplierOrgId { get; set; }

    [MaxLength(500)]
    public string Description { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? PropertyAddress { get; set; }

    public DateTime ScheduledStartUtc { get; set; }
    public DateTime ScheduledEndUtc { get; set; }

    public decimal Price { get; set; }

    public SupplierJobStatus Status { get; set; } = SupplierJobStatus.Offered;

    public DateTime? CheckedInAt { get; set; }
    public DateTime? CheckedOutAt { get; set; }

    [MaxLength(100)]
    public string? CheckInLocation { get; set; }

    [MaxLength(36)]
    public string? CheckInToken { get; set; }
    public DateTime? CheckInTokenExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(SupplierOrgId))]
    public Org Org { get; set; } = null!;
}
