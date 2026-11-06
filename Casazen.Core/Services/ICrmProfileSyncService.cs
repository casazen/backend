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
/// </remarks>
public interface ICrmProfileSyncService
{
    /// <summary>
    /// True when the sync can run: the keyed index of the CRM has its key (<c>Crm:ContactIndexKey</c>, or the fixed key of Development and
    /// Testing). False outside them without the key, which the application allows while <c>Features:CrmMarketing</c> is off: the callers then do
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

    /// <summary>The keyed index has no key (<see cref="ICrmProfileSyncService.IsAvailable"/> is false): nothing was read or written.</summary>
    NotAvailable = 2,

    /// <summary>The org has no such booking (deleted since the event was recorded): nothing to do.</summary>
    BookingNotFound = 3,

    /// <summary>The org has no such guest, or the guest is erased, anonymized or asked for erasure: nothing is kept of them in the CRM.</summary>
    GuestNotSyncable = 4,
}
