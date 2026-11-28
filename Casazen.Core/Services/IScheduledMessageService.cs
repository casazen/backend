using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;

namespace Casazen.Core.Services;

/// <summary>
/// The scheduled messages of the routines (E05/F07/US01, NF-71, MSG-11): the plan of what each booking will be sent, its timeline for the host with «Edit», «Skip» and «Send now», and the
/// one door the routine engine uses to send a message of a routine. Everything is behind <see cref="Features.FeatureFlags.ScheduledMessages"/>: with it off nothing is planned and nothing
/// is sent through the plan.
/// </summary>
/// <remarks>
/// <para><b>Who may do what.</b> Every method that answers a caller takes the <see cref="HostScope"/> of the caller and applies it in SQL to the row and to its booking (<c>InScope</c>):
/// a row of another org, or out of the reach of a collaborator «Solo alcuni», is a <see cref="Exceptions.NotFoundException"/> (404), never a 403. The permission to read or write the
/// booking is decided by the controller; this service never checks a role. The planning and the engine have no caller: they work in the org of the booking or of the routine they name.</para>
/// <para><b>Once.</b> The row of a booking, a routine and an action is unique; planning twice writes one. A message is sent once whichever of the routine that fires, the sweep and «Send now»
/// reaches it first: the row is claimed before the send and saved with its <c>xmin</c>, and the message layer keeps the key of the row.</para>
/// <para><b>Personal data.</b> A preview carries the text of the message but never the link of the check-in or the code of the lock; nothing of the guest is logged.</para>
/// </remarks>
public interface IScheduledMessageService
{
    /// <summary>
    /// Writes the rows of a booking from the active routines of its organization (<c>ScheduledMessagePlanner</c>) whose condition the booking meets (the same condition, read by the same evaluator, as
    /// the engine uses when the routine fires): a row that was sent, skipped or edited is never overwritten, a planned or blocked one moves with the dates of the booking, one whose routine is
    /// inactive, deleted or no longer has the action becomes <c>Cancelled</c> (<c>routine_inactive</c>), one whose booking no longer meets the condition becomes <c>Cancelled</c>
    /// (<c>condition_not_met</c>), one that would have to be planned for a moment that already passed is not written (or, when it exists, becomes <c>Expired</c>), and an organization with no
    /// active routine gets nothing. Needs both <see cref="Features.FeatureFlags.Routines"/> and <see cref="Features.FeatureFlags.ScheduledMessages"/>. Idempotent. Returns how many rows were written or changed.
    /// </summary>
    Task<int> PlanBookingAsync(Guid bookingId, CancellationToken ct = default);

    /// <summary>The open rows of a cancelled booking become <c>Cancelled</c> (<paramref name="reason"/>, a code of <c>ScheduledMessageReasons</c>) and their «To do» is closed. Idempotent.</summary>
    Task<int> CancelForBookingAsync(Guid bookingId, string reason, CancellationToken ct = default);

    Task<int> ReplanOrgAsync(Guid orgId, CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> OrgsWithMessageRoutinesAsync(CancellationToken ct = default);

    /// <summary>
    /// The timeline of a booking in time order: each message with its planned time and the zone of the property, the routine, the state, the reason it is blocked or skipped, whether the host
    /// edited it, the channel it will use now (<see cref="IMessageReplyService.ChooseChannelAsync"/>, no side effect) and the text as it will read, with the variables the booking has no
    /// value for listed (the link of the check-in, the code of the lock and the link of the review show as <c>[check_in_link]</c>, <c>[access_code]</c> and <c>[review_link]</c>: a preview never
    /// issues a link nor releases a code). 404 <c>booking_not_found</c> for a booking out of the reach of <paramref name="scope"/>.
    /// </summary>
    Task<IReadOnlyList<ScheduledMessageView>> ListForBookingAsync(HostScope scope, Guid bookingId, CancellationToken ct = default);

    Task<Guid?> FindBookingIdAsync(HostScope scope, Guid id, CancellationToken ct = default);

    Task<ScheduledMessageView> EditAsync(HostScope scope, Guid id, string? text, string userId, CancellationToken ct = default);

    Task<ScheduledMessageView> SkipAsync(HostScope scope, Guid id, string userId, CancellationToken ct = default);

    Task<ScheduledMessageView> SendNowAsync(HostScope scope, Guid id, string userId, CancellationToken ct = default);

    Task<RoutineActionResult> OnRoutineActionAsync(Guid routineId, int actionIndex, Guid bookingId, CancellationToken ct = default);
}

/// <summary>One message of the timeline of a booking (<see cref="IScheduledMessageService.ListForBookingAsync"/>).</summary>
/// <param name="PlannedAtUtc">When it goes out (UTC); <paramref name="TimeZone"/> is the zone of the property, to show it on its clock.</param>
/// <param name="Status">Where it stands.</param>
/// <param name="BlockedReason">Why it is held back, or why the last send failed: <c>variable_missing:{name}</c>, <c>id_not_verified</c>, a code of the channel.</param>
/// <param name="SkipReason">Why a message that did not go out ended.</param>
/// <param name="Edited">The host edited the text for this booking.</param>
/// <param name="Channel">How the guest is reached now (<c>None</c> with <paramref name="ChannelReason"/> when there is no way).</param>
/// <param name="Text">The text as it will read, with the check-in link, the access code and the review link as <c>[check_in_link]</c>, <c>[access_code]</c> and <c>[review_link]</c>; <c>null</c> for a message that was sent (the conversation holds it) or whose template is gone.</param>
/// <param name="MissingVariables">The variables of the text the booking has no value for.</param>
public sealed record ScheduledMessageView(
    Guid Id,
    Guid BookingId,
    Guid? RoutineId,
    string RoutineName,
    int ActionIndex,
    DateTime PlannedAtUtc,
    string TimeZone,
    ScheduledMessageStatus Status,
    string? BlockedReason,
    string? SkipReason,
    bool Edited,
    bool OutsideHours,
    DateTime? SentAt,
    string? SentThreadKey,
    MessageContactChannel Channel,
    string? ChannelReason,
    string? Text,
    IReadOnlyList<string> MissingVariables,
    bool CanEdit,
    bool CanSkip,
    bool CanSendNow);

public static class ScheduledMessageErrors
{
    public const string NotFound = "scheduled_message_not_found";

