using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One message of a routine planned for one booking (E05/F07/US01/T01, NF-71, MSG-11): one row for each booking, routine and action of the routine that is a
/// <c>MessageToGuest</c>. It holds the time the message goes out, where it stands, the edit of the host for this booking only, why it was held back or skipped, when it went
/// out and the «To do» it raised, so the booking can show its timeline and the host can edit, skip or send each message.
/// </summary>
/// <remarks>
/// <para><b>Once.</b> The unique index on (<see cref="BookingId"/>, <see cref="RoutineId"/>, <see cref="ActionIndex"/>) keeps planning twice from writing two rows; the row
/// is the unit the three ways to send (the routine that fires, the sweep, «Send now») agree on, and its <see cref="Version"/> (<c>xmin</c>) lets only one of them win. A routine
/// that is deleted keeps the history (<see cref="RoutineId"/> becomes <c>null</c>, <see cref="RoutineName"/> stays).</para>
/// <para><b>Personal data, encrypted.</b> <see cref="TextOverride"/> is a text written for a guest and can say anything: it is encrypted at rest with the purpose of the words of
/// the conversations (<c>EncryptedColumns</c>, <c>Casazen.Message.Content</c>), never logged and never put in an event. The row holds nothing else of the guest: ids, a time,
/// a state and stable codes.</para>
/// <para><b>Nothing by default.</b> A row exists only because a person activated a routine that holds a message; an organization with no active routine has none.</para>
/// </remarks>
[Table("ScheduledMessages")]
public class ScheduledMessage : ITenantOwned
{
    public const int RoutineNameMaxLength = 200;

    public const int TemplateRefMaxLength = 100;

    public const int ReasonMaxLength = 64;

    public const int UserIdMaxLength = 255;

    public const int SentThreadKeyMaxLength = 80;

    /// <summary>Name of the unique index on (<see cref="BookingId"/>, <see cref="RoutineId"/>, <see cref="ActionIndex"/>).</summary>
    public const string PlanIndexName = "UIX_ScheduledMessages_Booking_Routine_Action";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant: the org of the booking. Never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The property of the booking: the reach of a collaborator «Solo alcuni» is decided on it.</summary>
    public Guid PropertyId { get; set; }

    /// <summary>The booking the message is for. FK, CASCADE.</summary>
    public Guid BookingId { get; set; }

    /// <summary>The routine that holds the message. FK, SET NULL: a deleted routine keeps the history of what it planned.</summary>
    public Guid? RoutineId { get; set; }

    /// <summary>The name the routine had when the row was planned (a snapshot for the screen).</summary>
    [Required]
    [MaxLength(RoutineNameMaxLength)]
    public string RoutineName { get; set; } = string.Empty;

    /// <summary>The position of the <c>MessageToGuest</c> action in the <c>ActionsJson</c> of the routine.</summary>
    public int ActionIndex { get; set; }

    /// <summary>The template the action names: the id of a message template, or its name.</summary>
    [Required]
    [MaxLength(TemplateRefMaxLength)]
    public string TemplateRef { get; set; } = string.Empty;

    /// <summary>When the message goes out (UTC).</summary>
    public DateTime PlannedAtUtc { get; set; }

    public ScheduledMessageStatus Status { get; set; } = ScheduledMessageStatus.Scheduled;

    /// <summary>
    /// The host's edit of the text for this booking only, <b>encrypted at rest</b> (purpose <c>Casazen.Message.Content</c>; the column holds the payload only, so it has no
    /// length); <c>null</c> = the routine's template. It can carry the variables of a template, which are filled when the message is sent.
    /// </summary>
    public string? TextOverride { get; set; }

    /// <summary>Why the message is held back or the last send failed: <c>variable_missing:{name}</c>, <c>id_not_verified</c>, a code of the channel; <c>null</c> otherwise.</summary>
    [MaxLength(ReasonMaxLength)]
    public string? BlockedReason { get; set; }

    /// <summary>Why a message that did not go out ended: one of <see cref="ScheduledMessageReasons"/>; <c>null</c> while it did not.</summary>
    [MaxLength(ReasonMaxLength)]
    public string? SkipReason { get; set; }

