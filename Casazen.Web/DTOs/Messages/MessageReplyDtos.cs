using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Messages;

/// <summary>
/// The body of a message to a guest: <c>Text</c>, plain text of 1 to 2000 characters. It carries no validation attribute on purpose: an empty or too long
/// text is a 422 <c>message_text_invalid</c> of the service (like the rules of the channel), not a 400 of the binder.
/// </summary>
public sealed class ReplyRequest
{
    public string? Text { get; set; }
}

/// <summary>
/// The answer to a send (<c>POST api/messages/threads/{threadKey}/reply</c>, <c>POST api/bookings/{id}/messages/new</c>, NF-63). <c>Message</c> is the
/// message as the thread shows it (absent for an e-mail, which is not kept as a conversation); <c>State</c> is where it is on its way:
/// <c>Sent</c>, <c>Created</c> or <c>WaitingAuthorization</c> (the channel manager has it and sends it), <c>Error</c> (with <c>ErrorCode</c>) or
/// <c>ManualVerify</c> (the answer is 202: a person checks on the channel before it is sent again). <c>Replayed</c> is true when the same
/// <c>Idempotency-Key</c> had already written it and nothing was sent. <c>Channel</c> is <c>Ota</c>, <c>Direct</c> or <c>Email</c>; <c>ThreadKey</c> is the
/// conversation it is in (absent for an e-mail).
/// </summary>
public sealed class MessageReplyDto
{
    public MessageContactChannel Channel { get; init; }
    public string? ThreadKey { get; init; }
    public MessageHubMessageDto? Message { get; init; }
    public OtaMessageDeliveryState State { get; init; }
    public bool Replayed { get; init; }
    public string? ErrorCode { get; init; }

    public static MessageReplyDto From(MessageReplyResult result) => new()
    {
        Channel = result.Channel,
        ThreadKey = result.ThreadKey?.ToString(),
        Message = result.Message is null ? null : MessageHubMessageDto.From(result.Message),
        State = result.State,
        Replayed = result.Replayed,
        ErrorCode = result.ErrorCode,
    };
}

/// <summary>
/// How the guest of a booking can be written to (<c>GET api/bookings/{id}/contact-options</c>): <c>Channel</c> is <c>Ota</c> (the conversation of the
/// channel, <c>ThreadKey</c>), <c>Direct</c> (the conversation of the booking site) or <c>Email</c>, or <c>None</c> with a <c>Reason</c>
/// (<c>mapping_not_confirmed</c>, <c>thread_closed</c>, <c>no_conversation</c>, <c>contact_info_unavailable</c>, <c>octorate_not_connected</c>).
/// <c>EmailAvailable</c> and <c>PhoneAvailable</c> say whether an address and a number exist: they are never the address or the number.
/// </summary>
public sealed class MessageContactOptionsDto
{
    public MessageContactChannel Channel { get; init; }
    public string? ThreadKey { get; init; }
    public string? Reason { get; init; }
    public bool EmailAvailable { get; init; }
    public bool PhoneAvailable { get; init; }

    public static MessageContactOptionsDto From(MessageContactOptions options) => new()
    {
        Channel = options.Channel,
        ThreadKey = options.ThreadKey?.ToString(),
        Reason = options.Reason,
        EmailAvailable = options.EmailAvailable,
        PhoneAvailable = options.PhoneAvailable,
    };
}
