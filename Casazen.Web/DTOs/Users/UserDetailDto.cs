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

    /// <summary>
    /// Own profile only, <c>null</c> elsewhere (and for a user without a host org): the user's org has at least one
    /// property. False means the host still has to create the first one; the web app takes a host with a completed
    /// onboarding to the first-property creation, not back to the wizard. Independent of
    /// <see cref="OnboardingRequired"/>: it never gates an endpoint.
    /// </summary>
    public bool? HasProperties { get; set; }

    /// <summary>
    /// Own profile only (<c>GET/PUT /api/users/me</c>), <c>null</c> elsewhere: Auth0 says the account's email address was
    /// verified (the Action claim <c>https://casazen.app/email_verified</c>, else the Management API, see
    /// <c>AccountEmailResolver</c>). The guest area of the app (<c>api/guest/*</c>) shows the bookings of the address only
    /// when this is true; it is read from the access token, so after the person verifies the address the app needs a new
    /// token (refresh) before the value changes.
    /// </summary>
    public bool? EmailVerified { get; set; }
}
