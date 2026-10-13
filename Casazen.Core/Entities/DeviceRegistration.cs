using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

[Table("DeviceRegistrations")]
public class DeviceRegistration
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(128)]
    public string UserId { get; set; } = string.Empty;

    public Guid OrgId { get; set; }

    [Required, MaxLength(16)]
    public string Platform { get; set; } = string.Empty;

    [Required, MaxLength(512)]
    public string PushToken { get; set; } = string.Empty;

    [Required, MaxLength(128)]
    public string DeviceId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual Org? Org { get; set; }
}
