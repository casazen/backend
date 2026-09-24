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

    /// <summary>
    /// Own profile only (<c>GET/PUT /api/users/me</c>), <c>null</c> elsewhere: true while the host features are withheld
    /// (PL-02), i.e. the onboarding is not completed or the current Terms, Privacy notice and DPA are not accepted. The
    /// host endpoints then answer 403 <c>onboarding_required</c>. Platform admins and suppliers use their own areas
    /// meanwhile.
    /// </summary>
    public bool? OnboardingRequired { get; set; }

    /// <summary>
    /// Own profile only, <c>null</c> elsewhere: the current versions of Terms, Privacy notice and DPA are accepted for the
    /// user's org. False with a completed onboarding means a document changed and must be accepted again.
    /// </summary>
    public bool? ConsentsAccepted { get; set; }
}
