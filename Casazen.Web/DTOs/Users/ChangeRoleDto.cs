using System.ComponentModel.DataAnnotations;

namespace Casazen.Web.DTOs.Users;

public class ChangeRoleDto
{
    [Required]
    public string Role { get; set; } = string.Empty;
}
