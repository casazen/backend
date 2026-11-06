using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Web.DTOs.Messages;

/// <summary>
/// One conversation in the list of the hub (<c>GET api/messages?includeOta=true</c>, E05/F02/US01/T01). <c>ThreadKey</c> is the opaque key every
/// other endpoint of the hub takes (<c>ota:{id}</c> or <c>direct:{bookingId}</c>); <c>Source</c> is <c>Direct</c> or <c>Ota</c>, <c>Channel</c> is
/// <c>Direct</c>, <c>Airbnb</c>, <c>Booking</c>, <c>Vrbo</c> or <c>Expedia</c>, <c>Kind</c> is <c>Reservation</c> or <c>Request</c> (no booking yet),
/// <c>State</c> is <c>Open</c>, <c>Closed</c> or <c>Archived</c>. A conversation of a channel whose listing has no confirmed mapping has no property:
/// <c>Unassigned</c> is true and only a caller who reaches every property sees it. <c>Preview</c> is the start of the last message (plain text, for
/// the host only, <c>null</c> for a message that is only a file); <c>LastMessageOrigin</c> is <c>Guest</c>, <c>Person</c> or <c>Assistant</c>.
/// <c>NeedsReply</c> is the conversation of the «Da rispondere» list: open, with the guest's message last.
/// </summary>
public sealed class MessageHubThreadDto
{
    public string ThreadKey { get; init; } = string.Empty;
    public MessageThreadSource Source { get; init; }
    public MessageHubChannel Channel { get; init; }
    public MessageHubThreadKind Kind { get; init; }
    public MessageHubThreadState State { get; init; }
    public Guid? PropertyId { get; init; }
    public string? PropertyName { get; init; }
    public bool Unassigned { get; init; }
    public Guid? BookingId { get; init; }
    public string? BookingCode { get; init; }
    public string? GuestName { get; init; }
    public DateOnly? CheckInDate { get; init; }
    public DateOnly? CheckOutDate { get; init; }
    public DateTime LastMessageAt { get; init; }
    public MessageOrigin? LastMessageOrigin { get; init; }
    public string? Preview { get; init; }
    public int UnreadCount { get; init; }
    public bool NeedsReply { get; init; }

    public static MessageHubThreadDto From(MessageHubThreadSummary thread) => new()
    {
        ThreadKey = thread.Key.ToString(),
        Source = thread.Source,
        Channel = thread.Channel,
        Kind = thread.Kind,
        State = thread.State,
        PropertyId = thread.PropertyId,
        PropertyName = thread.PropertyName,
        Unassigned = thread.Unassigned,
        BookingId = thread.BookingId,
        BookingCode = thread.BookingCode,
        GuestName = thread.GuestName,
        CheckInDate = thread.CheckInDate is { } checkIn ? RomeCalendar.DateInRome(checkIn) : null,
        CheckOutDate = thread.CheckOutDate is { } checkOut ? RomeCalendar.DateInRome(checkOut) : null,
        LastMessageAt = thread.LastMessageAt,
        LastMessageOrigin = thread.LastMessageOrigin,
        Preview = thread.Preview,
        UnreadCount = thread.UnreadCount,
        NeedsReply = thread.NeedsReply,
    };
}

/// <summary>
/// The conversations with something unread (<c>GET api/messages/unread?includeOta=true</c>): one item per conversation with its key, the booking
/// it is about when there is one and how many messages of the guest the host has not read. Counters only: no name, no text.
/// </summary>
public sealed class MessageHubUnreadDto
{
    public IReadOnlyList<MessageHubUnreadItemDto> Items { get; init; } = [];

    public static MessageHubUnreadDto From(IReadOnlyList<MessageHubUnreadThread> threads) => new()
    {
        Items = threads.Select(t => new MessageHubUnreadItemDto(t.Key.ToString(), t.BookingId, t.UnreadCount)).ToList(),
    };
}

public sealed record MessageHubUnreadItemDto(string ThreadKey, Guid? BookingId, int UnreadCount);

/// <summary>What is still unread in a conversation after the host marked it read (<c>POST api/messages/threads/{threadKey}/read</c>).</summary>
public sealed class MessageReadDto
{
    public string ThreadKey { get; init; } = string.Empty;
    public int UnreadCount { get; init; }

