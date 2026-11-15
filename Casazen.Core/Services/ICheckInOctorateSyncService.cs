using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The check-in of a stay from CasaZen to the channel manager (E03/F01/US01/T03, NF-41, NF-45, NF-46): the guests of a booking sent to Octorate
/// and the three states of the stay pushed once each (NO_SHOW, and since E20/F01/US01/T01 the arrival and the departure, CHECKIN_END and
/// CHECKOUT_END, with the same row, retries and "To do"). The arrival and the departure are still queued by the one handler that reacts to them
/// (<c>OctorateBookingStatePushHandler</c> and its job), so they are never sent twice for one event; the job asks this service to send them.
/// Everything goes through <see cref="IOctorateApiClient"/>, for the org of the booking (<see cref="IOctorateOrgScope.Use"/>). Only a mirrored booking is touched: a
/// reservation id on the booking, a connected Octorate connection of the org, a confirmed mapping of the property and writes not blocked;
/// otherwise nothing is written and no row is created (absence means not mirrored, ADR-023-R4). An Octorate error never reaches the caller: it
/// is a stable code on the row, retried by the retry job with a backoff and raised as one "To do" at the last attempt.
/// </summary>
public interface ICheckInOctorateSyncService
{
    /// <summary>
    /// Sends the registered guests of the booking whose payload changed since the last send, in one call; deletes in Octorate the guests of the
    /// positions that no longer exist. A property whose sender is not the channel manager sends nothing while
    /// <c>Octorate:SendGuestsWhenSenderIsNotChannelManager</c> is off (<see cref="OctorateCheckInSendState.NotApplicable"/>,
    /// <c>sender_not_channel_manager</c>), because the sandbox ignores <c>draft</c>.
    /// </summary>
    Task<OctorateCheckInSendResult> SendGuestsAsync(Guid bookingId, CancellationToken cancellationToken);

    /// <summary>
    /// The retry job's send: the same rules and guards as <see cref="SendGuestsAsync"/>, but only the guests whose link a send already tried and
    /// did not complete (<see cref="OctorateSyncStatus.Pending"/> or <see cref="OctorateSyncStatus.Failed"/>, with attempts left) are sent again,
    /// and the deletes that failed. A guest changed or added since the host confirmed is not sent here, and no link is created: it travels with
    /// the next confirmation of the wizard, which has run its checks and warns the host when data already transmitted changes (AC-NF41-3).
    /// </summary>
    Task<OctorateCheckInSendResult> RetryGuestsAsync(Guid bookingId, CancellationToken cancellationToken);

    /// <summary>
    /// The host asked to try again now (E20/F01/US01/T02, the action <c>RetryOctorateWrite</c>): the same rules and guards as
    /// <see cref="RetryGuestsAsync"/>, but the guests whose attempts are used up are sent again too, because a person decided. Never a guest
    /// that was not tried before and never one held back for a place Octorate does not know (the host corrects its data, which sends it).
    /// </summary>
    Task<OctorateCheckInSendResult> RetryGuestsNowAsync(Guid bookingId, CancellationToken cancellationToken);

    /// <summary>
    /// Sends a state of the stay once (<see cref="OctorateStayStateOutcome.AlreadySent"/> when it was), each state with a row of its own (attempts,
    /// last error, one "To do" after the last failed attempt, closed by a later success); after <see cref="OctorateStayState.CheckInEnd"/> reads
    /// the arrival back (a failed read never fails the push). The arrival waits for the guests of the stay (<see cref="OctorateStayStateOutcome.WaitingForGuests"/>)
    /// while a write of theirs is still to complete. The callers are the booking-state push (the arrival and the departure the host registers,
    /// <c>OctorateBookingStatePushJob</c>), the NO_SHOW job and the retry job. A NO_SHOW is called only while the stay is still a no-show
    /// (<see cref="OctorateStayStateOutcome.NotApplicable"/> otherwise).
    /// </summary>
    Task<OctorateStayStateResult> SetStayStateAsync(Guid bookingId, OctorateStayState state, CancellationToken cancellationToken);

    /// <summary>Read-only: where the guests and the three states of the stay stand on Octorate, with the arrival read back. No Octorate call.</summary>
    Task<OctorateCheckInView> GetStatusAsync(Guid bookingId, CancellationToken cancellationToken);
}

/// <summary>The outcome of <see cref="ICheckInOctorateSyncService.SendGuestsAsync"/>.</summary>
public enum OctorateCheckInSendState
{
    /// <summary>The booking is not mirrored on Octorate (see the reason): nothing written, no row.</summary>
    NotMirrored = 0,

    /// <summary>The sender of the property does not send through Octorate, or the stay owes no communication (see the reason): nothing written.</summary>
    NotApplicable = 1,

    /// <summary>Every guest is already on Octorate as it is now.</summary>
    NothingToSend = 2,

    /// <summary>Every change was accepted.</summary>
    Sent = 3,

    /// <summary>Some guests were accepted, some failed (their links say why).</summary>
    PartlyFailed = 4,

