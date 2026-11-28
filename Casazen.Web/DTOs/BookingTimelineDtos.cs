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

    public IReadOnlyList<BookingTimelineEventDto> Events { get; init; } = [];

    public static BookingTimelineDto From(Guid bookingId, IReadOnlyList<BookingTimelineEvent> events) => new()
    {
        BookingId = bookingId,
        Events = events.Select(BookingTimelineEventDto.From).ToList(),
    };
}

public sealed class BookingTimelineEventDto
{
    /// <summary>
    /// <c>Created</c>, <c>RequestReceived</c>, <c>GuestEmailPending</c>, <c>ApprovalPending</c>, <c>PaymentPending</c>,
    /// <c>HostConfirmationPending</c>, <c>PaymentReceived</c>, <c>Confirmed</c>, <c>CheckInLinkSent</c>, <c>GuestDocumentsReceived</c>,
    /// <c>Arrived</c>, <c>Alloggiati</c>, <c>CheckedOut</c>, <c>Cancelled</c> or <c>Refund</c>.
    /// </summary>
    public BookingTimelineKind Kind { get; init; }

    public DateTime? At { get; init; }

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

public sealed class BookingTimelineDataDto
{
    public BookingSource? Source { get; init; }

    public decimal? Amount { get; init; }

    public PaymentMethod? Method { get; init; }

    public DateTime? ExpiresAt { get; init; }

    public BookingCancellationReason? Reason { get; init; }

    public string? Note { get; init; }

    public HostTodayCheckInState? LinkState { get; init; }

    /// <summary>
    /// The Alloggiati Web status as the Alloggiati page reads it (<c>Alloggiati</c>, from the arrival day): <c>DaInviareManualmente</c>
    /// (CasaZen does not transmit: the host sends it), <c>InviatoManualmente</c> (declared sent by the host, no receipt), <c>Inviato</c>
    /// (with a receipt), <c>Errore</c> or <c>Rifiutato</c>.
    /// </summary>
    public AlloggiatiWebStatus? AlloggiatiStatus { get; init; }

    /// <summary>The legal term of the Alloggiati communication (UTC) while it is not sent: 24 hours from the arrival, 6 for a stay of one night or less.</summary>
    public DateTime? DeadlineAt { get; init; }

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