    public static MessageReadDto From(MessageHubReadResult result) => new()
    {
        ThreadKey = result.Key.ToString(),
        UnreadCount = result.UnreadCount,
    };
}

/// <summary>The file of a message: its type only. The client opens it with <c>GET api/messages/{messageId}/attachment</c>; the address on the channel is never sent.</summary>
public sealed class MessageAttachmentDto
{
    public string ContentType { get; init; } = string.Empty;
}

/// <summary>
/// A message of a conversation. <c>Direction</c> is <c>Inbound</c> (the guest) or <c>Outbound</c> (the host side); <c>Origin</c> is <c>Guest</c>,
/// <c>Person</c> or <c>Assistant</c>, and <c>RuleCode</c> the situation an assistant message answered. <c>DeliveryState</c> is where an outbound message
/// of a channel is on its way: <c>Created</c>, <c>WaitingAuthorization</c>, <c>Sent</c>, <c>Error</c> or <c>ManualVerify</c> (a person checks on the
/// channel before it is sent again); it is absent on the others. <c>Text</c> is plain text, to be shown as text.
/// </summary>
public sealed class MessageHubMessageDto
{
    public Guid Id { get; init; }
    public OtaMessageDirection Direction { get; init; }
    public MessageOrigin Origin { get; init; }
    public string? RuleCode { get; init; }
    public string? SenderName { get; init; }
    public string Text { get; init; } = string.Empty;
    public DateTime SentAt { get; init; }
    public DateTime? ReadAt { get; init; }
    public OtaMessageDeliveryState? DeliveryState { get; init; }
    public MessageAttachmentDto? Attachment { get; init; }

    public static MessageHubMessageDto From(MessageHubMessage message) => new()
    {
        Id = message.Id,
        Direction = message.Direction,
        Origin = message.Origin,
        RuleCode = message.RuleCode,
        SenderName = message.SenderName,
        Text = message.Text,
        SentAt = message.SentAt,
        ReadAt = message.ReadAt,
        DeliveryState = message.DeliveryState,
        Attachment = message.AttachmentContentType is { } type ? new MessageAttachmentDto { ContentType = type } : null,
    };
}

/// <summary>
/// The amounts of the booking in <c>Currency</c> of its card. A figure that is not available is left out, not zero (a channel often sends the
/// commission late, and the net and the balance only when it documents them). The whole object is absent for a caller who may not read payments.
/// </summary>
public sealed class MessageBookingAmountsDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Total { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Paid { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Commission { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Net { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Balance { get; init; }

    public static MessageBookingAmountsDto From(MessageBookingAmounts amounts) => new()
    {
        Total = amounts.Total,
        Paid = amounts.Paid,
        Commission = amounts.Commission,
        Net = amounts.Net,
        Balance = amounts.Balance,
    };
}

/// <summary>
/// The booking beside a conversation: the code as the guest reads it (<c>XXXXX-XXXXX</c>), the guest, the stay as Rome dates, how many people, the
/// state (<c>Pending</c>, <c>Confirmed</c>, <c>CheckedIn</c>, <c>CheckedOut</c>, <c>Cancelled</c>), where it came from (<c>Source</c>, with the name the
/// channel manager gives in <c>ChannelLabel</c> for a portal CasaZen has no value for) and the amounts.
/// </summary>
public sealed class MessageBookingCardDto
{
    public Guid BookingId { get; init; }
    public string BookingCode { get; init; } = string.Empty;
    public string GuestName { get; init; } = string.Empty;
    public DateOnly CheckInDate { get; init; }
    public DateOnly CheckOutDate { get; init; }
    public int Guests { get; init; }
    public int Adults { get; init; }
    public int Children { get; init; }
    public BookingStatus Status { get; init; }
    public BookingSource Source { get; init; }
    public string? ChannelLabel { get; init; }
    public string Currency { get; init; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessageBookingAmountsDto? Amounts { get; init; }

    public static MessageBookingCardDto From(MessageBookingCard card) => new()
    {
        BookingId = card.BookingId,
        BookingCode = card.BookingCode,
        GuestName = card.GuestName,
        CheckInDate = RomeCalendar.DateInRome(card.CheckInDate),
        CheckOutDate = RomeCalendar.DateInRome(card.CheckOutDate),
        Guests = card.Guests,
        Adults = card.Adults,
        Children = card.Children,
        Status = card.Status,
        Source = card.Source,
        ChannelLabel = card.ChannelLabel,
        Currency = card.Currency,
        Amounts = card.Amounts is null ? null : MessageBookingAmountsDto.From(card.Amounts),
    };
}

/// <summary>The link to the profile of the customer: the id of the guest of the booking. The route of the profile is built by the client.</summary>
public sealed class MessageCustomerDto
{
    public Guid GuestId { get; init; }
}

/// <summary>
/// What the host may do in the conversation. All false with <c>Reason</c> <c>mapping_not_confirmed</c> while the listing of a conversation of a
/// channel has no confirmed mapping (it can be read, not answered), and with <c>thread_closed</c> for a direct thread past the seventh day after the
/// check-out. <c>ContactDetailsAllowed</c> is false in an Airbnb request (no link, address or number before the booking); the attachments and the
/// automatic answer are false until the policy of each channel is confirmed.
/// </summary>
public sealed class MessageThreadActionsDto
{
    public bool CanReply { get; init; }
    public bool ContactDetailsAllowed { get; init; }
    public bool AttachmentsAllowed { get; init; }
    public bool AutoReplyAllowed { get; init; }
    public string? Reason { get; init; }

