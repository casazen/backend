using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

[Table("OtaIntegrations")]
public class OtaIntegration : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey("Property")]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    /// <summary>Tenant of the row, copied from <see cref="Property"/> when it is created (TN-2).</summary>
    public Guid OrgId { get; set; }

    [Required, MaxLength(50)]
    public string Platform { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string ExternalPropertyId { get; set; } = string.Empty;

    [MaxLength(1000)]
    [JsonIgnore]
    public string ApiKey { get; set; } = string.Empty;

    [MaxLength(1000)]
    [JsonIgnore]
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