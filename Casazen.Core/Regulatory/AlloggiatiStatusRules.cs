using Casazen.Core.Entities;
using Casazen.Core.Octorate;

namespace Casazen.Core.Regulatory;

/// <summary>
/// One guest of a stay as it stands on the channel manager (a link of <see cref="StayGuestOctorateLink"/>), for <see cref="AlloggiatiStatusRules"/>.
/// No personal data: a position, a state and codes.
/// </summary>
/// <param name="Position">The position of the guest in the stay.</param>
/// <param name="Status">Pending, Sent or Failed.</param>
/// <param name="SentAsDraft">Written as a draft (the sender was not the channel manager): not entrusted.</param>
/// <param name="ErrorCode">Why the last attempt failed; <c>null</c> after a success.</param>
/// <param name="ErrorFields">The fields to correct for a <c>place_not_recognized</c> error (comma separated names); <c>null</c> otherwise.</param>
/// <param name="Attempts">The attempts of the payload last tried.</param>
/// <param name="LastAttemptAt">When the link last changed (UTC), the time of the last attempt.</param>
public sealed record ChannelManagerGuestFact(
    int Position,
    OctorateSyncStatus Status,
    bool SentAsDraft,
    string? ErrorCode,
    string? ErrorFields,
    int Attempts,
    DateTime? LastAttemptAt);

/// <summary>Where the guests of a stay stand on the channel manager (derived from their links, never stored).</summary>
public enum ChannelManagerWriteState
{
    /// <summary>The channel manager is not the sender of the property (or the connector is off): nothing is entrusted to it.</summary>
    NotApplicable = 0,

    /// <summary>No guest is registered, or none was written yet.</summary>
    NothingWritten = 1,

    /// <summary>Some guests are written or being written, none failed, and not every one is on the channel manager for good yet.</summary>
    InProgress = 2,

    /// <summary>Every registered guest is on the channel manager, written for good: the stay is entrusted to it.</summary>
    Entrusted = 3,

    /// <summary>At least one guest could not be written: see the error of its position.</summary>
    Failed = 4,
}

/// <summary>The actions the per-stay Alloggiati status offers (stable names the clients map).</summary>
public static class AlloggiatiStayActions
{
    /// <summary>
    /// A write of a guest to the channel manager failed in a way a new attempt can mend (Octorate refused or did not answer): the host asks to try again
    /// (<c>POST api/alloggiati/{bookingId}/retry-octorate-write</c>, <c>alloggiati.submit</c>). A guest held back for a place Octorate does not know is not
    /// one: its data has to be corrected.
    /// </summary>
    public const string RetryOctorateWrite = "RetryOctorateWrite";
}

/// <summary>
/// How an Alloggiati Web status reads for the host (CO-11, decision D6). CasaZen does not transmit (no web service client), so
/// from the arrival day on every communication not sent is "to send manually", whether or not the arrival-day job
/// has already run: a booking never looks done without a receipt or the host's declaration. A stay whose sender is the channel manager reads
/// <see cref="AlloggiatiWebStatus.EntrustedToChannelManager"/> once its guests are on Octorate, or <see cref="AlloggiatiWebStatus.ChannelManagerWriteFailed"/>
/// when a guest could not be written (E20/F01/US01/T02): derived by <see cref="WithChannelManager"/>, from the same facts for the per-stay API and the
/// cockpit, never stored and never green without an archived receipt.
/// </summary>
public static class AlloggiatiStatusRules
{
    /// <summary>
    /// The statuses of a booking that owes a police communication: confirmed, checked in or checked out. A request, a hold and a cancelled
    /// booking owe none. The one definition of the Alloggiati job, the lists, the alerts and the check-in sent to the channel manager.
    /// </summary>
    public static readonly BookingStatus[] ActiveBookingStatuses =
    [
        BookingStatus.Confirmed,
        BookingStatus.CheckedIn,
        BookingStatus.CheckedOut,
    ];

    /// <summary>
    /// True when the booking owes a police communication: a status of <see cref="ActiveBookingStatuses"/> and no no-show mark (NF-45: the
    /// guests of a no-show never came, there is nobody to communicate). Queries repeat it as
    /// <c>ActiveBookingStatuses.Contains(b.Status) &amp;&amp; b.NoShowAt == null</c>.
    /// </summary>
    public static bool IsDue(Booking booking)
    {
        ArgumentNullException.ThrowIfNull(booking);
        return ActiveBookingStatuses.Contains(booking.Status) && booking.NoShowAt is null;
    }

    /// <summary>
    /// The report that stands for a booking: the one of the booking's guest, or else the latest report of the booking (the same
    /// guest slot, CO-11). The Home (SR-03b) and the summary of the Alloggiati page read the same one.
    /// </summary>
    public static AlloggiatiWebReport? SlotReport(IReadOnlyCollection<AlloggiatiWebReport> reports, Guid guestId) =>
        reports.FirstOrDefault(r => r.GuestId == guestId)
        ?? reports.OrderByDescending(r => r.UpdatedAt).FirstOrDefault();

    /// <summary>Sent with a receipt, or declared sent by the host.</summary>
    public static bool IsSent(AlloggiatiWebStatus status) =>
        status is AlloggiatiWebStatus.Inviato or AlloggiatiWebStatus.InviatoManualmente;

    /// <summary>
    /// Error or rejection, or a guest the channel manager could not take: the host must act. The state of a failed write to the channel manager
    /// (<see cref="AlloggiatiWebStatus.ChannelManagerWriteFailed"/>) is one, so the cockpit shows it as a failure to resolve.
    /// </summary>
    public static bool IsFailure(AlloggiatiWebStatus status) =>
        status is AlloggiatiWebStatus.Errore or AlloggiatiWebStatus.Rifiutato or AlloggiatiWebStatus.ChannelManagerWriteFailed;

