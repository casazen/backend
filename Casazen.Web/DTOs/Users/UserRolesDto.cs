namespace Casazen.Web.DTOs.Users;

public class UserRolesDto
{
    public string Id { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = [];

    public List<string> RolesGranted { get; set; } = [];

    public List<string> RolesRevoked { get; set; } = [];
}