    public const string NotEditable = "scheduled_message_not_editable";

    public const string NotSendable = "scheduled_message_not_sendable";

    public const string Blocked = "scheduled_message_blocked";

    public const string InProgress = "scheduled_message_in_progress";

    public const string Changed = "scheduled_message_changed";

    public const string NotFoundMessageKey = "ScheduledMessageNotFound";

    public const string NotEditableMessageKey = "ScheduledMessageNotEditable";

    public const string NotSendableMessageKey = "ScheduledMessageNotSendable";

    public const string BlockedMessageKey = "ScheduledMessageBlocked";

    public const string InProgressMessageKey = "ScheduledMessageInProgress";

    public const string ChangedMessageKey = "ScheduledMessageChanged";
}

public enum ScheduledMessageDispatchOutcome
{
    Sent = 1,

    NotDue = 2,

    Blocked = 3,

    Skipped = 4,

    Cancelled = 5,

    Failed = 6,

    NothingToDo = 7,

    InProgress = 8,
}

public enum ScheduledMessageDispatchMode
{
    /// <summary>The routine fired: the message goes out when its planned time has come, and waits for it otherwise.</summary>
    Routine = 1,

    Sweep = 2,

    SendNow = 3,
}

public sealed record ScheduledMessageDispatchResult(ScheduledMessageDispatchOutcome Outcome, ScheduledMessageStatus Status, string? Code);

/// <summary>
/// The one place that sends a scheduled message (E05/F07/US01/T03, NF-71, ADR-022-R11): decides what the text is, fills its variables, holds the message back when something is missing, writes it
/// through the message layer with the key of the row, and keeps the row, its «To do» and the alert to the hosts in step. It never throws for what a channel answers: the answer is the state of the row.
/// An unexpected failure (the database, a call that is not an answer of the channel) is recorded on the row (<c>Failed</c> with a code) and thrown, so the caller that can try again does.
/// </summary>
public interface IScheduledMessageDispatcher
{
    Task<ScheduledMessageDispatchResult> TryDispatchAsync(Guid id, DateTime nowUtc, ScheduledMessageDispatchMode mode, string? userId, CancellationToken ct = default);

    /// <summary>
    /// One pass of the sweep: the planned messages overdue by more than <c>CatchUpMinutes</c> are sent, the blocked and the failed ones are tried again every <c>BlockedRetryMinutes</c> (a failed
    /// one up to <c>ScheduledMessageRules.MaxSendAttempts</c> times), and one still blocked at the check-out of its booking becomes <c>Expired</c> with its «To do» closed. One row's failure never
    /// stops the others (the first is thrown when the pass is over). Returns how many rows were looked at.
    /// </summary>
    Task<int> SweepAsync(DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// The text of the open message <paramref name="id"/> as it will read, for the timeline of the booking: the edit of the host or the template in the language of the guest, the variables the booking has a
    /// value for filled in, the link of the check-in, the code of the lock and the link of the review shown as <c>[check_in_link]</c>, <c>[access_code]</c> and <c>[review_link]</c> (a preview never issues a
    /// link nor releases a code), and the variables the booking has no value for listed. Reads and writes nothing. The caller checked that the row is within its reach.
    /// </summary>
    Task<ScheduledMessagePreview> PreviewAsync(Guid id, CancellationToken ct = default);
}

public sealed record ScheduledMessagePreview(string? Text, IReadOnlyList<string> MissingVariables);

public static class ScheduledMessageRules
{
    public const int MaxSendAttempts = 4;

    public const int SweepBatchSize = 200;

    public static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The key a message goes out with (<c>Idempotency-Key</c> of the reply): the row and, after a send the channel refused (a refused message did not go out, but the same key would replay that
    /// refusal for ever), the number of the attempt. Letters, digits and dashes only, 8 to 64 characters, as <see cref="GuestMessageRules.NormalizeIdempotencyKey"/> asks (a colon would be refused).
    /// </summary>
    public static string SendKeyOf(Guid id, int attempt = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var key = $"sm-{id:N}";
        return attempt == 1 ? key : $"{key}-{attempt}";
    }

    public static string ReleaseChannelOf(MessageContactChannel channel) => channel switch
    {
        MessageContactChannel.Ota => AccessCodeReleaseChannels.PortalChat,
        MessageContactChannel.Direct => AccessCodeReleaseChannels.GuestArea,
        _ => AccessCodeReleaseChannels.Email,
    };
}