    /// <summary>Every change failed.</summary>
    Failed = 5,
}

/// <summary>What happened to one guest.</summary>
public enum OctorateGuestSendOutcome
{
    Created = 0,
    Updated = 1,
    Unchanged = 2,
    Failed = 3,
}

/// <summary>One guest of a send.</summary>
/// <param name="Position">The position of the guest.</param>
/// <param name="Outcome">Created, updated, unchanged or failed.</param>
/// <param name="ErrorCode">Why it failed (<c>octorate_auth</c>, <c>octorate_rate_limited</c>, <c>octorate_conflict</c>, <c>octorate_not_found</c>,
/// <c>octorate_server</c>, <c>octorate_guest_rejected</c>); null otherwise.</param>
public sealed record OctorateGuestSendResult(int Position, OctorateGuestSendOutcome Outcome, string? ErrorCode);

/// <summary>The outcome of a send of the guests.</summary>
/// <param name="State">What happened overall.</param>
/// <param name="Reason">A stable code when nothing was sent and the state says why (<c>no_reservation</c>, <c>not_connected</c>,
/// <c>mapping_not_confirmed</c>, <c>writes_blocked</c>, <c>connector_off</c>, <c>booking_not_active</c> (a request still waiting, cancelled or a no-show),
/// <c>sender_not_applicable</c>, <c>sender_not_channel_manager</c>, <c>no_guests</c>, <c>send_in_progress</c>); null otherwise.</param>
/// <param name="Draft">The guests were sent as drafts (the sender is not the channel manager).</param>
/// <param name="Guests">One entry per guest of the stay.</param>
/// <param name="CorrectionAfterSend">A guest already sent with <c>draft = false</c> was updated with different data: the host is warned that what was
/// transmitted to the police may need a correction on the Alloggiati portal (AC-NF41-3).</param>
public sealed record OctorateCheckInSendResult(
    OctorateCheckInSendState State,
    string? Reason,
    bool Draft,
    IReadOnlyList<OctorateGuestSendResult> Guests,
    bool CorrectionAfterSend);

/// <summary>The outcome of <see cref="ICheckInOctorateSyncService.SetStayStateAsync"/>.</summary>
public enum OctorateStayStateOutcome
{
    NotMirrored = 0,
    AlreadySent = 1,
    Sent = 2,
    Failed = 3,

    /// <summary>
    /// The stay is no longer in the state the push is about (a <see cref="OctorateStayState.NoShow"/> that is retried after a late arrival cleared
    /// the mark): nothing was called and the open row of the push was closed.
    /// </summary>
    NotApplicable = 4,

    /// <summary>
    /// The arrival was not sent yet: a write of the stay's guests to Octorate is still to complete, and the arrival goes after them. Nothing was called, no
    /// attempt was used up, and the retry job asks again.
    /// </summary>
    WaitingForGuests = 5,
}

/// <summary>A push of a state of the stay: the outcome and, when it failed, why.</summary>
public sealed record OctorateStayStateResult(OctorateStayStateOutcome Outcome, string? ErrorCode);

/// <summary>Where the check-in of a stay stands on Octorate (read-only, the wizard of E03/F01/US01/T04 shows it).</summary>
/// <param name="Mirrored">False when the booking is not mirrored on Octorate (then the lists are empty).</param>
/// <param name="Guests">The guests sent, by position.</param>
/// <param name="States">The three states of the stay, sent or not.</param>
public sealed record OctorateCheckInView(
    bool Mirrored,
    IReadOnlyList<OctorateGuestLinkView> Guests,
    IReadOnlyList<OctorateStayStateView> States);

/// <summary>One guest as sent to Octorate. No personal data.</summary>
/// <param name="Position">The position of the guest.</param>
/// <param name="Status">Pending, Sent or Failed.</param>
/// <param name="SentAsDraft">Sent as a draft (the sender is not the channel manager).</param>
/// <param name="Attempts">The attempts of the payload last tried.</param>
/// <param name="ErrorCode">Why the last attempt failed; <c>null</c> after a success.</param>
/// <param name="SentAt">When Octorate last accepted the guest (UTC).</param>
/// <param name="ErrorFields">The fields to correct when <paramref name="ErrorCode"/> is <c>place_not_recognized</c> (comma separated names); <c>null</c> otherwise.</param>
/// <param name="LastAttemptAt">When the link last changed, the time of the last attempt (UTC).</param>
public sealed record OctorateGuestLinkView(
    int Position,
    OctorateSyncStatus Status,
    bool SentAsDraft,
    int Attempts,
    string? ErrorCode,
    DateTime? SentAt,
    string? ErrorFields = null,
    DateTime? LastAttemptAt = null);

/// <summary>One state of the stay; <paramref name="Status"/> null when it was never pushed.</summary>
public sealed record OctorateStayStateView(
    OctorateStayState State,
    OctorateSyncStatus? Status,
    int Attempts,
    string? ErrorCode,
    DateTime? SentAt,
    DateTime? EffectiveCheckin,
    string? ArrivalTime);
