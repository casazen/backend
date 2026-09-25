using System.ComponentModel.DataAnnotations;

namespace Casazen.Web.DTOs.Users;

/// <summary>
/// Request body of <c>PUT /api/users/{id}/roles</c> (A1-17): the user's exact role set among
/// <see cref="Casazen.Core.Services.AdminManageableRoles.All"/>. An empty list is valid (strips every
/// admin-manageable role, leaving <c>UserRole.None</c>).
/// </summary>
public class UpdateUserRolesDto
{
    [Required]
    public List<string> Roles { get; set; } = [];
}
