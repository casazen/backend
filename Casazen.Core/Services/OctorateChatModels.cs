using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public sealed record OctorateChatThreadPage(IReadOnlyList<OctorateChatThread> Threads, int ItemCount)
{
    public int Unreadable => ItemCount - Threads.Count;
}

/// <summary>
/// A chat thread as Octorate describes it (<c>ChatThreadDTO</c>): only the members the copy of the conversations uses. The name of the guest is
/// personal data: never logged.
/// </summary>
public sealed record OctorateChatThread
{
    public required string Id { get; init; }

    public string? AccommodationId { get; init; }

    public string? DefaultProcessor { get; init; }

    public bool? AiDriven { get; init; }

    public string? ReservationId { get; init; }

    public string? ReservationChannelId { get; init; }

    public string? ReservationChannelName { get; init; }

    public IReadOnlyList<string> ConnectionIds { get; init; } = [];

    public bool HasAirbnbBookingDetails { get; init; }

    public string? GuestName { get; init; }

    public DateTime? ArchivedAt { get; init; }

    public DateTime? LastActivityAt { get; init; }

    public string? ExternalThreadId { get; init; }

    /// <summary>
    /// The person of the host side the messages of CasaZen are written as: the first member of the thread that is a user of the channel manager
    /// (<c>persons[]</c> with a <c>userId</c>, its <c>id</c>), which is the <c>sender</c> a message requires (E05/F02/US02/T01). <c>null</c> when the
    /// thread has no such member. A name or a contact of that person is never read.
    /// </summary>
    public string? HostPersonId { get; init; }
}

/// <summary>
/// What the channel manager answered to a message sent to a channel (<c>POST /rest/v1/chat/{property}/messages</c>, E05/F02/US02/T01): the id it gave
/// the message and the state it reports, read once from the answer (<c>status</c>: <c>CREATED</c>, <c>WAIT_AUTH</c>, <c>SENT</c>, <c>ERROR</c>,
/// <c>MANUAL_VERIFY</c>; a word it does not know is <see cref="OtaMessageDeliveryState.ManualVerify"/>, never a success).
/// </summary>
/// <param name="MessageId">The message on Octorate (a number kept as text): how the copy of the conversation finds it again.</param>
/// <param name="State">Where the message is on its way to the guest.</param>
public sealed record OctorateChatSendResult(string MessageId, OtaMessageDeliveryState State);

/// <summary>
/// A message template of the channel manager for a thread (<c>MailTemplateDTO</c>, E05/F02/US02/T02): read only, to copy into the composer. Never
/// edited and never stored by CasaZen.
/// </summary>
/// <param name="Id">The template on Octorate, kept as text.</param>
/// <param name="Name">What the template is called there.</param>
/// <param name="Language">The language it is written in (<c>IT</c>, <c>EN</c>, ...), when the answer says.</param>
/// <param name="Text">The body of the template, with the variables the channel manager has.</param>
public sealed record OctorateChatTemplate(string Id, string Name, string? Language, string Text);

/// <summary>
/// A chat message as Octorate describes it (<c>ChatMessageDTO</c>): only the members the copy uses. The text, the name of the sender and the
/// address of the attachment are personal data: never logged. The direction and the delivery state are read here, once, from what Octorate
/// says (<c>OTA_FLOW_DIRECTION</c>, the sender, <c>status</c>), so the service that stores the message never interprets the wire.
/// </summary>
public sealed record OctorateChatMessage
{
    public required string Id { get; init; }

    public string? ThreadId { get; init; }

    public string? Processor { get; init; }

    public OtaMessageDirection Direction { get; init; }

    public string SenderName { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    public string? AttachmentUrl { get; init; }

    public string? AttachmentContentType { get; init; }

    public DateTime SentAt { get; init; }

    public DateTime? ReadAt { get; init; }

    public OtaMessageDeliveryState DeliveryState { get; init; }
}
