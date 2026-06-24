using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public interface ISupplierService
{
    /// <summary>
    /// Registers a new supplier org. If <paramref name="inviteToken"/> is provided it is validated
    /// against an outstanding admin invite; otherwise self-serve registration is assumed.
    /// When <paramref name="userId"/> is provided the caller's User record is linked to the new org.
    /// </summary>
    Task<(Org Org, SupplierProfile Profile)> RegisterAsync(
        string email,
        string legalName,
        string phone,
        string comuneCode,
        string? inviteToken,
        string? userId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the <see cref="SupplierProfile"/> for the given supplier org.</summary>
    Task<SupplierProfile?> GetProfileAsync(Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>Updates mutable profile fields. Returns the updated profile or null if not found.</summary>
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
    /// Returns <see cref="SupplierStatus.Active"/> suppliers for a given comune (AC6).
    /// </summary>
    Task<IReadOnlyList<SupplierProfile>> GetActiveByComune(string comuneCode, string? category, CancellationToken cancellationToken = default);

    /// <summary>Creates an admin invite record. Returns the generated invite id.</summary>
    Task<SupplierInvite> CreateInviteAsync(
        string email,
        string comuneCode,
        IEnumerable<string>? categories,
        string? message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the caller's supplier org, provisioning org + profile when a Supplier user has none yet.
    /// Supports dual-role users whose <c>User.OrgId</c> points at a host org.
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
    /// Updates calendar sync settings for a supplier profile. Self-contained save — does not
    /// depend on <see cref="UpdateProfileAsync"/> side-effects.
    /// </summary>
    Task<SupplierProfile?> UpdateCalendarSyncAsync(
        Guid orgId,
        CalendarSyncType syncType,
        string? icalFeedUrl,
        string? calendarSyncError,
        CancellationToken cancellationToken = default);
}

public record ActivationStep(string Id, string Label, string Status, string? Blocker = null);

public record SupplierInvite(Guid InviteId, DateTime ExpiresAt);

public record FixOrphanedSupplierOrgsReport(
    int ProfilesScanned,
    int UsersLinked,
    int DuplicatesMerged,
    int EmptyOrgsDeleted,
    int OrphansSkipped,
    IReadOnlyList<string> Details);
