namespace Casazen.Web.DTOs.Users;

public class UserSummaryDto
{
    public string Id { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string? RentalType { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? OrgId { get; set; }
    public string? OrgName { get; set; }

    /// <summary>Effective plan tier of the user's org (Starter unless a subscription pays for it).</summary>
    public string? PlanTier { get; set; }
}
