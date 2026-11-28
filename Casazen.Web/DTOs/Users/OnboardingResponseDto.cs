namespace Casazen.Web.DTOs.Users;

public class OnboardingResponseDto
{
    public string[] RolesAssigned { get; set; } = [];
    public string RentalType { get; set; } = string.Empty;
    public Guid? OrgId { get; set; }
    public bool OrgProvisioned { get; set; }
    public bool ConsentsRecorded { get; set; }

    /// <summary>
    /// True when the Auth0 roles were updated. When false the DB (and therefore backend
    /// authorization) is already updated but the JWT will not carry the new roles until the sync
    /// succeeds: the client should warn the user and retry (PUT /api/users/onboarding).
    /// </summary>
    public bool RolesSynced { get; set; }

    public string? RolesSyncError { get; set; }
}