    public static MessageThreadActionsDto From(MessageThreadActions actions) => new()
    {
        CanReply = actions.CanReply,
        ContactDetailsAllowed = actions.ContactDetailsAllowed,
        AttachmentsAllowed = actions.AttachmentsAllowed,
        AutoReplyAllowed = actions.AutoReplyAllowed,
        Reason = actions.Reason,
    };
}

/// <summary>
/// One conversation with everything the screen shows beside it (<c>GET api/messages/threads/{threadKey}</c>): the latest messages oldest first
/// (<c>HasEarlierMessages</c> when there are more), the <c>Booking</c> card when the conversation is about a booking CasaZen holds, the <c>Customer</c>
/// link (absent for a request, which has no booking) and the <c>AllowedActions</c>.
/// </summary>
public sealed class MessageThreadDto
{
    public string ThreadKey { get; init; } = string.Empty;
    public MessageThreadSource Source { get; init; }
    public MessageHubChannel Channel { get; init; }
    public MessageHubThreadKind Kind { get; init; }
    public MessageHubThreadState State { get; init; }
    public Guid? PropertyId { get; init; }
    public string? PropertyName { get; init; }
    public bool Unassigned { get; init; }
    public string? GuestName { get; init; }
    public int UnreadCount { get; init; }
    public bool NeedsReply { get; init; }
    public bool Anonymized { get; init; }
    public MessageBookingCardDto? Booking { get; init; }
    public MessageCustomerDto? Customer { get; init; }
    public MessageThreadActionsDto AllowedActions { get; init; } = new();
    public IReadOnlyList<MessageHubMessageDto> Messages { get; init; } = [];
    public bool HasEarlierMessages { get; init; }

    public static MessageThreadDto From(MessageThreadView thread) => new()
    {
        ThreadKey = thread.Key.ToString(),
        Source = thread.Source,
        Channel = thread.Channel,
        Kind = thread.Kind,
        State = thread.State,
        PropertyId = thread.PropertyId,
        PropertyName = thread.PropertyName,
        Unassigned = thread.Unassigned,
        GuestName = thread.GuestName,
        UnreadCount = thread.UnreadCount,
        NeedsReply = thread.NeedsReply,
        Anonymized = thread.Anonymized,
        Booking = thread.Booking is null ? null : MessageBookingCardDto.From(thread.Booking),
        Customer = thread.CustomerGuestId is { } guestId ? new MessageCustomerDto { GuestId = guestId } : null,
        AllowedActions = MessageThreadActionsDto.From(thread.Actions),
        Messages = thread.Messages.Select(MessageHubMessageDto.From).ToList(),
        HasEarlierMessages = thread.HasEarlierMessages,
    };
}
