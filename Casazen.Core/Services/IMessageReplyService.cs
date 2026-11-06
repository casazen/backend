using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The write side of the hub of messages (E05/F02/US02/T01, ADR-032, ADR-023, NF-63): answer a conversation, write first to the guest of a booking,
/// and say through which channel a guest can be reached. A reply to a conversation with the guest of a direct booking goes through
/// <see cref="IGuestMessageService.ReplyAsHostAsync"/>, unchanged (idempotency key, closing of the thread, redaction); a reply to a conversation of a
/// channel is sent to the channel by the channel manager (<see cref="IOctorateApiClient.SendChatMessageAsync"/>); a first message to a guest who has no
/// conversation, on a channel that has no chat, is an e-mail.
/// </summary>
/// <remarks>
/// <para><b>Who may do what.</b> Every method takes the <see cref="HostScope"/> of the caller and applies it in SQL to the conversation or the booking
/// (<c>InScope</c>), and names the org of the scope in every query: a conversation or a booking of another org, or out of the reach of a collaborator
/// «Solo alcuni», is a <see cref="Exceptions.NotFoundException"/> (404), never a 403. A system caller (the assistant, a scheduled message, the executor
/// of an approval) passes the scope of the whole org (<c>new HostScope(orgId)</c>) with <see cref="MessageOrigin.Assistant"/> and its rule code: nothing is
/// ever allowed because a scope was left out. The permission to write (<c>booking.write</c>) is decided by the controller; this service never checks a role.</para>
/// <para><b>Once.</b> The same <c>Idempotency-Key</c> in the same conversation is the same message: the second call answers the first one and sends
/// nothing (OCT-10). A send whose outcome is not known (a timeout, a 5xx, an answer that cannot be read) is recorded as
/// <see cref="OtaMessageDeliveryState.ManualVerify"/> and is <b>never</b> sent again by this service: the copy of the conversation
/// (<see cref="IMessageSyncService"/>) matches it with the message that arrived, or a person checks on the channel. Nothing is retried here (OCT-13).</para>
/// <para><b>Nothing written to a channel that is not confirmed (OCT-04).</b> A conversation with no property, or whose property sits in a row of the
/// mapping nobody decided, is not written to: 409 <c>mapping_not_confirmed</c> and nothing leaves. With the Octorate connector off nothing leaves either.</para>
/// <para><b>Personal data.</b> The words of a message are never logged, and the e-mail address and the phone number that the channel manager gives for a
/// booking never leave this service but for the queue of the e-mails: they are not in an answer, a log or a row of CasaZen's tables (the audit line
/// <c>ContactInfoRead</c> holds the booking, the person who asked and the purpose); the job of the queue holds the address for as long as the job lives.</para>
/// </remarks>
public interface IMessageReplyService
{
    /// <summary>
    /// Answers a conversation of the hub. A <c>direct:</c> key goes to the existing direct service. An <c>ota:</c> key checks that writing is allowed (the
    /// conversation has a property whose mapping is decided, else 409 <c>mapping_not_confirmed</c>), then the rules of the channel
    /// (<see cref="MessageChannelRules"/>: 422 <c>message_text_invalid</c> or <c>channel_rule_violation</c>), then looks for a message with the same key
    /// (<see cref="MessageReplyResult.Replayed"/>), and otherwise writes the outbound row first (<see cref="OtaMessageDeliveryState.Created"/>), sends it,
    /// and stores the id and the state the channel manager reports. A typed refusal of the channel manager is the state
    /// <see cref="OtaMessageDeliveryState.Error"/> with its code in <see cref="MessageReplyResult.ErrorCode"/>; an unknown outcome is
    /// <see cref="OtaMessageDeliveryState.ManualVerify"/>. A message that went out marks the inbound messages of the conversation as read. 404
    /// <c>thread_not_found</c>; 409 <c>thread_closed</c> (a direct conversation past the seventh day after the check-out, or an anonymized one);
    /// 422 <c>idempotency_key_invalid</c>.
    /// </summary>
    /// <param name="origin">The person who wrote it, or the assistant; never the guest.</param>
    /// <param name="ruleCode">The rule of the assistant that produced the message; only with <see cref="MessageOrigin.Assistant"/>.</param>
    Task<MessageReplyResult> ReplyAsync(
        HostScope scope,
        MessageThreadKey key,
        string? text,
        string? idempotencyKey,
        string? userId,
        MessageOrigin origin,
        string? ruleCode,
        CancellationToken ct = default);

    /// <summary>
    /// Through which channel the guest of a booking is reached now (NF-63, AC-NF63-3): the conversation the booking has on a channel
    /// (<see cref="MessageContactChannel.Ota"/>), else the conversation with the guest of a direct booking while it is open
    /// (<see cref="MessageContactChannel.Direct"/>, the host may write first), else, for an active stay (confirmed, checked in or checked out, not a no-show:
    /// a pending or cancelled booking and a no-show get <c>no_conversation</c> and their contact is not read) of a channel that has no chat, an e-mail when
    /// the channel manager says it holds the guest's address (<see cref="MessageContactChannel.Email"/>), else <see cref="MessageContactChannel.None"/>
    /// with the reason. A conversation whose mapping is not confirmed is never chosen. It reads <c>emailAvailable</c> and <c>phoneAvailable</c>, which are
    /// flags: it never asks for the address or the number, and never returns them. 404 <c>booking_not_found</c> for a booking out of the reach of the scope.
    /// </summary>
    Task<MessageContactOptions> ChooseChannelAsync(HostScope scope, Guid bookingId, CancellationToken ct = default);

