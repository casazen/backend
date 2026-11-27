using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Messages;

/// <summary>
/// The body of the edit of a scheduled message (<c>PUT api/scheduled-messages/{id}</c>): <c>Text</c>, plain text of 1 to 2000 characters, with the variables of a template; empty or missing goes back to the
/// template of the routine. It carries no validation attribute on purpose: a text that is too long is a 422 <c>message_text_invalid</c> of the service (like the composer of the hub), not a 400 of the binder.
/// </summary>
public sealed class EditScheduledMessageRequest
{
    public string? Text { get; set; }
}

/// <summary>
/// One message of the timeline of a booking (<c>GET api/bookings/{id}/scheduled-messages</c>, E05/F07/US01, NF-71): <c>PlannedAtUtc</c> with the <c>TimeZone</c> of the property to show it on its clock, the
/// routine that holds it, its <c>Status</c> (<c>Scheduled</c>, <c>Sent</c>, <c>Skipped</c>, <c>Blocked</c>, <c>Failed</c>, <c>Cancelled</c>, <c>Expired</c>) with the reason it is blocked (<c>BlockedReason</c>:
/// <c>variable_missing:{name}</c>, <c>id_not_verified</c>, a code of the channel) or skipped (<c>SkipReason</c>), whether the host <c>Edited</c> it, whether it went out <c>OutsideHours</c>, the <c>Channel</c> it will use
/// now (<c>Ota</c>, <c>Direct</c>, <c>Email</c>, or <c>None</c> with <c>ChannelReason</c>) and its <c>Text</c> as it will read (the link of the check-in and the access code stay <c>[check_in_link]</c> and
/// <c>[access_code]</c>: a preview never issues a link nor releases a code) with the <c>MissingVariables</c>. <c>CanEdit</c>, <c>CanSkip</c> and <c>CanSendNow</c> say what the host may do.
/// </summary>
public sealed class ScheduledMessageDto
{
    public Guid Id { get; init; }
    public Guid BookingId { get; init; }
    public Guid? RoutineId { get; init; }
    public string RoutineName { get; init; } = string.Empty;
    public int ActionIndex { get; init; }
    public DateTime PlannedAtUtc { get; init; }
    public string TimeZone { get; init; } = string.Empty;
    public ScheduledMessageStatus Status { get; init; }
    public string? BlockedReason { get; init; }
    public string? SkipReason { get; init; }
    public bool Edited { get; init; }
    public bool OutsideHours { get; init; }
    public DateTime? SentAt { get; init; }
    public string? SentThreadKey { get; init; }
    public MessageContactChannel Channel { get; init; }
    public string? ChannelReason { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<string> MissingVariables { get; init; } = [];
    public bool CanEdit { get; init; }
    public bool CanSkip { get; init; }
    public bool CanSendNow { get; init; }

    public static ScheduledMessageDto From(ScheduledMessageView view) => new()
    {
        Id = view.Id,
        BookingId = view.BookingId,
        RoutineId = view.RoutineId,
        RoutineName = view.RoutineName,
        ActionIndex = view.ActionIndex,
        PlannedAtUtc = view.PlannedAtUtc,
        TimeZone = view.TimeZone,
        Status = view.Status,
        BlockedReason = view.BlockedReason,
        SkipReason = view.SkipReason,
        Edited = view.Edited,
        OutsideHours = view.OutsideHours,
        SentAt = view.SentAt,
        SentThreadKey = view.SentThreadKey,
        Channel = view.Channel,
        ChannelReason = view.ChannelReason,
        Text = view.Text,
        MissingVariables = view.MissingVariables,
        CanEdit = view.CanEdit,
        CanSkip = view.CanSkip,
        CanSendNow = view.CanSendNow,
    };
}
