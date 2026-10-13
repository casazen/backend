using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

/// <summary>
/// One row for the whole installation. The admin turns the demo environment on from the staff console; it does not
/// exist in Production (<see cref="Services.IDemoEnvironmentGate"/>).
/// </summary>
[Table("DemoEnvironmentSettings")]
public class DemoEnvironmentSetting
{
    public const int SingletonId = 1;

    [Key]
    public int Id { get; set; } = SingletonId;

    public bool Enabled { get; set; }

    public DateTime? LastResetAt { get; set; }

    [MaxLength(255)]
    public string? LastResetBy { get; set; }

    [MaxLength(40)]
    public string TemplateVersion { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
