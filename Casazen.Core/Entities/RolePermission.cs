using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

[Table("RolePermissions")]
public class RolePermission
{
    [Required]
    public int RoleId { get; set; }

    [Required, MaxLength(128)]
    public string PermissionKey { get; set; } = string.Empty;

    public Role Role { get; set; } = null!;
}
