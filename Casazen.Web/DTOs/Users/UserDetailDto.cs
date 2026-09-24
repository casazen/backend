namespace Casazen.Web.DTOs.Users;

public class UserDetailDto : UserSummaryDto
{
    public string? PhoneNumber { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>UTC timestamp when user completed onboarding. Null if user has not completed onboarding yet.</summary>
    public DateTime? OnboardingCompletedAt { get; set; }

    /// <summary>The caller's organization summary (AC9), or <c>null</c> when the user has no org.</summary>
    public OrgSummaryDto? Org { get; set; }

    /// <summary>
    /// The supplier org the user is linked to (invite, registration or claim), or <c>null</c>. For a supplier-only user
    /// it is also <see cref="UserSummaryDto.OrgId"/>. The web app sends a linked supplier to the supplier console, never
    /// to the host onboarding, even before the Auth0 <c>Supplier</c> role reaches the token (SU-02, A4-02).
    /// </summary>
    public Guid? SupplierOrgId { get; set; }
}
