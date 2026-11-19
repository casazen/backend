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

    /// <summary>Plans every booking of the organization whose check-in or check-out is within <c>PlanHorizonDays</c>. Returns how many rows were written or changed.</summary>
    Task<int> ReplanOrgAsync(Guid orgId, CancellationToken ct = default);

    /// <summary>The organizations that have an active routine with a <c>MessageToGuest</c> action: the ones the hourly plan looks at.</summary>
    Task<IReadOnlyList<Guid>> OrgsWithMessageRoutinesAsync(CancellationToken ct = default);

    /// <summary>
    /// The timeline of a booking in time order: each message with its planned time and the zone of the property, the routine, the state, the reason it is blocked or skipped, whether the host
    /// edited it, the channel it will use now (<see cref="IMessageReplyService.ChooseChannelAsync"/>, no side effect) and the text as it will read, with the variables the booking has no
    /// value for listed (the link of the check-in, the code of the lock and the link of the review show as <c>[check_in_link]</c>, <c>[access_code]</c> and <c>[review_link]</c>: a preview never
    /// issues a link nor releases a code). 404 <c>booking_not_found</c> for a booking out of the reach of <paramref name="scope"/>.
    /// </summary>
    Task<IReadOnlyList<ScheduledMessageView>> ListForBookingAsync(HostScope scope, Guid bookingId, CancellationToken ct = default);

    /// <summary>
    /// The booking of a row in the reach of <paramref name="scope"/>; <c>null</c> when the row is not there, is of another org or is out of the reach: the controller then answers 404 before it
    /// asks the permission on the booking.
    /// </summary>
    Task<Guid?> FindBookingIdAsync(HostScope scope, Guid id, CancellationToken ct = default);

    /// <summary>
    /// Sets the text of one message for this booking only, or clears it (an empty text goes back to the template of the routine). Only a planned, blocked or failed message can be edited
    /// (409 <c>scheduled_message_not_editable</c>); a text that is too long or empty after the edit of a text is 422 <c>message_text_invalid</c>, a name that is not a variable 422
    /// <c>template_variable_unknown</c>, as the composer of the hub. 404 for a row out of the reach. Two people on one row: the one that loses gets 409 <c>scheduled_message_changed</c>.
    /// </summary>
    Task<ScheduledMessageView> EditAsync(HostScope scope, Guid id, string? text, string userId, CancellationToken ct = default);

    /// <summary>
    /// «Skip»: a planned, blocked or failed message becomes <c>Skipped</c> (<c>person</c>), its «To do» is closed and the other people who reach the property are told (not the one who
    /// skipped). 409 <c>scheduled_message_not_editable</c> for a message that already ended; 404 for a row out of the reach.
    /// </summary>
    Task<ScheduledMessageView> SkipAsync(HostScope scope, Guid id, string userId, CancellationToken ct = default);

    /// <summary>
    /// «Send now»: sends the message at once, whatever its planned time and the send window. 409 <c>scheduled_message_not_sendable</c> for a sent, skipped, cancelled or expired message;
    /// 422 <c>scheduled_message_blocked</c> with the missing items (the extension <c>missing</c>) for one that is still blocked; 409 <c>scheduled_message_in_progress</c> when another actor is
    /// sending it; 404 for a row out of the reach. A message the guest cannot be written to is skipped by the system, as when it is sent by the plan.
    /// </summary>
    Task<ScheduledMessageView> SendNowAsync(HostScope scope, Guid id, string userId, CancellationToken ct = default);

    /// <summary>
    /// The routine engine's door (action <c>MessageToGuest</c>, the routine firing IS the moment): gets or creates the row of (booking, routine, action) (a <c>Sent</c> row after the fact for a routine
    /// on an event the plan cannot foresee), does nothing when it is skipped, sent, cancelled or expired, and sends it when its planned time has come. <c>done</c> when it went out, was
    /// waiting for its time or was already handled; <c>blocked</c> with the code when it is held back; <c>failed</c> with the code when the channel did not take it. The guardrails of the
    /// engine (ADR-022-R11) have already run.
    /// </summary>
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

/// <summary>The stable codes and the message keys (<c>SharedResources</c>, Italian and English) of the refusals of <see cref="IScheduledMessageService"/>.</summary>
public static class ScheduledMessageErrors
{
    /// <summary>404: the message is not there, is of another org or is out of the reach of the caller.</summary>
    public const string NotFound = "scheduled_message_not_found";

    /// <summary>409: the message already ended (sent, skipped, cancelled or expired): it cannot be edited or skipped.</summary>
    public const string NotEditable = "scheduled_message_not_editable";

    /// <summary>409: the message already ended: it cannot be sent now.</summary>
    public const string NotSendable = "scheduled_message_not_sendable";

    /// <summary>422: the message is still blocked: the extension <c>missing</c> names what it waits for.</summary>
    public const string Blocked = "scheduled_message_blocked";

