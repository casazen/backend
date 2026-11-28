using System.ComponentModel.DataAnnotations;

namespace Casazen.Web.DTOs.Users;

public class UpdateUserRolesDto
{
    [Required]
    public List<string> Roles { get; set; } = [];
}
