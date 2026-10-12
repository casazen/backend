using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

/// <summary>
/// Admin override of the AI provider (one row per database). The API key is encrypted at rest. Empty fields fall back
/// to the server configuration (<c>Ai__*</c>). The running process reads this row, so a save takes effect without a restart.
/// </summary>
[Table("PlatformAiSettings")]
public class PlatformAiSettings
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary><c>Stub</c> or <c>DeepSeek</c>.</summary>
    [Required, MaxLength(32)]
    public string Provider { get; set; } = "Stub";

    /// <summary>Clear key. Null: the server configuration's key is used, when there is one.</summary>
    [MaxLength(4000)]
    public string? ApiKey { get; set; }

    /// <summary>Null: the default model of the chosen provider is used.</summary>
    [MaxLength(100)]
    public string? Model { get; set; }

    /// <summary>Null: the default base URL of the chosen provider. Required for <c>OpenAiCompatible</c>.</summary>
    [MaxLength(300)]
    public string? BaseUrl { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(255)]
    public string? UpdatedBy { get; set; }
}
