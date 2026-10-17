using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// The history of a booking (SR-05a, <c>GET /api/bookings/{id}/timeline</c>): what happened, in order, and what is waiting right now.
/// Derived from the timestamps the booking keeps and the rows tied to it; there is no event log. A step that happened before its time
/// was kept (a stay confirmed, cancelled or checked out before SR-05a) is there with <c>at: null</c>, never with an invented time. The
/// steps still to come (the link to send, the arrival, the departure) are not entries: the booking sheet says what happens next.
/// </summary>
public sealed class BookingTimelineDto
{
    public Guid BookingId { get; init; }

    /// <summary>The entries in order, by time. Nothing in them is a name or an email.</summary>
    public IReadOnlyList<BookingTimelineEventDto> Events { get; init; } = [];

    public static BookingTimelineDto From(Guid bookingId, IReadOnlyList<BookingTimelineEvent> events) => new()
    {
        BookingId = bookingId,
        Events = events.Select(BookingTimelineEventDto.From).ToList(),
    };
}

/// <summary>An entry of the timeline. The title is the <see cref="Kind"/>: the words are the client's (IT and EN).</summary>
public sealed class BookingTimelineEventDto
{
    /// <summary>
    /// <c>Created</c>, <c>RequestReceived</c>, <c>GuestEmailPending</c>, <c>ApprovalPending</c>, <c>PaymentPending</c>,
    /// <c>HostConfirmationPending</c>, <c>PaymentReceived</c>, <c>Confirmed</c>, <c>CheckInLinkSent</c>, <c>GuestDocumentsReceived</c>,
    /// <c>Arrived</c>, <c>Alloggiati</c>, <c>CheckedOut</c>, <c>Cancelled</c> or <c>Refund</c>.
    /// </summary>
    public BookingTimelineKind Kind { get; init; }

    /// <summary>
    /// When it happened (UTC). <c>null</c> for a step that happened before the time was kept, for an arrival registered after the check-in
    /// day, and for a wait with no instant of its own.
    /// </summary>
    public DateTime? At { get; init; }

    /// <summary><c>Done</c>, or <c>Current</c> (it waits for the host, the guest or the bank).</summary>
    public BookingTimelineState State { get; init; }

    public BookingTimelineDataDto Data { get; init; } = new();

    public static BookingTimelineEventDto From(BookingTimelineEvent entry) => new()
    {
        Kind = entry.Kind,
        At = entry.At,
        State = entry.State,
        Data = BookingTimelineDataDto.From(entry.Data),
    };
}

/// <summary>What an entry says besides its kind: each field belongs to a few kinds and is <c>null</c> for the others.</summary>
public sealed class BookingTimelineDataDto
{
    /// <summary>Where the booking came from (<c>Created</c>, <c>RequestReceived</c>): <c>Direct</c> (the booking site), <c>Manual</c>, <c>Airbnb</c>, <c>BookingCom</c>…</summary>
    public BookingSource? Source { get; init; }

    /// <summary>
    /// Euros (<c>PaymentReceived</c>, <c>Refund</c>). <c>null</c> for a caller without <c>payment.read</c>: the entry is there, without the
    /// figure.
    /// </summary>
    public decimal? Amount { get; init; }

    /// <summary>How it was paid (<c>PaymentReceived</c>): <c>CreditCard</c>, <c>CashOnArrival</c>…</summary>
    public PaymentMethod? Method { get; init; }

    /// <summary>The term of a request that waits (<c>GuestEmailPending</c>, <c>ApprovalPending</c>), UTC.</summary>
    public DateTime? ExpiresAt { get; init; }

    /// <summary>
    /// Why a booking was cancelled by the system, or the request the host declined (<c>Cancelled</c>): <c>CheckoutHoldExpired</c>,
    /// <c>DatesUnavailableAtPayment</c>, <c>OnSiteRequestDeclined</c>, <c>OnSiteRequestExpired</c>, <c>OnSiteEmailNotConfirmed</c> or
    /// <c>DeferredPaymentNotCompleted</c>; <c>null</c> for a cancellation of a person.
    /// </summary>
    public BookingCancellationReason? Reason { get; init; }

    /// <summary>What the host wrote when cancelling (<c>Cancelled</c>). The host's own note: it is never sent to the guest.</summary>
    public string? Note { get; init; }

    /// <summary>Where the link of the online check-in stands (<c>CheckInLinkSent</c>): <c>Sent</c>, <c>InProgress</c>, <c>Expired</c> or <c>Completed</c>.</summary>
    public HostTodayCheckInState? LinkState { get; init; }

    /// <summary>
    /// The Alloggiati Web status as the Alloggiati page reads it (<c>Alloggiati</c>, from the arrival day): <c>DaInviareManualmente</c>
    /// (CasaZen does not transmit: the host sends it), <c>InviatoManualmente</c> (declared sent by the host, no receipt), <c>Inviato</c>
    /// (with a receipt), <c>Errore</c> or <c>Rifiutato</c>.
    /// </summary>
    public AlloggiatiWebStatus? AlloggiatiStatus { get; init; }

    /// <summary>The legal term of the Alloggiati communication (UTC) while it is not sent: 24 hours from the arrival, 6 for a stay of one night or less.</summary>
    public DateTime? DeadlineAt { get; init; }

    /// <summary>Where the refund stands (<c>Refund</c>): <c>Succeeded</c>, <c>Pending</c>, <c>RequiresAction</c> or <c>Failed</c>.</summary>
    public PaymentRefundStatus? RefundStatus { get; init; }

    public static BookingTimelineDataDto From(BookingTimelineData data) => new()
    {
        Source = data.Source,
        Amount = data.Amount,
        Method = data.Method,
        ExpiresAt = data.ExpiresAt,
        Reason = data.Reason,
        Note = data.Note,
        LinkState = data.LinkState,
        AlloggiatiStatus = data.AlloggiatiStatus,
        DeadlineAt = data.DeadlineAt,
        RefundStatus = data.RefundStatus,
    };
}
