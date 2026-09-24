using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public interface ISupplierService
{
    /// <summary>
    /// Registers a new supplier org (SU-01). With an invite token the invite must be valid, unused and not expired, and
    /// is accepted only by a signed-in user whose account email is the invited one, for the invited comune; the invite
    /// is then used up. Without a token (self-serve) the comune must be a configured pilot comune and, when signed in,
    /// the email must be the account email. When <see cref="SupplierRegistration.UserId"/> is set the user is linked to
    /// the org; a user already linked to a supplier org gets that registration back. An anonymous self-serve
    /// registration gets a claim token (<see cref="SupplierRegistrationResult.Claim"/>, SU-02) instead: the account
    /// created afterwards links itself with <see cref="ClaimAsync"/>.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
    /// Codes <c>supplier_invite_invalid</c>, <c>supplier_invite_expired</c>, <c>supplier_invite_used</c>,
    /// <c>supplier_invite_login_required</c>, <c>supplier_invite_email_mismatch</c>,
    /// <c>supplier_invite_comune_mismatch</c>, <c>supplier_account_email_missing</c>,
    /// <c>supplier_account_email_mismatch</c>, <c>supplier_self_serve_unavailable</c>, <c>supplier_comune_not_pilot</c>.
    /// </exception>
    Task<SupplierRegistrationResult> RegisterAsync(
        SupplierRegistration registration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Links the signed-in user to a supplier profile registered anonymously (SU-02, A4-02), explicitly and never by an
    /// unverified email (A4-23, A1-13):
    /// <list type="bullet">
    /// <item>with <see cref="SupplierClaim.ClaimToken"/>: the token of that registration, unused by another account and
    /// not expired, and an account email equal to the profile email (the token proves the registrant);</item>
    /// <item>without a token: only when Auth0 verified the account email (<see cref="SupplierClaim.AccountEmailVerified"/>),
    /// the one profile with that email that no account holds yet (registrations whose token was lost or that predate
    /// SU-02).</item>
    /// </list>
    /// A user already linked to a supplier org gets it back (idempotent retry, e.g. after a failed Auth0 role sync).
    /// The caller assigns the Supplier role; this method only links the account.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
    /// Codes <c>supplier_account_email_missing</c>, <c>supplier_claim_invalid</c>, <c>supplier_claim_expired</c>,
    /// <c>supplier_claim_used</c>, <c>supplier_claim_email_mismatch</c>, <c>supplier_claim_email_unverified</c>,
    /// <c>supplier_claim_not_found</c>.
    /// </exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException">
    /// Codes <c>supplier_account_already_linked</c> (the caller holds another supplier org) and
    /// <c>supplier_claim_ambiguous</c> (several unclaimed profiles with the verified email).
    /// </exception>
    Task<SupplierClaimResult> ClaimAsync(SupplierClaim claim, CancellationToken cancellationToken = default);

    /// <summary>
    /// The invite of a link token, to pre-fill the registration page (SU-01). Nothing is changed.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
    /// Codes <c>supplier_invite_invalid</c> (malformed or unknown token), <c>supplier_invite_expired</c>,
    /// <c>supplier_invite_used</c>.
    /// </exception>
    Task<SupplierInvitePreview> GetInviteAsync(string? inviteToken, CancellationToken cancellationToken = default);

    /// <summary>Returns the <see cref="SupplierProfile"/> for the given supplier org.</summary>
    Task<SupplierProfile?> GetProfileAsync(Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>Updates mutable profile fields. Returns the updated profile or null if not found.</summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
    /// Code <c>invalid_service_category</c>: one of <paramref name="categories"/> is not a
    /// <see cref="Casazen.Core.Suppliers.ServiceCategories"/> code.
    /// </exception>
    Task<SupplierProfile?> UpdateProfileAsync(
        Guid orgId,
        string? legalName,
        string? vatNumber,
        string? phone,
        IEnumerable<string>? categories,
        IEnumerable<string>? comuni,
        string? bio,
        IEnumerable<string>? photoUrls,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns wizard step statuses for the activation flow (AC5).
    /// </summary>
    Task<IReadOnlyList<ActivationStep>> GetActivationStepsAsync(Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the profile to <see cref="SupplierStatus.Active"/> when all blockers are satisfied and ToS is accepted.
    /// Throws <see cref="InvalidOperationException"/> (→ 409) if blockers remain.
    /// </summary>
    Task<SupplierProfile> CompleteActivationAsync(Guid orgId, bool tosAccepted, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns availability entries for the supplier within an inclusive date range.
    /// </summary>
    Task<IReadOnlyList<(DateOnly Date, bool Available)>> GetAvailabilityAsync(
        Guid orgId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates availability entries for the supplier (AC8).
    /// Returns the number of rows written.
    /// </summary>
    Task<int> UpdateAvailabilityAsync(
        Guid orgId,
        IEnumerable<(DateOnly Date, bool Available)> entries,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <see cref="SupplierStatus.Active"/> suppliers for a given comune (AC6). With a
    /// <paramref name="category"/> only suppliers that declared that category code are returned (a supplier without
    /// categories matches no category).
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
    /// Code <c>invalid_service_category</c>: <paramref name="category"/> is not a
    /// <see cref="Casazen.Core.Suppliers.ServiceCategories"/> code.
    /// </exception>
    Task<IReadOnlyList<SupplierProfile>> GetActiveByComune(string comuneCode, string? category, CancellationToken cancellationToken = default);

    /// <summary>Creates an admin invite record. Returns the generated invite id.</summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
    /// Code <c>invalid_service_category</c>: one of <paramref name="categories"/> is not a
    /// <see cref="Casazen.Core.Suppliers.ServiceCategories"/> code.
    /// </exception>
    Task<SupplierInvite> CreateInviteAsync(
        string email,
        string comuneCode,
        IEnumerable<string>? categories,
        string? message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the caller's supplier org, provisioning org + profile when a Supplier user has none yet.
    /// Supports dual-role users whose <c>User.OrgId</c> points at a host org. The org comes only from the user's own
    /// link (<c>User.SupplierOrgId</c>, or <c>User.OrgId</c> of a supplier org): never from the email, which the
    /// token does not prove (A4-23, A1-13); an existing profile is joined through an invite or <see cref="ClaimAsync"/>.
    /// <paramref name="email"/> only fills the contact of a provisioned profile.
    /// </summary>
    Task<Guid?> GetOrProvisionSupplierOrgIdAsync(
        string userId,
        string email,
        string firstName,
        string lastName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retroactive fix: detects and repairs orphaned/duplicate supplier profiles.
    /// Links users to their supplier orgs, merges duplicates, and cleans up auto-provisioned
    /// empty profiles. Returns a report of actions taken. Idempotent.
    /// </summary>
    Task<FixOrphanedSupplierOrgsReport> FixOrphanedSupplierOrgsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns aggregated dashboard statistics for a supplier org.
    /// </summary>
    Task<SupplierDashboard> GetDashboardStatsAsync(Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates calendar sync settings for a supplier profile. Self-contained save — does not
    /// depend on <see cref="UpdateProfileAsync"/> side-effects.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
    /// Code <c>ical_invalid_url</c>: <paramref name="icalFeedUrl"/> is not an external https URL the server may download.
    /// </exception>
    Task<SupplierProfile?> UpdateCalendarSyncAsync(
        Guid orgId,
        CalendarSyncType syncType,
        string? icalFeedUrl,
        string? calendarSyncError,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stored category values that are not <see cref="Casazen.Core.Suppliers.ServiceCategories"/> codes, across every
    /// org (platform admin report, SU-03): values the migration <c>NormalizeServiceCategories</c> could not map are
    /// kept, never deleted, and listed here until someone fixes them.
    /// </summary>
    Task<IReadOnlyList<UnmappedServiceCategory>> GetUnmappedCategoriesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A stored category value that is not a known code. <paramref name="Source"/> is <c>supplier_profile</c> (id = supplier
/// org id), <c>supplier_invite</c> (id = invite id) or <c>service_request</c> (id = request id).
/// </summary>
public record UnmappedServiceCategory(string Source, Guid Id, string Value);

public record ActivationStep(string Id, string Label, string Status, string? Blocker = null);

public record SupplierInvite(Guid InviteId, DateTime ExpiresAt);

/// <summary>
/// Outcome of <see cref="ISupplierService.RegisterAsync"/>. <paramref name="Claim"/> is set only for an anonymous
/// self-serve registration: the secret the web app keeps to link the account created afterwards (SU-02).
/// </summary>
public record SupplierRegistrationResult(Org Org, SupplierProfile Profile, SupplierClaimTicket? Claim = null);

/// <summary>A claim token (plain text: returned once, only its hash is stored) and its UTC expiry.</summary>
public record SupplierClaimTicket(string Token, DateTime ExpiresAt);

/// <summary>
/// A claim request of the signed-in user <paramref name="UserId"/> (Auth0 <c>sub</c>). <paramref name="AccountEmail"/>
/// is the account email from the access token or Auth0; <paramref name="AccountEmailVerified"/> is Auth0's
/// <c>email_verified</c> for it (needed only without <paramref name="ClaimToken"/>).
/// </summary>
public record SupplierClaim(string UserId, string? AccountEmail, bool AccountEmailVerified, string? ClaimToken);

/// <summary>The supplier org the caller is linked to; <paramref name="NewlyLinked"/> is false for a repeated claim.</summary>
public record SupplierClaimResult(Guid OrgId, bool NewlyLinked);

/// <summary>
/// A supplier registration request. <paramref name="UserId"/> and <paramref name="AccountEmail"/> are the signed-in
/// caller (Auth0 <c>sub</c> and account email), both null for an anonymous self-serve registration.
/// </summary>
public record SupplierRegistration(
    string Email,
    string LegalName,
    string Phone,
    string ComuneCode,
    string? InviteToken = null,
    string? UserId = null,
    string? AccountEmail = null);

/// <summary>
/// What an invite link grants: the email that must register, the comune (with its display name when known) and the
/// preselected service category codes.
/// </summary>
public record SupplierInvitePreview(
    string Email,
    string ComuneCode,
    string? ComuneName,
    IReadOnlyList<string> Categories,
    DateTime ExpiresAt);

public record SupplierDashboard(
    int ProfileCompletionPercent,
    string Status,
    int TotalJobs,
    int CompletedJobs,
    int UpcomingJobs,
    double AvailabilityRate,
    string CalendarSyncType,
    string? IcalFeedUrl,
    DateTime? CalendarLastSyncAt,
    string? CalendarSyncError,
    DateTime LastUpdated);

public record FixOrphanedSupplierOrgsReport(
    int ProfilesScanned,
    int UsersLinked,
    int DuplicatesMerged,
    int EmptyOrgsDeleted,
    int OrphansSkipped,
    IReadOnlyList<string> Details);
