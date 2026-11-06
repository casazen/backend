using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// One page of the chat threads of an accommodation (<c>GET /rest/v1/chat/{property}/threads</c>, E05/F01/US01/T02). The list has no page
/// size of its own: the next page starts where the threads already read end, and the list ends with a page that has no item at all, not with
/// a short one.
/// </summary>
/// <param name="Threads">The threads that could be read; an item without a usable id is left out.</param>
/// <param name="ItemCount">How many items the answer had, the unreadable ones included: what the next offset moves by.</param>
public sealed record OctorateChatThreadPage(IReadOnlyList<OctorateChatThread> Threads, int ItemCount)
{
    /// <summary>The items of the page that could not be read.</summary>
    public int Unreadable => ItemCount - Threads.Count;
}

/// <summary>
/// A chat thread as Octorate describes it (<c>ChatThreadDTO</c>): only the members the copy of the conversations uses. The name of the guest is
/// personal data: never logged.
/// </summary>
public sealed record OctorateChatThread
{
    /// <summary>The thread (<c>id</c>, a number kept as text): the key of the copy.</summary>
    public required string Id { get; init; }

    /// <summary>The accommodation that owns the thread (<c>accommodationId</c>), when the answer says.</summary>
    public string? AccommodationId { get; init; }

    /// <summary>The default processor of the thread (<c>defaultProcessor</c>: <c>DIRECT</c>, <c>CHANNEL</c>, <c>WHATSAPP</c>, ...).</summary>
    public string? DefaultProcessor { get; init; }

    /// <summary>The flag of Octorate's own assistant for the thread (<c>aiDriven</c>); <c>null</c> when the thread does not say.</summary>
    public bool? AiDriven { get; init; }

    /// <summary>The reservation the thread is linked to (<c>reservation.id</c>); <c>null</c> for an inquiry.</summary>
    public string? ReservationId { get; init; }

    /// <summary>The channel code of that reservation (<c>reservation.channelId</c>): 142 Booking.com, 274 Airbnb, 235 Vrbo, 4 Expedia.</summary>
    public string? ReservationChannelId { get; init; }

    /// <summary>The channel name of that reservation (<c>reservation.channelName</c>).</summary>
    public string? ReservationChannelName { get; init; }

    /// <summary>The connections of the accommodation the thread comes through (<c>chatThreadExternals[].connectionId</c>).</summary>
    public IReadOnlyList<string> ConnectionIds { get; init; } = [];

    /// <summary>The thread carries the details of an Airbnb booking (the attribute <c>AIRBNB_BOOKING_DETAILS</c>): a sign of its channel.</summary>
    public bool HasAirbnbBookingDetails { get; init; }

    /// <summary>The name of the guest, from the members of the thread that are not users. Personal data.</summary>
    public string? GuestName { get; init; }

    /// <summary>When the thread was archived (<c>archiveTime</c>, UTC); <c>null</c> while it is not.</summary>
    public DateTime? ArchivedAt { get; init; }

    /// <summary>When the newest message of the thread was sent (<c>lastMessage</c>, UTC), when the answer carries it.</summary>
    public DateTime? LastActivityAt { get; init; }
}

/// <summary>
/// A chat message as Octorate describes it (<c>ChatMessageDTO</c>): only the members the copy uses. The text, the name of the sender and the
/// address of the attachment are personal data: never logged. The direction and the delivery state are read here, once, from what Octorate
/// says (<c>OTA_FLOW_DIRECTION</c>, the sender, <c>status</c>), so the service that stores the message never interprets the wire.
/// </summary>
public sealed record OctorateChatMessage
{
    /// <summary>The message (<c>id</c>, a number kept as text): the key of the copy.</summary>
    public required string Id { get; init; }

    /// <summary>The thread the message belongs to (<c>threadId</c>), when the answer says (the messages of <c>recentMessages</c> name theirs).</summary>
    public string? ThreadId { get; init; }

    /// <summary>The processor of the message (<c>processor</c>): <c>CHANNEL</c> for the channel, <c>NOTE</c> for an internal note of the team.</summary>
    public string? Processor { get; init; }

    public OtaMessageDirection Direction { get; init; }

    /// <summary>The name of the sender. Personal data; empty when the message does not say.</summary>
    public string SenderName { get; init; } = string.Empty;

    /// <summary>What was written (the attribute <c>TEXT</c>); empty for a message that holds only an attachment.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>The address of the attachment, an absolute http or https one; <c>null</c> when there is none.</summary>
    public string? AttachmentUrl { get; init; }

    /// <summary><c>image/jpeg</c>, <c>image/png</c> or <c>application/octet-stream</c>; <c>null</c> without an attachment.</summary>
    public string? AttachmentContentType { get; init; }

    /// <summary>When the message was sent, else created (UTC).</summary>
    public DateTime SentAt { get; init; }

    /// <summary>When a person of the host side read it (<c>readTime</c>, or the first of <c>readers</c>, UTC); <c>null</c> while nobody did.</summary>
    public DateTime? ReadAt { get; init; }

    public OtaMessageDeliveryState DeliveryState { get; init; }
}