    /// <summary>The person who skipped the message; <c>null</c> for a skip of the system.</summary>
    [MaxLength(UserIdMaxLength)]
    public string? SkippedByUserId { get; set; }

    public DateTime? SkippedAt { get; set; }

    public DateTime? SentAt { get; set; }

    /// <summary>The conversation the message went to (<c>ota:{id}</c> or <c>direct:{id}</c>); <c>null</c> for an e-mail and until it is sent.</summary>
    [MaxLength(SentThreadKeyMaxLength)]
    public string? SentThreadKey { get; set; }

    /// <summary>The message went out when the clock of the property was outside the send window (the host chose that offset): the hosts were told.</summary>
    public bool OutsideHours { get; set; }

    /// <summary>The «To do» raised for a blocked message; <c>null</c> when there is none. One «To do» is shared by the blocked messages of a booking.</summary>
    public Guid? ActivityId { get; set; }

    /// <summary>The last time a send was tried (or claimed), UTC: the retries keep their distance from it, and a send under way is not started a second time.</summary>
    public DateTime? LastCheckedAt { get; set; }

    /// <summary>How many times a send was tried.</summary>
    public int Attempts { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optimistic concurrency token: mapped to PostgreSQL's <c>xmin</c> system column, which changes with every update of the row (no column of its own), as for
    /// <see cref="ServiceRequest.Version"/>: two actors on one row cannot both win.
    /// </summary>
    public uint Version { get; set; }

    [ForeignKey(nameof(BookingId))]
    public Booking Booking { get; set; } = null!;

    [ForeignKey(nameof(PropertyId))]
    public Property Property { get; set; } = null!;

    /// <summary>The message is still to be decided: planned, held back or failed (the sweep tries them again).</summary>
    public bool IsOpen => Status is ScheduledMessageStatus.Scheduled or ScheduledMessageStatus.Blocked or ScheduledMessageStatus.Failed;
}

/// <summary>The stable codes of <see cref="ScheduledMessage.SkipReason"/> and of <see cref="ScheduledMessage.BlockedReason"/> (E05/F07/US01): codes, never a text.</summary>
public static class ScheduledMessageReasons
{
    /// <summary>A person skipped the message.</summary>
    public const string Person = "person";

    /// <summary>The rules of the channel refuse this text for this conversation (a link on a request of a portal, a channel that takes no answer).</summary>
    public const string ChannelRule = "channel_rule";

    /// <summary>The booking was cancelled (or marked as a no-show) before the message went out.</summary>
    public const string BookingCancelled = "booking_cancelled";

    /// <summary>The routine was switched off, deleted or no longer holds the message.</summary>
    public const string RoutineInactive = "routine_inactive";

    /// <summary>The booking does not (or no longer) meet the condition of the routine (its channel, the length of the stay, ...): the message is not for this stay.</summary>
    public const string ConditionNotMet = "condition_not_met";

    /// <summary>The guest cannot be reached: no conversation, a closed one, no address.</summary>
    public const string ContactUnavailable = "contact_unavailable";

    /// <summary>The listing of the conversation has no confirmed mapping: nothing is written to a channel for it.</summary>
    public const string MappingNotConfirmed = "mapping_not_confirmed";

    /// <summary>The planned time had passed for good when the message was planned or moved: it is not sent late.</summary>
    public const string Missed = "missed";

    /// <summary>It stayed blocked until the check-out.</summary>
    public const string CheckOutReached = "check_out_reached";

    /// <summary>The access code cannot be given: the guest is not verified (ADR-022-R11).</summary>
    public const string IdNotVerified = "id_not_verified";

    /// <summary>The stay is held back for the tourist tax before the access code (the organization asked for it).</summary>
    public const string AccessCodeHeld = "access_code_held";

    /// <summary>The prefix of the reason of a message that names a variable the booking has no value for: <c>variable_missing:{name}</c>.</summary>
    public const string VariableMissingPrefix = "variable_missing:";

    /// <summary>The reason of a message that waits for a variable the booking has no value for.</summary>
    public static string VariableMissing(string name) => VariableMissingPrefix + name;
}
