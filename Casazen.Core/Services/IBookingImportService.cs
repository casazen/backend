using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The bookings of the channel manager in CasaZen (E02/F02/US01/T01, ADR-031, ADR-033): reads the reservations of a property through
/// <see cref="IOctorateApiClient"/> and keeps one booking for each, and sends CasaZen's own states back (<see cref="SyncBookingStateAsync"/>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>One org at a time.</b> The caller outside a request (the daily job, the reconciliation, the webhook handlers) opens
/// <c>IOctorateOrgScope.Use(orgId)</c> before it calls the service, because the client methods take no org; the service filters its own
/// queries by the org of the confirmed mapping explicitly, because a job and an event handler run with no EF tenant filter.</item>
/// <item><b>Only a confirmed mapping.</b> The CasaZen property of a reservation is found through the confirmed
/// <c>OctorateListingMapping</c> and its room; a reservation of another room, or of a listing that is not confirmed, is reported in
/// <see cref="ImportResult.Unmapped"/> and never creates a property.</item>
/// <item><b>Idempotent.</b> The key is the Octorate reservation id within the org; a reservation already imported updates its booking,
/// creates no second one and is never an error; one whose <c>updateTime</c> is not newer than the one applied changes nothing.</item>
/// </list>
/// </remarks>
public interface IBookingImportService
{
    /// <summary>
    /// Reads every reservation of the property that Octorate lists as changed since the history began and applies it. The first run of a
    /// listing: it ends by marking the listing as read completely, so the next runs read only what changed.
    /// </summary>
    /// <param name="propertyId">The CasaZen property; it needs a confirmed mapping with its room chosen.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="options">Only the reconciliation passes some.</param>
    Task<ImportResult> ImportAllAsync(Guid propertyId, CancellationToken ct, BookingImportOptions? options = null);

    /// <summary>
    /// Reads the reservations of the property changed since <paramref name="since"/> (a Rome calendar day is read: the caller adds the
    /// overlap it needs) and applies them. The listing is marked as read completely only when <paramref name="since"/> reaches back to the
    /// last complete read.
    /// </summary>
    Task<ImportResult> ImportIncrementalAsync(Guid propertyId, DateTime since, CancellationToken ct, BookingImportOptions? options = null);

    /// <summary>
    /// Sends the new state of a booking CasaZen owns to Octorate: the arrival (<see cref="BookingStatus.CheckedIn"/>) and the departure
    /// (<see cref="BookingStatus.CheckedOut"/>) with the check-in status operation, the cancellation (<see cref="BookingStatus.Cancelled"/>)
    /// of a reservation made in CasaZen with <c>DELETE</c>. Nothing is sent for a state Octorate has no operation for, nor for a booking that
    /// is not mirrored (no Octorate reservation, mapping not confirmed). A reservation that came from a portal is not cancelled from here
    /// (422): the portal owns its cancellation. An inbound change never calls this, so Octorate is not echoed.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">The booking does not exist (or is not visible to the caller).</exception>
    /// <exception cref="Exceptions.DomainRuleException">The cancellation of a portal's reservation.</exception>
    Task SyncBookingStateAsync(Guid bookingId, BookingStatus newStatus, CancellationToken ct);
}

/// <summary>What one run of the import did, in counts and ids only (no name, contact, note or amount).</summary>
/// <param name="Created">Bookings made.</param>
/// <param name="Updated">Bookings changed.</param>
/// <param name="Skipped">Reservations that changed nothing: already applied, an older version, unreadable, or protected by <see cref="BookingImportOptions"/>.</param>
/// <param name="Unmapped">The reservations of a room, or of a listing, that is not the property's: not imported.</param>
/// <param name="Outcome">Whether the run read everything it had to.</param>
/// <param name="Failed">Reservations that could not be applied (Octorate failed on them) and items of the list that could not be read; the next run reads them again.</param>
public sealed record ImportResult(
    int Created,
    int Updated,
    int Skipped,
    IReadOnlyList<UnmappedReservation> Unmapped,
    ImportOutcome Outcome = ImportOutcome.Completed,
    int Failed = 0)
{
    /// <summary>The run read everything it had to.</summary>
    public bool IsComplete => Outcome == ImportOutcome.Completed;

    /// <summary>A run that did nothing, for <paramref name="outcome"/>.</summary>
    public static ImportResult Nothing(ImportOutcome outcome) => new(0, 0, 0, [], outcome);
}

/// <summary>How a run of the import ended.</summary>
public enum ImportOutcome
{
    /// <summary>Every reservation of the window was read and dealt with.</summary>
    Completed = 1,

    /// <summary>
    /// The run stopped before the end (the rate limit of the org, Octorate failing on a reservation, or an item of the list that cannot be
    /// read): what was read is saved, the listing is not marked as read, and the next run reads the window again.
    /// </summary>
    Incomplete = 2,

    /// <summary>The property has no confirmed mapping (or is not known): nothing was asked of Octorate.</summary>
    NotMapped = 3,

    /// <summary>The listing is confirmed but its room is not chosen yet, so it is not known which reservations are the property's: nothing was asked.</summary>
    RoomNotChosen = 4,
}

/// <summary>A reservation Octorate listed that is not the property's: ids only.</summary>
/// <param name="AccommodationId">The accommodation it was listed under.</param>
/// <param name="ReservationId">The Octorate reservation id.</param>
/// <param name="ProductId">The room it is on, when Octorate said.</param>
public sealed record UnmappedReservation(string AccommodationId, string ReservationId, string? ProductId);

/// <summary>What changes the way a run applies a reservation.</summary>
/// <param name="SkipChangedWithin">
/// A booking CasaZen changed within this time is left as it is (the optimistic window of the reconciliation, ADR-031-R9): the change a person
/// made a moment ago has not reached Octorate yet and must not be overwritten by an older read. <c>null</c> = no protection (the daily
/// import and the webhook handlers).
/// </param>
public sealed record BookingImportOptions(TimeSpan? SkipChangedWithin = null);
