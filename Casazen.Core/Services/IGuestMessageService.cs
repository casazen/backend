using Casazen.Core.Authorization;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The messages between the guest of a direct booking and the host (DB-14a): one thread per booking, written by the guest from the
/// guest area and by the host from the booking. No chat in real time (each side reads when it opens the screen), no attachment, no
/// automatic answer.
/// </summary>
/// <remarks>
/// <para><b>Who may call what.</b> The guest side takes the booking that <see cref="IGuestSessionService.FindBookingAsync"/> found for the
/// guest of the session, so a booking that is not the guest's never gets here. The host side takes the id of a booking the controller
/// has already authorized (<c>booking.read</c> / <c>booking.write</c> and the host's reach on the property) and reads the tenant of the
/// caller; the list applies the caller's <see cref="HostScope"/> in SQL.</para>
/// <para><b>What writes to a third party.</b> Two emails, and nothing else: to the host when the guest writes, to the guest when the host
/// answers. Each is the direct consequence of a message somebody just wrote, goes at most once every ten minutes per thread, and never
/// carries the text of the message. No job, no timer and no event sends a message or an email by itself.</para>
/// <para><b>Personal data.</b> The text is free text: it is never logged (only ids), the guest's text is stored with card numbers and
/// IBANs replaced (<see cref="GuestMessageRules.Redact"/>), and the thread goes away when the guest is erased.</para>
/// </remarks>
public interface IGuestMessageService
{
    /// <summary>
    /// The thread of the guest's booking (empty if nobody wrote yet), also after it closed.
    /// <see cref="Exceptions.NotFoundException"/> <c>guest_booking_not_found</c> (404) when the booking has no thread to show: it was
    /// cancelled or never confirmed (the same answer as a booking that is not the guest's).
    /// </summary>
    Task<GuestThreadView> GetForGuestAsync(Booking booking, CancellationToken cancellationToken = default);

    /// <summary>
    /// The guest's message. <see cref="Exceptions.DomainConflictException"/> <c>thread_closed</c> (409) once the thread has closed,
    /// <see cref="Exceptions.DomainRuleException"/> <c>guest_message_text_invalid</c> / <c>idempotency_key_invalid</c> (422), the 404 of
    /// <see cref="GetForGuestAsync"/>. The same key twice is the same message: the second call answers the first one and writes nothing.
    /// </summary>
    Task<SentGuestMessage> SendAsGuestAsync(Booking booking, string? text, string? idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>The thread of a booking for its host: messages, how many the host has not read, whether the guest waits for an answer.</summary>
    /// <exception cref="Exceptions.NotFoundException">The booking is not one of the caller's org.</exception>
    Task<HostThreadView> GetForHostAsync(Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The host's answer (also its first message: the host may write first). The same errors as <see cref="SendAsGuestAsync"/>; the text
    /// is stored as written. Answering marks the guest's messages as read.
    /// </summary>
    Task<SentGuestMessage> ReplyAsHostAsync(Guid bookingId, string? text, string? idempotencyKey, string? userId, CancellationToken cancellationToken = default);

    /// <summary>Marks the guest's messages as read by the host, up to the last one written.</summary>
    Task<HostThreadView> MarkReadAsync(Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The threads of the properties the scope reaches, the ones with the latest message first. With <paramref name="needsReply"/> true,
    /// only the open threads whose last message is the guest's (the host owes an answer); false, the others; <c>null</c>, all.
    /// </summary>
    Task<HostThreadPage> ListForHostAsync(
        HostScope scope,
        bool? needsReply,
        Guid? propertyId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}

/// <summary>A message as the screens show it.</summary>
public sealed record GuestThreadMessage(Guid Id, GuestMessageSender Sender, string Text, DateTime CreatedAt);

/// <summary>The guest's view of its thread: <paramref name="ClosesAt"/> is when it stops taking messages.</summary>
public sealed record GuestThreadView(GuestThreadState State, DateTime ClosesAt, IReadOnlyList<GuestThreadMessage> Messages);

/// <summary>
/// A message just sent. <paramref name="Redacted"/> says the text of the guest had a card number or an IBAN that was replaced;
/// <paramref name="Replayed"/> that the key had been used already and nothing was written.
/// </summary>
public sealed record SentGuestMessage(GuestThreadMessage Message, bool Redacted, bool Replayed);

/// <summary>
/// The host's view of the thread of a booking. <paramref name="UnreadCount"/> counts the guest's messages the host has not read;
/// <paramref name="NeedsReply"/> is true when the last message is the guest's and the thread is open.
/// </summary>
public sealed record HostThreadView(
    GuestThreadState State,
    DateTime? ClosesAt,
    int UnreadCount,
    bool NeedsReply,
    IReadOnlyList<GuestThreadMessage> Messages);

/// <summary>One row of the host's list of threads. The preview is the start of the guest's last message, for the host only.</summary>
public sealed record HostThreadSummary(
    Guid BookingId,
    string BookingCode,
    Guid PropertyId,
    string PropertyName,
    string GuestName,
    DateTime CheckInDate,
    DateTime CheckOutDate,
    GuestThreadState State,
    DateTime LastMessageAt,
    GuestMessageSender? LastMessageSender,
    string? LastGuestMessagePreview,
    int UnreadCount,
    bool NeedsReply);

public sealed record HostThreadPage(IReadOnlyList<HostThreadSummary> Items, int TotalCount, int Page, int PageSize);
