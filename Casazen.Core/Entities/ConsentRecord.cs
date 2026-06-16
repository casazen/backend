using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

[Table("ConsentRecords")]
public class ConsentRecord
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(255)]
    public string UserId { get; set; } = string.Empty;

    public Guid OrgId { get; set; }

    public ConsentType Type { get; set; }

    [Required, MaxLength(100)]
    public string Version { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? IpAddress { get; set; }

    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}
