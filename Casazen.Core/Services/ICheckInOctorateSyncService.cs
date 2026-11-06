using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The check-in of a stay from CasaZen to the channel manager (E03/F01/US01/T03, NF-41, NF-45, NF-46): the guests of a booking sent to Octorate,
/// the states of the stay (arrival, departure, no-show) pushed once each, and the arrival read back. Everything goes through
/// <see cref="IOctorateApiClient"/>, for the org of the booking (<see cref="IOctorateOrgScope.Use"/>). Only a mirrored booking is touched: a
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
    /// Sends a state of the stay once (<see cref="OctorateStayStateOutcome.AlreadySent"/> when it was); after
    /// <see cref="OctorateStayState.CheckInEnd"/> reads the arrival back (a failed read never fails the push).
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

    /// <summary>The sender of the property does not send through Octorate, or the stay is cancelled or a no-show (see the reason): nothing written.</summary>
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
/// <c>mapping_not_confirmed</c>, <c>writes_blocked</c>, <c>connector_off</c>, <c>booking_not_active</c> (cancelled or no-show),
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
public sealed record OctorateGuestLinkView(
    int Position,
    OctorateSyncStatus Status,
    bool SentAsDraft,
    int Attempts,
    string? ErrorCode,
    DateTime? SentAt);

/// <summary>One state of the stay; <paramref name="Status"/> null when it was never pushed.</summary>
public sealed record OctorateStayStateView(
    OctorateStayState State,
    OctorateSyncStatus? Status,
    int Attempts,
    string? ErrorCode,
    DateTime? SentAt,
    DateTime? EffectiveCheckin,
    string? ArrivalTime);
