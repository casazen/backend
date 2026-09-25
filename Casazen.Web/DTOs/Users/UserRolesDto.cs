namespace Casazen.Web.DTOs.Users;

/// <summary>Response of the roles endpoints (A1-17): the user's current admin-manageable role set.</summary>
public class UserRolesDto
{
    public string Id { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = [];

    /// <summary>Roles granted by this call (empty on a plain read or when nothing changed).</summary>
    public List<string> RolesGranted { get; set; } = [];

    /// <summary>Roles revoked by this call (empty on a plain read or when nothing changed).</summary>
    public List<string> RolesRevoked { get; set; } = [];
}
