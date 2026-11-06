using Casazen.Core.Features;

namespace Casazen.Core.Services;

/// <summary>
/// Keeps the CRM in step with the bookings (E07/F01/US02/T03, NF-88, NF-20): takes the booker of a booking, whatever its source (direct, manual,
/// calendar block, Octorate import), and makes sure the booker has a profile and its contacts and that the profile's aggregates are right. Used
/// by the event handler (<c>CrmBookingEventHandler</c>, which only shortens the delay), by the recurring job <c>crm-profile-sync</c> (which
/// guarantees the result and backfills the guests with no profile) and by the demo seed.
/// </summary>
/// <remarks>
/// Idempotent: running it twice changes nothing, so a retry never duplicates a profile or a contact. It reads the booker's record and the
/// booker's bookings, consents and contacts only, never <c>StayGuest</c>, a document or an Alloggiati field: the guests of a registration never
/// become profiles. A guest erased or anonymized (GDPR) is left alone. Every query names the org, so the service works under the tenant of a
/// request, of the event bus and of a job scope alike.
/// <para><b>Part of the marketing module</b>: it runs only with <c>Features:CrmMarketing</c> on and the key of the index set
/// (<see cref="CrmProfileSyncGate"/>). It copies the e-mail, the phone and the name of the bookers, and a keyed hash of the birth date, into
/// tables that the erasure, the anonymization and the retention of the guests do not reach yet (E07/F06/US02/T01), so it must not run in an
/// environment before that task is delivered.</para>
/// </remarks>
public interface ICrmProfileSyncService
{
    /// <summary>
    /// True when the sync can run: <c>Features:CrmMarketing</c> is on <b>and</b> the keyed index of the CRM has its key (<c>Crm:ContactIndexKey</c>,
    /// or the fixed key of Development and Testing), see <see cref="CrmProfileSyncGate"/>. False when either is missing, and the callers then do
    /// nothing, with no error, instead of failing every booking.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Syncs the booker of a booking of the org: finds or creates the profile (<see cref="IGuestMatchingService.FindOrCreateProfileAsync"/>), builds
    /// the contacts (<see cref="ICrmContactService.SyncFromGuestAsync"/>), recomputes the aggregates of the profile and sets its language from
    /// the most recent contact language and its country from the guest when empty.
    /// </summary>
    Task<CrmSyncOutcome> SyncBookingAsync(Guid orgId, Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same for a guest of the org, with no booking in hand: the backfill of the guests who have no profile yet, and a guest created from the
    /// guest list who never had a booking.
    /// </summary>
    Task<CrmSyncOutcome> SyncGuestAsync(Guid orgId, Guid guestId, CancellationToken cancellationToken = default);
}

/// <summary>What a sync did (<see cref="ICrmProfileSyncService"/>).</summary>
public enum CrmSyncOutcome
{
    /// <summary>The guest has its profile, its contacts and its aggregates.</summary>
    Synced = 1,

    /// <summary>
    /// The module is closed (<see cref="ICrmProfileSyncService.IsAvailable"/> is false: the flag is off or the keyed index has no key): nothing
    /// was read or written.
    /// </summary>
    NotAvailable = 2,

    /// <summary>The org has no such booking (deleted since the event was recorded): nothing to do.</summary>
    BookingNotFound = 3,

    /// <summary>The org has no such guest, or the guest is erased, anonymized or asked for erasure: nothing is kept of them in the CRM.</summary>
    GuestNotSyncable = 4,
}

/// <summary>
/// When the CRM sync exists (E07/F01/US02/T03): <c>Features:CrmMarketing</c> on <b>and</b> the key of the keyed index configured. One rule for
/// the service (<see cref="ICrmProfileSyncService.IsAvailable"/>), the event handler, the job <c>crm-profile-sync</c> and the demo seed, in
/// the way of <see cref="RliProviderFiling"/>. The key alone does not open it: the index serves the always-on paths too (the suppression list of
/// a withdrawal), so the flag is what keeps the sync closed with the rest of the module.
/// </summary>
public static class CrmProfileSyncGate
{
    public static bool IsOpen(IFeatureFlags featureFlags, ICrmContactIndex contactIndex)
    {
        ArgumentNullException.ThrowIfNull(featureFlags);
        ArgumentNullException.ThrowIfNull(contactIndex);
        return featureFlags.IsEnabled(FeatureFlags.CrmMarketing) && contactIndex.IsConfigured;
    }
}