    /// <summary>409: another actor is sending the message at this moment (or just did): nothing was sent twice.</summary>
    public const string InProgress = "scheduled_message_in_progress";

    /// <summary>409: somebody else changed the message while the caller was changing it (the <c>xmin</c> check): read it again.</summary>
    public const string Changed = "scheduled_message_changed";

    /// <summary>The message key of <see cref="NotFound"/>.</summary>
    public const string NotFoundMessageKey = "ScheduledMessageNotFound";

    public const string NotEditableMessageKey = "ScheduledMessageNotEditable";

    public const string NotSendableMessageKey = "ScheduledMessageNotSendable";

    public const string BlockedMessageKey = "ScheduledMessageBlocked";

    public const string InProgressMessageKey = "ScheduledMessageInProgress";

    public const string ChangedMessageKey = "ScheduledMessageChanged";
}

/// <summary>What one try to send a message did (<see cref="IScheduledMessageDispatcher.TryDispatchAsync"/>).</summary>
public enum ScheduledMessageDispatchOutcome
{
    /// <summary>The message went out (or the message layer holds it and tells the channel).</summary>
    Sent = 1,

    /// <summary>Its time has not come: the row stays planned and the sweep sends it.</summary>
    NotDue = 2,

    /// <summary>A piece of data is missing: the row is blocked, with a «To do».</summary>
    Blocked = 3,

    /// <summary>The system skipped it: the rules of the channel, no way to the guest, a mapping nobody decided.</summary>
    Skipped = 4,

    /// <summary>The booking was cancelled, or the routine does not hold the message any more: the row is cancelled.</summary>
    Cancelled = 5,

    /// <summary>The channel did not take the message (or could not be reached): the row failed and is tried again.</summary>
    Failed = 6,

    /// <summary>The row already ended, or it was changed under the actor: nothing was sent.</summary>
    NothingToDo = 7,

    /// <summary>Another actor holds the row at this moment: nothing was sent twice.</summary>
    InProgress = 8,
}

/// <summary>Why a message is being sent now (<see cref="IScheduledMessageDispatcher.TryDispatchAsync"/>).</summary>
public enum ScheduledMessageDispatchMode
{
    /// <summary>The routine fired: the message goes out when its planned time has come, and waits for it otherwise.</summary>
    Routine = 1,

    /// <summary>The sweep: a message overdue, a blocked one or a failed one tried again.</summary>
    Sweep = 2,

    /// <summary>The host asked for «Send now»: whatever the time and the send window.</summary>
    SendNow = 3,
}

/// <summary>The answer of a try to send: what happened, where the row stands now and the stable code of the reason when there is one.</summary>
public sealed record ScheduledMessageDispatchResult(ScheduledMessageDispatchOutcome Outcome, ScheduledMessageStatus Status, string? Code);

/// <summary>
/// The one place that sends a scheduled message (E05/F07/US01/T03, NF-71, ADR-022-R11): decides what the text is, fills its variables, holds the message back when something is missing, writes it
/// through the message layer with the key of the row, and keeps the row, its «To do» and the alert to the hosts in step. It never throws for what a channel answers: the answer is the state of the row.
/// An unexpected failure (the database, a call that is not an answer of the channel) is recorded on the row (<c>Failed</c> with a code) and thrown, so the caller that can try again does.
/// </summary>
public interface IScheduledMessageDispatcher
{
    /// <summary>
    /// Tries to send the row <paramref name="id"/> now: claims it (a second actor at the same moment does nothing), checks the booking and the routine, fills the text, and sends it.
    /// <paramref name="userId"/> is the person who asked for «Send now» (not told about it), <c>null</c> for the routine and the sweep.
    /// </summary>
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

/// <summary>The text of a scheduled message as it will read (<see cref="IScheduledMessageDispatcher.PreviewAsync"/>): <c>null</c> when there is none (the message ended, or its template is gone).</summary>
public sealed record ScheduledMessagePreview(string? Text, IReadOnlyList<string> MissingVariables);

/// <summary>The rules of the scheduled messages that need no database, in one place (E05/F07/US01).</summary>
public static class ScheduledMessageRules
{
    /// <summary>How many times a failed message is tried again by the sweep, the first send included; after that it waits for the host («Send now»).</summary>
    public const int MaxSendAttempts = 4;

    /// <summary>The most rows one pass of the sweep looks at.</summary>
    public const int SweepBatchSize = 200;

    /// <summary>How long a claim of a row holds other actors off: the message layer takes a send that did not finish for one whose outcome nobody knows after two minutes.</summary>
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

    /// <summary>The access code is released through the channel the message will use.</summary>
    public static string ReleaseChannelOf(MessageContactChannel channel) => channel switch
    {
        MessageContactChannel.Ota => AccessCodeReleaseChannels.PortalChat,
        MessageContactChannel.Direct => AccessCodeReleaseChannels.GuestArea,
        _ => AccessCodeReleaseChannels.Email,
    };
}
