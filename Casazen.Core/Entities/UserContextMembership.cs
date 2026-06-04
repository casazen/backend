using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

[Table("UserContextMemberships")]
public class UserContextMembership
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(255)]
    public string UserId { get; set; } = string.Empty;

    [Required, MaxLength(64)]
    public string ContextKey { get; set; } = string.Empty;

    [Required]
    public int RoleId { get; set; }

    public User User { get; set; } = null!;
    public AppContext Context { get; set; } = null!;
    public Role Role { get; set; } = null!;
}
