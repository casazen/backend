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
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException">
    /// Code <c>supplier_email_taken</c>: another supplier profile already has this email (trimmed, case-insensitive;
    /// SU-14). Its owner links it with <see cref="ClaimAsync"/>.
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
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException">
    /// Code <c>supplier_email_taken</c>: a supplier profile already has this email, so the invite could never be
    /// accepted (SU-14).
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
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException">
    /// Code <c>supplier_email_taken</c>: a profile would have to be provisioned, but another profile already has the
    /// email (SU-14). The account links that profile with <see cref="ClaimAsync"/> instead of getting a duplicate.
    /// </exception>
    Task<Guid?> GetOrProvisionSupplierOrgIdAsync(
        string userId,
        string email,
        string firstName,
        string lastName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Admin repair of supplier profiles (SU-14, A4-22), in one transaction under an advisory lock. Idempotent.
    /// <list type="number">
    /// <item>Profiles with the same email (trimmed, case-insensitive; blank emails are never merged) are merged into one
    /// keeper: the active profile, then the one with linked accounts, then the oldest. Service requests, availability
    /// (the keeper's day wins), categories, comuni, accounts (<c>User.SupplierOrgId</c>, <c>User.OrgId</c>) and devices
    /// move to the keeper, then the duplicate profile and org are deleted (the org is kept, without its supplier
    /// profile, when it also holds host data). A group with a suspended profile, or with profiles held by several
    /// accounts, is not merged: it is reported as a manual intervention.</item>
    /// <item>Accounts whose <c>SupplierOrgId</c> points to a deleted org are unlinked; accounts whose <c>OrgId</c> is a
    /// supplier org get the matching <c>SupplierOrgId</c>.</item>
    /// <item>Profiles held by no account are only reported: nothing is ever linked by email (A4-23). Accounts with the
    /// same email are listed as a manual intervention (the supplier links the profile with the claim, SU-02).</item>
    /// </list>
    /// With <paramref name="dryRun"/> every step runs and is reported, then the transaction is rolled back.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException">
    /// Code <c>supplier_maintenance_conflict</c>: a concurrent change stopped the run (nothing was saved; retry).
    /// </exception>
    Task<FixOrphanedSupplierOrgsReport> FixOrphanedSupplierOrgsAsync(
        bool dryRun,
        CancellationToken cancellationToken = default);

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

/// <summary>
/// Profile, availability and calendar state of a supplier org. The work KPIs are computed from its service requests by
/// <see cref="ISupplierKpiService"/> (SU-11).
/// </summary>
public record SupplierDashboard(
    int ProfileCompletionPercent,
    string Status,
    double AvailabilityRate,
    string CalendarSyncType,
    string? IcalFeedUrl,
    DateTime? CalendarLastSyncAt,
    string? CalendarSyncError,
    DateTime LastUpdated);

/// <summary>Outcome of <see cref="ISupplierService.FixOrphanedSupplierOrgsAsync"/>. Ids and counts only, no personal data.</summary>
/// <param name="DryRun">True when nothing was saved.</param>
/// <param name="ProfilesScanned">Supplier profiles at the start of the run.</param>
/// <param name="DuplicateGroups">Emails used by more than one profile.</param>
/// <param name="Merges">One entry per duplicate profile merged into its keeper.</param>
/// <param name="DanglingLinksCleared">Accounts whose <c>SupplierOrgId</c> pointed to a deleted org (now unlinked).</param>
/// <param name="SupplierLinksBackfilled">Accounts whose <c>OrgId</c> is a supplier org that got the matching <c>SupplierOrgId</c>.</param>
/// <param name="OrphanProfiles">Profiles held by no account and with no account of the same email: left for their claim.</param>
/// <param name="ManualInterventions">Cases the repair does not decide alone; nothing was changed for them.</param>
public record FixOrphanedSupplierOrgsReport(
    bool DryRun,
    int ProfilesScanned,
    int DuplicateGroups,
    IReadOnlyList<SupplierDuplicateMerge> Merges,
    IReadOnlyList<string> DanglingLinksCleared,
    IReadOnlyList<string> SupplierLinksBackfilled,
    IReadOnlyList<Guid> OrphanProfiles,
    IReadOnlyList<SupplierManualIntervention> ManualInterventions);

/// <summary>A duplicate supplier profile merged into (or, in a dry run, to be merged into) its keeper.</summary>
/// <param name="DuplicateOrgDeleted">
/// False when the duplicate org also holds host data (properties, consents, ...): only its supplier profile is removed.
/// </param>
public record SupplierDuplicateMerge(
    Guid KeeperOrgId,
    Guid DuplicateOrgId,
    int ServiceRequestsMoved,
    int AvailabilityDaysMoved,
    int AvailabilityDaysDropped,
    IReadOnlyList<string> CategoriesAdded,
    IReadOnlyList<string> ComuniAdded,
    int SupplierLinksMoved,
    int OrgMembersMoved,
    int DevicesMoved,
    bool DuplicateOrgDeleted);

/// <summary>A case left untouched for an admin decision (<see cref="Code"/>: see <c>docs/runbooks/suppliers.md</c>).</summary>
public record SupplierManualIntervention(string Code, IReadOnlyList<Guid> OrgIds, IReadOnlyList<string> UserIds);
