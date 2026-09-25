using Casazen.Core.Models;

namespace Casazen.Core.Services;

/// <summary>
/// Rights of the guests on their personal data (CO-15, runbook <c>docs/runbooks/gdpr.md</c>). Guest operations act only
/// on a guest of <c>orgId</c> (TN-1): any other guest, including the guest of another org, raises
/// <c>NotFoundException</c> (<c>guest_not_found</c>). <c>actorUserId</c> is the host who asked, recorded in the audit
/// (<c>GuestPrivacyAuditEntry</c>, never personal data of the guest).
/// </summary>
public interface IGdprService
{
    /// <summary>Consents with their versions, retention per category and status, for the host's GDPR tab.</summary>
    Task<GuestPrivacySummary> GetGuestPrivacySummaryAsync(Guid orgId, Guid guestId, CancellationToken cancellationToken = default);

    /// <summary>Complete, versioned export (art. 15 and 20 GDPR), document decrypted; audited.</summary>
    Task<GuestDataExport> ExportGuestDataAsync(Guid orgId, Guid guestId, string? actorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Erasure (art. 17 GDPR): every personal field of the guest and of the guests of its stays, the consent IPs and notes,
    /// the special requests of its bookings and the document scan in the private storage; the record is marked deleted
    /// and kept for its bookings. Idempotent (a second call changes and audits nothing). Raises
    /// <c>DomainConflictException</c> (<c>guest_has_open_bookings</c>) while a booking of the guest is open.
    /// </summary>
    Task EraseGuestDataAsync(Guid orgId, Guid guestId, string reason, string? actorUserId, CancellationToken cancellationToken = default);

    /// <summary>Same anonymization as <see cref="EraseGuestDataAsync"/> without marking the record deleted.</summary>
    Task AnonymizeGuestDataAsync(Guid orgId, Guid guestId, string? actorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the stored files of a guest that is about to be removed (no booking references it): the document scan
    /// object, unless another guest record still points to it. Audited as an erasure.
    /// </summary>
    Task EraseStoredFilesBeforeRemovalAsync(Guid orgId, Guid guestId, string? actorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Change of the marketing consent asked by the host. The host can never grant it (only the guest can, on the
    /// check-in portal): <paramref name="marketingConsent"/> true raises <c>DomainRuleException</c>
    /// (<c>gdpr_marketing_consent_host_grant_forbidden</c>, 422). A withdrawal needs the guest's documented request in
    /// <paramref name="note"/> (<c>gdpr_marketing_withdrawal_note_required</c>); withdrawing a consent that is not in
    /// force changes nothing.
    /// </summary>
    Task UpdateMarketingConsentAsync(
        Guid orgId,
        Guid guestId,
        bool marketingConsent,
        string? note,
        string? actorUserId,
        CancellationToken cancellationToken = default);

    Task<Dictionary<string, object>> ExportOrgFiscalDataAsync(Guid orgId, CancellationToken cancellationToken = default);
    Task AnonymizeOrgFiscalDataAsync(Guid orgId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Applies the retention period of each category of guest data (<c>Gdpr:Retention</c>, CO-15) to every org. A category
/// without a configured period and source is skipped with a warning: nothing is deleted without a documented period.
/// Idempotent: rows already processed are marked and never processed again.
/// </summary>
public interface IGuestDataRetentionService
{
    Task<GuestRetentionRunResult> ApplyAsync(CancellationToken cancellationToken = default);
}
