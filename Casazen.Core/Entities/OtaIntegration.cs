using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Enums;

namespace Casazen.Core.Entities;

[Table("OtaIntegrations")]
public class OtaIntegration
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey("Property")]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    [Required, MaxLength(50)]
    public string Platform { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string ExternalPropertyId { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string ApiKey { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string ApiSecret { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
    public bool SyncEnabled { get; set; } = true;

    public DateTime LastSyncAt { get; set; }

    [MaxLength(50)]
    public string? SyncStatus { get; set; }

    [MaxLength(2000)]
    public string? LastSyncError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}