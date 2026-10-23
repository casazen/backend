using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.GuestMessages;

/// <summary>The body of a message, from the guest or from the host: plain text, up to 2000 characters.</summary>
public sealed class SendMessageRequest
{
    [Required(ErrorMessage = "GuestMessageTextInvalid")]
    [MaxLength(GuestMessageRules.TextMaxLength, ErrorMessage = "GuestMessageTextInvalid")]
    public string Text { get; set; } = string.Empty;
}

/// <summary>A message of the thread. <c>Sender</c> is <c>Guest</c> or <c>Host</c>; the text is plain text, to be shown as text.</summary>
public sealed class GuestMessageDto
{
    public Guid Id { get; init; }
    public GuestMessageSender Sender { get; init; }
    public string Text { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }

    public static GuestMessageDto From(GuestThreadMessage message) => new()
    {
        Id = message.Id,
        Sender = message.Sender,
        Text = message.Text,
        CreatedAt = message.CreatedAt,
    };
}

/// <summary>
/// The thread as the guest reads it. <c>State</c> is <c>Open</c> or <c>Closed</c>; <c>ClosesAt</c> is when it stops taking messages (a
/// closed thread is read-only: the guest can still read it).
/// </summary>
public sealed class GuestThreadDto
{
    public GuestThreadState State { get; init; }
    public DateTime ClosesAt { get; init; }
    public IReadOnlyList<GuestMessageDto> Messages { get; init; } = [];

    public static GuestThreadDto From(GuestThreadView thread) => new()
    {
        State = thread.State,
        ClosesAt = thread.ClosesAt,
        Messages = thread.Messages.Select(GuestMessageDto.From).ToList(),
    };
}

/// <summary>
/// The answer to a send: the message as stored, <c>Redacted</c> when a card number or an IBAN of the guest's text was replaced by
/// «[rimosso]», <c>Replayed</c> when the same <c>Idempotency-Key</c> had already written it (nothing new was written).
/// </summary>
public sealed class SentGuestMessageDto
{
    public GuestMessageDto Message { get; init; } = null!;
    public bool Redacted { get; init; }
    public bool Replayed { get; init; }

    public static SentGuestMessageDto From(SentGuestMessage sent) => new()
    {
        Message = GuestMessageDto.From(sent.Message),
        Redacted = sent.Redacted,
        Replayed = sent.Replayed,
    };
}

/// <summary>
/// The thread of a booking as its host reads it. <c>State</c> is <c>Open</c>, <c>Closed</c> or <c>Unavailable</c> (not a direct booking,
/// or cancelled: there is no thread and the screen shows no Messaggi); <c>UnreadCount</c> counts the guest's messages the host has not
/// read; <c>NeedsReply</c> is true when the thread is open and the last message is the guest's.
/// </summary>
public sealed class HostThreadDto
{
    public GuestThreadState State { get; init; }
    public DateTime? ClosesAt { get; init; }
    public int UnreadCount { get; init; }
    public bool NeedsReply { get; init; }
    public IReadOnlyList<GuestMessageDto> Messages { get; init; } = [];

    public static HostThreadDto From(HostThreadView thread) => new()
    {
        State = thread.State,
        ClosesAt = thread.ClosesAt,
        UnreadCount = thread.UnreadCount,
        NeedsReply = thread.NeedsReply,
        Messages = thread.Messages.Select(GuestMessageDto.From).ToList(),
    };
}

/// <summary>
/// One thread in the host's list (<c>GET api/messages</c>). <c>LastGuestMessagePreview</c> is the start of the guest's last message (plain
/// text, for the host only); <c>NeedsReply</c> is the thread of the «Da rispondere» list.
/// </summary>
public sealed class HostThreadSummaryDto
{
    public Guid BookingId { get; init; }
    public string BookingCode { get; init; } = string.Empty;
    public Guid PropertyId { get; init; }
    public string PropertyName { get; init; } = string.Empty;
    public string GuestName { get; init; } = string.Empty;
    public DateOnly CheckInDate { get; init; }
    public DateOnly CheckOutDate { get; init; }
    public GuestThreadState State { get; init; }
    public DateTime LastMessageAt { get; init; }
    public GuestMessageSender? LastMessageSender { get; init; }
    public string? LastGuestMessagePreview { get; init; }
    public int UnreadCount { get; init; }
    public bool NeedsReply { get; init; }

    public static HostThreadSummaryDto From(HostThreadSummary thread) => new()
    {
        BookingId = thread.BookingId,
        BookingCode = thread.BookingCode,
        PropertyId = thread.PropertyId,
        PropertyName = thread.PropertyName,
        GuestName = thread.GuestName,
        CheckInDate = Casazen.Core.Utilities.RomeCalendar.DateInRome(thread.CheckInDate),
        CheckOutDate = Casazen.Core.Utilities.RomeCalendar.DateInRome(thread.CheckOutDate),
        State = thread.State,
        LastMessageAt = thread.LastMessageAt,
        LastMessageSender = thread.LastMessageSender,
        LastGuestMessagePreview = thread.LastGuestMessagePreview,
        UnreadCount = thread.UnreadCount,
        NeedsReply = thread.NeedsReply,
    };
}
