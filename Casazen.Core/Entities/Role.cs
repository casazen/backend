using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

[Table("Roles")]
public class Role
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(64)]
    public string ContextKey { get; set; } = string.Empty;

    [Required, MaxLength(64)]
    public string RoleKey { get; set; } = string.Empty;

    public AppContext Context { get; set; } = null!;
    public ICollection<RolePermission> Permissions { get; set; } = new List<RolePermission>();
    public ICollection<UserContextMembership> Memberships { get; set; } = new List<UserContextMembership>();
}
