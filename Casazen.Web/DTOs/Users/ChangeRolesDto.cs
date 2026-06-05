using System.ComponentModel.DataAnnotations;

namespace Casazen.Web.DTOs.Users;

public class ChangeRolesDto
{
    [Required]
    [MinLength(1)]
    public string[] Roles { get; set; } = [];
}
