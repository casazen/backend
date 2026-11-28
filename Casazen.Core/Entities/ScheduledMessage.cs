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

    public const string PlanIndexName = "UIX_ScheduledMessages_Booking_Routine_Action";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    public Guid BookingId { get; set; }

    public Guid? RoutineId { get; set; }

    [Required]
    [MaxLength(RoutineNameMaxLength)]
    public string RoutineName { get; set; } = string.Empty;

    public int ActionIndex { get; set; }

    [Required]
    [MaxLength(TemplateRefMaxLength)]
    public string TemplateRef { get; set; } = string.Empty;

    public DateTime PlannedAtUtc { get; set; }

    public ScheduledMessageStatus Status { get; set; } = ScheduledMessageStatus.Scheduled;

    public string? TextOverride { get; set; }

    /// <summary>Why the message is held back or the last send failed: <c>variable_missing:{name}</c>, <c>id_not_verified</c>, a code of the channel; <c>null</c> otherwise.</summary>
    [MaxLength(ReasonMaxLength)]
    public string? BlockedReason { get; set; }

    [MaxLength(ReasonMaxLength)]
    public string? SkipReason { get; set; }

    [MaxLength(UserIdMaxLength)]
    public string? SkippedByUserId { get; set; }

    public DateTime? SkippedAt { get; set; }

    public DateTime? SentAt { get; set; }

    [MaxLength(SentThreadKeyMaxLength)]
    public string? SentThreadKey { get; set; }

    public bool OutsideHours { get; set; }

    public Guid? ActivityId { get; set; }

    /// <summary>The last time a send was tried (or claimed), UTC: the retries keep their distance from it, and a send under way is not started a second time.</summary>
    public DateTime? LastCheckedAt { get; set; }

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

    public bool IsOpen => Status is ScheduledMessageStatus.Scheduled or ScheduledMessageStatus.Blocked or ScheduledMessageStatus.Failed;
}

public static class ScheduledMessageReasons
{
    public const string Person = "person";

    public const string ChannelRule = "channel_rule";

    public const string BookingCancelled = "booking_cancelled";

    public const string RoutineInactive = "routine_inactive";

    public const string ConditionNotMet = "condition_not_met";

    public const string ContactUnavailable = "contact_unavailable";

    public const string MappingNotConfirmed = "mapping_not_confirmed";

    public const string Missed = "missed";

    public const string CheckOutReached = "check_out_reached";

    public const string IdNotVerified = "id_not_verified";

    public const string AccessCodeHeld = "access_code_held";

    public const string CampaignNeedsConsent = "campaign_needs_consent";

    public const string CheckInCompleted = "check_in_completed";

    public const string CheckInWithOperator = "check_in_with_operator";

    public const string CheckInNotOpen = "check_in_not_open";

    public const string CheckInInProgress = "check_in_in_progress";

    public const string VariableMissingPrefix = "variable_missing:";

    public static string VariableMissing(string name) => VariableMissingPrefix + name;
}
