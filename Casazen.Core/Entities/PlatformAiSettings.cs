using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

[Table("PlatformAiSettings")]
public class PlatformAiSettings
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(32)]
    public string Provider { get; set; } = "Stub";

    [MaxLength(4000)]
    public string? ApiKey { get; set; }

    [MaxLength(100)]
    public string? Model { get; set; }

    [MaxLength(300)]
    public string? BaseUrl { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(255)]
    public string? UpdatedBy { get; set; }
}
