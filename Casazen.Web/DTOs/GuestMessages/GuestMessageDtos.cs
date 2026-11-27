using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.GuestMessages;

public sealed class SendMessageRequest
{
    [Required(ErrorMessage = "GuestMessageTextInvalid")]
    [MaxLength(GuestMessageRules.TextMaxLength, ErrorMessage = "GuestMessageTextInvalid")]
    public string Text { get; set; } = string.Empty;
}

public sealed class SendPortalMessageRequest
{
    [Required(ErrorMessage = "GuestMessageTextInvalid")]
    [MaxLength(GuestMessageRules.TextMaxLength, ErrorMessage = "GuestMessageTextInvalid")]
    public string Text { get; init; } = string.Empty;
    [MaxLength(2)]
    public string? Language { get; init; }
    public bool RequestHost { get; init; }
}

public sealed class GuestMessageDto
{
    public Guid Id { get; init; }
    public GuestMessageSender Sender { get; init; }
    public MessageOrigin? Origin { get; init; }
    public string Text { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }

    public static GuestMessageDto From(GuestThreadMessage message) => new()
    {
        Id = message.Id,
        Sender = message.Sender,
        Origin = message.Origin,
        Text = message.Text,
        CreatedAt = message.CreatedAt,
    };
}

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

public sealed class HostUnreadThreadsDto
{
    public IReadOnlyList<HostUnreadThreadDto> Items { get; init; } = [];

    public static HostUnreadThreadsDto From(IReadOnlyList<HostUnreadThread> threads) => new()
    {
        Items = threads.Select(t => new HostUnreadThreadDto(t.BookingId, t.UnreadCount)).ToList(),
    };
}

public sealed record HostUnreadThreadDto(Guid BookingId, int UnreadCount);
