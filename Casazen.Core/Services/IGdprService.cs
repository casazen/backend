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

    /// <summary>
    /// Complete, versioned export (art. 15 and 20 GDPR), document decrypted; audited. The data of the guest is what <c>guest.read</c> opens; the
    /// <b>messages</b> of its bookings (E07/F06/US02/T01) follow the rules of the message screens of the host instead: only the threads of the
    /// properties <paramref name="scope"/> reaches, and only when the caller holds <c>booking.read</c> (<paramref name="includeMessages"/>).
    /// </summary>
    /// <param name="scope">The caller's reach, from <see cref="Casazen.Core.Authorization.IHostScopeResolver"/>; its org must be <paramref name="orgId"/>. Never widened: a scope is always passed.</param>
    /// <param name="includeMessages">The caller holds <c>booking.read</c> on the org; false leaves the messages out of the export.</param>
    Task<GuestDataExport> ExportGuestDataAsync(Guid orgId, Guid guestId, Casazen.Core.Authorization.HostScope scope, bool includeMessages, string? actorUserId, CancellationToken cancellationToken = default);

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
    /// check-in portal or at the checkout of the booking site): <paramref name="marketingConsent"/> true raises <c>DomainRuleException</c>
    /// (<c>gdpr_marketing_consent_host_grant_forbidden</c>, 422). A withdrawal needs the guest's documented request in
    /// <paramref name="note"/> (<c>gdpr_marketing_withdrawal_note_required</c>); withdrawing a consent that is not in
    /// force (the flag is unset and the guest has no open CRM contact) changes nothing. A withdrawal that is recorded also puts the e-mail of the
    /// guest and every marketing e-mail of its per-channel consents in the suppression list (<see cref="ISuppressionService"/>, source
    /// <c>manual</c>), and flags the CRM contacts with the same keyed hash, in the same unit of work (E07/F06/US01/T02, AC-NF95-6). <b>It ends
    /// every consent the guest gave</b>, not only the one the flag says (E07/F04/US01): the open CRM contacts of the guest, for example a
    /// consent on WhatsApp alone (which leaves the flag unset) or on an address typed on the guest page, are suppressed too, each with its row of
    /// the privacy history (<see cref="ICrmContactService.WithdrawAsync"/>). <b>The withdrawal never depends on the key of that index</b>: without
    /// <c>Crm:ContactIndexKey</c> (allowed while <c>Features:CrmMarketing</c> is off) the consent record, the audit entry and the flag are saved
    /// all the same, the suppression is skipped and an error in the log names the withdrawal and the variable.
    /// </summary>
    Task UpdateMarketingConsentAsync(
        Guid orgId,
        Guid guestId,
        bool marketingConsent,
        string? note,
        string? actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The fiscal data of the org (Partita IVA, codice fiscale, the fiscal years and the taxpayers of the properties) and the
    /// people who have access. An act of the holder of the org: the endpoint is open to the owner and the administrators only
    /// (<c>OrgBillingAdmin</c>, AM-03b). The reach of the caller is applied anyway: the sections that belong to a property
    /// (<c>propertyFiscalYears</c>, <c>propertyTaxpayers</c>) hold only the properties <paramref name="scope"/> reaches, so a
    /// scope narrower than the org never reads another person's properties. For the holder (an org-wide scope) the answer is
    /// complete and unchanged.
    /// </summary>
    /// <param name="scope">The caller's reach, from <see cref="Casazen.Core.Authorization.IHostScopeResolver"/>; its org must be <paramref name="orgId"/>.</param>
    Task<Dictionary<string, object>> ExportOrgFiscalDataAsync(
        Guid orgId,
        Casazen.Core.Authorization.HostScope scope,
        CancellationToken cancellationToken = default);

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
