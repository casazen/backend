using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The hub of messages (E05/F02/US01/T01, ADR-032, NF-61, NF-64): one list and one thread model over the conversations of the channels
/// (<see cref="OtaMessageThread"/>, copied from the channel manager) and the ones with the guests of direct bookings (<c>GuestThread</c>, served
/// by <see cref="IGuestMessageService"/>, which this does not replace and does not move). It reads; the reply is the service of F02/US02.
/// </summary>
/// <remarks>
/// <para><b>Who may see what.</b> Every method takes the <see cref="HostScope"/> of the caller and applies it to both sources in SQL
/// (<c>InScope</c>), and names the org of the scope in every query: a collaborator «Solo alcuni» reaches the threads of its properties only, and
/// a thread of another org is a 404 (<see cref="Exceptions.NotFoundException"/>), never a 403. A conversation of a channel whose listing has no
/// confirmed mapping has no property (<see cref="MessageHubThreadSummary.Unassigned"/>): it is listed and read only by a scope that reaches every
/// property of the org, and is never shown under a property it was not mapped to (OCT-04).</para>
/// <para><b>Personal data.</b> The text of a message, the name of the guest and the address of an attachment are never logged. The address of an
/// attachment never leaves the service: the client gets the file through <see cref="OpenAttachmentAsync"/> and nothing else.</para>
/// </remarks>
public interface IMessageHubService
{
    /// <summary>
    /// The conversations the scope reaches, both sources in one list, the one with the latest message first, paged in SQL (one statement
    /// concatenates the two projections). <paramref name="page"/> starts at 1 and <paramref name="pageSize"/> is clamped to
    /// <see cref="GuestMessageRules.MaxPageSize"/>.
    /// </summary>
    Task<MessageHubPage> ListAsync(
        HostScope scope,
        MessageHubFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One conversation: its messages (the latest <see cref="GuestMessageRules.TranscriptMaxMessages"/>, oldest first), the booking card, the
    /// link to the customer and what the host may do in it. <paramref name="includeAmounts"/> is false for a caller without <c>payment.read</c>
    /// (SR-D7): the card then carries no money. <see cref="Exceptions.NotFoundException"/> <c>thread_not_found</c> (404) when the conversation does
    /// not exist, is of another org or is out of the reach of the scope, or when a direct booking has no thread.
    /// </summary>
    Task<MessageThreadView> GetThreadAsync(
        HostScope scope,
        MessageThreadKey key,
        bool includeAmounts,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the messages of the guest as read by the host: direct through <see cref="IGuestMessageService.MarkReadAsync"/>, a conversation of a
    /// channel by setting <c>ReadAt</c> on its inbound messages and recounting <c>UnreadCount</c>. Answers how many are still unread. The same 404 as
    /// <see cref="GetThreadAsync"/>.
    /// </summary>
    Task<MessageHubReadResult> MarkReadAsync(
        HostScope scope,
        MessageThreadKey key,
        string? userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The conversations with something the host has not read, with how many, for the badges: counters only, never a name or a text. Direct ones are
    /// the open threads of <see cref="IGuestMessageService.ListUnreadForHostAsync"/> (at most <see cref="GuestMessageRules.UnreadThreadsMax"/>);
    /// with <paramref name="includeOta"/> the conversations of the channels follow, at most as many, the latest first.
    /// </summary>
    Task<IReadOnlyList<MessageHubUnreadThread>> ListUnreadAsync(
        HostScope scope,
        bool includeOta,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The file of an OTA message, fetched from the address the channel manager gave (https, 30 seconds, 20 MB, one of three types). A message
    /// outside the org or the reach of the scope is a 404 <c>message_not_found</c>; a message the caller reaches whose file is gone, expired, too big, of
    /// another type or unreachable is a 404 <c>attachment_unavailable</c>. The address is never in an answer, a header or a log.
    /// </summary>
    Task<OtaAttachmentContent> OpenAttachmentAsync(
        HostScope scope,
        Guid messageId,
        CancellationToken cancellationToken = default);
}

/// <summary>The channel of a conversation of the hub: the direct one, or one of the four channels with an integrated chat. The numbers of the channels are those of <see cref="OtaChannel"/>.</summary>
public enum MessageHubChannel
{
    Direct = 0,
    Airbnb = 1,
    Booking = 2,
    Vrbo = 3,
    Expedia = 4,
}

/// <summary>What a conversation is about: a reservation, or a request with no booking yet (an inquiry). The numbers are those of <see cref="OtaThreadKind"/>; a direct conversation is always a reservation.</summary>
public enum MessageHubThreadKind
{
    Reservation = 1,
    Request = 2,
}

/// <summary>Whether the conversation is still being worked on: open, closed (a direct thread past the seventh day after the check-out) or archived on the channel manager.</summary>
public enum MessageHubThreadState
{
    Open = 0,
    Closed = 1,
    Archived = 2,
}

/// <summary>
/// The filters of the list, all optional and all combined with AND. <see cref="Query"/> matches the name of the guest, the code of the booking and
/// the name of the property, anywhere in them, any case: plain columns only, the text of a message is encrypted and is not searched.
/// </summary>
/// <param name="NeedsReply">True: open conversations whose last message is the guest's. False: the others. <c>null</c>: all.</param>
/// <param name="PropertyId">The conversations of one property (an unassigned one is of none).</param>
/// <param name="Channel">The conversations of one channel, or the direct ones.</param>
/// <param name="BookingId">The conversation of one booking.</param>
/// <param name="Unread">True: with messages the host has not read. False: with none.</param>
/// <param name="Kind">Reservations, or requests (a direct conversation is a reservation).</param>
/// <param name="Query">A piece of the name of the guest, of the booking code or of the name of the property.</param>
public sealed record MessageHubFilter(
    bool? NeedsReply = null,
    Guid? PropertyId = null,
    MessageHubChannel? Channel = null,
    Guid? BookingId = null,
    bool? Unread = null,
    MessageHubThreadKind? Kind = null,
    string? Query = null)
{
    /// <summary>The longest text of <see cref="Query"/> that counts: a name or a code never needs more.</summary>
    public const int QueryMaxLength = 64;
}

/// <summary>
/// One row of the list of the hub. The preview is the start of the last message of the conversation (any of its authors), for the host only;
/// <paramref name="CheckInDate"/> and <paramref name="CheckOutDate"/> are the instants stored on the booking (the API shows them as Rome dates).
/// </summary>
public sealed record MessageHubThreadSummary(
    MessageThreadKey Key,
    MessageThreadSource Source,
    MessageHubChannel Channel,
    MessageHubThreadKind Kind,
    MessageHubThreadState State,
    Guid? PropertyId,
    string? PropertyName,
    bool Unassigned,
    Guid? BookingId,
    string? BookingCode,
    string? GuestName,
    DateTime? CheckInDate,
    DateTime? CheckOutDate,
    DateTime LastMessageAt,
    MessageOrigin? LastMessageOrigin,
    string? Preview,
    int UnreadCount,
    bool NeedsReply);

/// <summary>A page of the list of the hub.</summary>
public sealed record MessageHubPage(IReadOnlyList<MessageHubThreadSummary> Items, int TotalCount, int Page, int PageSize);

/// <summary>A conversation with <paramref name="UnreadCount"/> (at least 1) messages of the guest the host has not read; <paramref name="BookingId"/> is the booking it is about, when there is one.</summary>
public sealed record MessageHubUnreadThread(MessageThreadKey Key, Guid? BookingId, int UnreadCount);

/// <summary>What is left unread in a conversation once the host marked it read.</summary>
public sealed record MessageHubReadResult(MessageThreadKey Key, int UnreadCount);

/// <summary>
/// A message of a conversation. <paramref name="Origin"/> is the guest, a person of the host side or the assistant; <paramref name="RuleCode"/> is the
/// situation an assistant message answered (<c>null</c> for the others, and for every direct message until the assistant writes there too);
/// <paramref name="DeliveryState"/> is where an outbound message of a channel is on its way to the guest (<c>null</c> for the others);
/// <paramref name="AttachmentContentType"/> is the type of its file when it has one, which the client opens through the proxy and never by an address.
/// </summary>
public sealed record MessageHubMessage(
    Guid Id,
    OtaMessageDirection Direction,
    MessageOrigin Origin,
    string? RuleCode,
    string? SenderName,
    string Text,
    DateTime SentAt,
    DateTime? ReadAt,
    OtaMessageDeliveryState? DeliveryState,
    string? AttachmentContentType);

/// <summary>
/// The amounts of a booking in <see cref="MessageBookingCard.Currency"/>. For a booking whose money is the channel manager's
/// (<see cref="BookingAmountSourceRules.HoldsChannelManagerMoney"/>): the four figures the channel manager gave (each <c>null</c> when it did not,
/// never derived from another) and the total when it is set; for any other booking, one CasaZen sent to Octorate included: the total and what came in.
/// </summary>
public sealed record MessageBookingAmounts(decimal? Total, decimal? Paid, decimal? Commission, decimal? Net, decimal? Balance);

/// <summary>
/// The booking beside a conversation (ADR-032-R8, AC-NF61-3): the code, the guest, the stay as instants (the API shows Rome dates), the people, the
/// state, where it came from and the amounts. <paramref name="Amounts"/> is <c>null</c> for a caller who may not read payments.
/// </summary>
public sealed record MessageBookingCard(
    Guid BookingId,
    string BookingCode,
    string GuestName,
    DateTime CheckInDate,
    DateTime CheckOutDate,
    int Guests,
    int Adults,
    int Children,
    BookingStatus Status,
    BookingSource Source,
    string? ChannelLabel,
    string Currency,
    MessageBookingAmounts? Amounts);

/// <summary>
/// What the host may do in a conversation. The answer of <see cref="OtaChannelCapabilities"/> for a conversation of a channel; all false with the
/// <see cref="MappingNotConfirmedReason"/> while its listing has no confirmed mapping, or its property is in a row of the mapping that nobody decided
/// (OCT-04); a direct thread is writable while it is open (<see cref="ThreadClosedReason"/> after).
/// </summary>
/// <param name="CanReply">The host may answer.</param>
/// <param name="ContactDetailsAllowed">A link, an address or a number may be written.</param>
/// <param name="AttachmentsAllowed">The channel is confirmed to take an attachment of the host (D3).</param>
/// <param name="AutoReplyAllowed">The channel is confirmed to take an automatic answer (D4).</param>
/// <param name="Reason">Why nothing is allowed: <see cref="MappingNotConfirmedReason"/> or <see cref="ThreadClosedReason"/>; <c>null</c> when the actions are the channel's own.</param>
public sealed record MessageThreadActions(
    bool CanReply,
    bool ContactDetailsAllowed,
    bool AttachmentsAllowed,
    bool AutoReplyAllowed,
    string? Reason)
{
    /// <summary>The conversation has no confirmed property mapping, so nothing is written to the channel (the same code the reply answers with, 409).</summary>
    public const string MappingNotConfirmedReason = "mapping_not_confirmed";

    /// <summary>The direct thread is past the seventh day after the check-out (the same code the reply answers with, 409).</summary>
    public const string ThreadClosedReason = "thread_closed";

    /// <summary>Nothing is allowed because of <paramref name="reason"/>.</summary>
    public static MessageThreadActions Blocked(string reason) => new(false, false, false, false, reason);
}

/// <summary>
/// One conversation with everything the screen shows beside it. <paramref name="CustomerGuestId"/> is the guest of the booking, for the link to the
/// profile (the route is built by the client; <c>null</c> for a request, which has no booking). <paramref name="HasEarlierMessages"/> is true when the
/// conversation has more messages than the latest ones it carries.
/// </summary>
public sealed record MessageThreadView(
    MessageThreadKey Key,
    MessageThreadSource Source,
    MessageHubChannel Channel,
    MessageHubThreadKind Kind,
    MessageHubThreadState State,
    Guid? PropertyId,
    string? PropertyName,
    bool Unassigned,
    string? GuestName,
    int UnreadCount,
    bool NeedsReply,
    bool Anonymized,
    MessageBookingCard? Booking,
    Guid? CustomerGuestId,
    MessageThreadActions Actions,
    IReadOnlyList<MessageHubMessage> Messages,
    bool HasEarlierMessages);