    /// <summary>Nothing is due: the stay is a no-show (<see cref="Effective"/> derives it, no report holds it).</summary>
    public static bool IsNotDue(AlloggiatiWebStatus status) => status == AlloggiatiWebStatus.NotDue;

    /// <summary>The guests are entrusted to the channel manager (<see cref="AlloggiatiWebStatus.EntrustedToChannelManager"/>): yellow, never sent, never green.</summary>
    public static bool IsEntrusted(AlloggiatiWebStatus status) => status == AlloggiatiWebStatus.EntrustedToChannelManager;

    /// <summary>
    /// Status shown for a booking: the stored one once past <see cref="AlloggiatiWebStatus.DaInviare"/>; otherwise
    /// (no report yet, or job not run yet) <see cref="AlloggiatiWebStatus.DaInviare"/> before the arrival day and
    /// <see cref="AlloggiatiWebStatus.DaInviareManualmente"/> from the arrival day in Europe/Rome. A no-show
    /// (<paramref name="noShow"/>, NF-45) owes no communication: <see cref="AlloggiatiWebStatus.NotDue"/>, whatever the stored report says,
    /// unless the communication was sent or declared sent anyway (that stays visible).
    /// </summary>
    public static AlloggiatiWebStatus Effective(AlloggiatiWebStatus? stored, DateTime checkInDate, DateTime todayInRome, bool noShow = false)
    {
        if (noShow && !(stored is { } declared && IsSent(declared)))
            return AlloggiatiWebStatus.NotDue;

        if (stored is { } status && status != AlloggiatiWebStatus.DaInviare)
            return status;

        return AlloggiatiTerms.IsArrivalDayReached(checkInDate, todayInRome)
            ? AlloggiatiWebStatus.DaInviareManualmente
            : AlloggiatiWebStatus.DaInviare;
    }

    /// <summary>
    /// Where the guests of a stay stand on the channel manager, from its registered guests and their links (E20/F01/US01/T02): entrusted when every
    /// registered guest has a link that is <see cref="OctorateSyncStatus.Sent"/> and not a draft; failed when a guest's link is
    /// <see cref="OctorateSyncStatus.Failed"/>; in progress when some link exists; nothing written otherwise. A link of a position that is no longer a
    /// registered guest is not looked at. Pure: the caller decides whether the channel manager is the sender at all
    /// (<see cref="ChannelManagerWriteState.NotApplicable"/>).
    /// </summary>
    public static ChannelManagerWriteState ChannelManagerStateOf(
        IReadOnlyCollection<int> guestPositions,
        IReadOnlyCollection<ChannelManagerGuestFact> links)
    {
        ArgumentNullException.ThrowIfNull(guestPositions);
        ArgumentNullException.ThrowIfNull(links);
        var positions = guestPositions.Distinct().ToList();
        if (positions.Count == 0)
            return ChannelManagerWriteState.NothingWritten;

        var ofGuests = links.Where(l => positions.Contains(l.Position)).ToList();
        if (ofGuests.Any(l => l.Status == OctorateSyncStatus.Failed))
            return ChannelManagerWriteState.Failed;

        var onChannelManager = ofGuests
            .Where(l => l.Status == OctorateSyncStatus.Sent && !l.SentAsDraft)
            .Select(l => l.Position)
            .ToHashSet();
        if (positions.All(onChannelManager.Contains))
            return ChannelManagerWriteState.Entrusted;

        return ofGuests.Count > 0 ? ChannelManagerWriteState.InProgress : ChannelManagerWriteState.NothingWritten;
    }

    /// <summary>
    /// The status of a stay with the channel manager taken into account (E20/F01/US01/T02, the one truth of the per-stay API and the cockpit): a
    /// status that is still «to send» (<see cref="AlloggiatiWebStatus.DaInviare"/>, <see cref="AlloggiatiWebStatus.DaInviareManualmente"/>) becomes
    /// <see cref="AlloggiatiWebStatus.EntrustedToChannelManager"/> when every guest is on the channel manager and
    /// <see cref="AlloggiatiWebStatus.ChannelManagerWriteFailed"/> when a guest could not be written. Whatever the stay already is stays as it was: sent
    /// or declared sent, an error or a rejection of the portal, a transmission in progress, a no-show. Never green: this is a state, and green needs an
    /// archived receipt.
    /// </summary>
    public static AlloggiatiWebStatus WithChannelManager(AlloggiatiWebStatus effective, ChannelManagerWriteState state)
    {
        if (effective is not (AlloggiatiWebStatus.DaInviare or AlloggiatiWebStatus.DaInviareManualmente))
            return effective;

        return state switch
        {
            ChannelManagerWriteState.Entrusted => AlloggiatiWebStatus.EntrustedToChannelManager,
            ChannelManagerWriteState.Failed => AlloggiatiWebStatus.ChannelManagerWriteFailed,
            _ => effective,
        };
    }

    /// <summary>
    /// True when asking again can mend a write that failed: a link <see cref="OctorateSyncStatus.Failed"/> or Pending whose error is not a place
    /// Octorate does not know (that one needs the host to correct the guest). It is what the action <see cref="AlloggiatiStayActions.RetryOctorateWrite"/>
    /// is offered on.
    /// </summary>
    public static bool CanRetryChannelManagerWrite(IReadOnlyCollection<ChannelManagerGuestFact> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        return links.Any(l => (l.Status is OctorateSyncStatus.Failed or OctorateSyncStatus.Pending)
                              && l.ErrorCode != OctoratePlaceRules.PlaceNotRecognizedCode);
    }
}