    /// <summary>
    /// Writes first to the guest of a booking through the channel <see cref="ChooseChannelAsync"/> chooses: the conversation of the channel or the direct
    /// one (as <see cref="ReplyAsync"/>), or an e-mail. For the e-mail the contact of that one booking is read from the channel manager (never in bulk;
    /// 409 <c>contact_info_unavailable</c> when it is refused, which it is until CasaZen's static IP addresses are registered), the e-mail is queued with the
    /// template <c>HostGuestMessage</c> in the language of the guest, with the organization's address as the one an answer goes to, and the read is
    /// recorded in the activity log as <c>ContactInfoRead</c>: the booking, the person, the purpose; never the address. 409 <c>no_contact_channel</c>
    /// when there is no way to write.
    /// </summary>
    Task<MessageReplyResult> StartConversationAsync(
        HostScope scope,
        Guid bookingId,
        string? text,
        string? idempotencyKey,
        string? userId,
        CancellationToken ct = default);
}

/// <summary>How a guest can be written to from CasaZen (<see cref="MessageContactOptions.Channel"/>).</summary>
public enum MessageContactChannel
{
    /// <summary>There is no way to write to the guest now (<see cref="MessageContactOptions.Reason"/> says why).</summary>
    None = 0,

    /// <summary>The conversation the booking has on a channel (Airbnb, Booking.com, Vrbo, Expedia), through the channel manager.</summary>
    Ota = 1,

    /// <summary>The conversation with the guest of a direct booking, in CasaZen itself.</summary>
    Direct = 2,

    /// <summary>An e-mail to the guest, for a booking of a channel that has no chat.</summary>
    Email = 3,
}

/// <summary>
/// The answer of <see cref="IMessageReplyService.ChooseChannelAsync"/>. <paramref name="ThreadKey"/> is the conversation to write in (<c>ota:</c> or
/// <c>direct:</c>), <c>null</c> for an e-mail and for none. <paramref name="Reason"/> is a stable code, set when there is no channel:
/// <c>mapping_not_confirmed</c>, <c>thread_closed</c>, <c>no_conversation</c> or <c>contact_info_unavailable</c>. The two flags say whether an address and
/// a number exist where the channel manager holds the contact: they are never the address or the number.
/// </summary>
public sealed record MessageContactOptions(
    MessageContactChannel Channel,
    MessageThreadKey? ThreadKey,
    string? Reason,
    bool EmailAvailable,
    bool PhoneAvailable)
{
    /// <summary>No conversation of the booking takes a message now.</summary>
    public const string NoConversationReason = "no_conversation";

    /// <summary>The booking is a channel's, its conversation (or listing) is not mapped to a property that was decided: nothing is written (OCT-04).</summary>
    public const string MappingNotConfirmedReason = MessageThreadActions.MappingNotConfirmedReason;

    /// <summary>The conversation with the guest of the direct booking is closed.</summary>
    public const string ThreadClosedReason = MessageThreadActions.ThreadClosedReason;

    /// <summary>The booking has no conversation and the channel manager holds no address for its guest, or it cannot be asked.</summary>
    public const string ContactInfoUnavailableReason = "contact_info_unavailable";

    /// <summary>The Octorate connector is off: nothing is written to a channel (the same code as <c>OctorateAuthException.NotConnectedCode</c>).</summary>
    public const string NotConnectedReason = "octorate_not_connected";
}

/// <summary>
/// The outcome of a send (<see cref="IMessageReplyService.ReplyAsync"/>, <see cref="IMessageReplyService.StartConversationAsync"/>).
/// <paramref name="Message"/> is the message as the hub shows it (<c>null</c> for an e-mail, which is not kept as a conversation);
/// <paramref name="State"/> is where it is: <see cref="OtaMessageDeliveryState.Sent"/> (also for a direct message and an e-mail that was queued),
/// <see cref="OtaMessageDeliveryState.Created"/> or <see cref="OtaMessageDeliveryState.WaitingAuthorization"/> (the channel manager has it and sends it),
/// <see cref="OtaMessageDeliveryState.Error"/> (with <paramref name="ErrorCode"/>) or <see cref="OtaMessageDeliveryState.ManualVerify"/> (a person checks
/// on the channel before it is sent again). <paramref name="Replayed"/> is true when the same key had written it already and nothing was sent.
/// </summary>
public sealed record MessageReplyResult(
    MessageContactChannel Channel,
    MessageThreadKey? ThreadKey,
    MessageHubMessage? Message,
    OtaMessageDeliveryState State,
    bool Replayed,
    string? ErrorCode);
