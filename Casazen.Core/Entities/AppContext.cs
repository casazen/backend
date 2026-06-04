using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

[Table("AppContexts")]
public class AppContext
{
    [Key, MaxLength(64)]
    public string Key { get; set; } = string.Empty;

    [Required, MaxLength(128)]
    public string DisplayName { get; set; } = string.Empty;

    public ICollection<Role> Roles { get; set; } = new List<Role>();
    public ICollection<UserContextMembership> Memberships { get; set; } = new List<UserContextMembership>();
}
