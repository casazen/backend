namespace Casazen.Web.DTOs.Users;

public class UserDetailDto : UserSummaryDto
{
    public string? PhoneNumber { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>The caller's organization id (AC9). <c>null</c> for a brand-new user pre-backfill.</summary>
    public Guid? OrgId { get; set; }

    /// <summary>The caller's organization summary (AC9), or <c>null</c> when the user has no org.</summary>
    public OrgSummaryDto? Org { get; set; }
}
