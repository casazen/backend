using Casazen.Core.Entities;
using Casazen.Core.Regulatory;
using Casazen.Core.Utilities;

namespace Casazen.Core.Services;

public enum BookingTimelineKind
{
    Created,

    RequestReceived,

    GuestEmailPending,

    ApprovalPending,

    PaymentPending,

    HostConfirmationPending,

    PaymentReceived,

    Confirmed,

    CheckInLinkSent,

    GuestDocumentsReceived,

    Arrived,

    /// <summary>
    /// The Alloggiati Web communication, from the arrival day, as it honestly stands (<see cref="BookingTimelineData.AlloggiatiStatus"/>):
    /// never "sent" without a receipt or the host's declaration. It follows the arrival.
    /// </summary>
    Alloggiati,

    CheckedOut,

    Cancelled,

    Refund,
}

public enum BookingTimelineState
{
    Done,

    Current,
}

public sealed record BookingTimelineData
{
    public BookingSource? Source { get; init; }

    public decimal? Amount { get; init; }

    public PaymentMethod? Method { get; init; }

    public DateTime? ExpiresAt { get; init; }

    public BookingCancellationReason? Reason { get; init; }

    public string? Note { get; init; }

    public HostTodayCheckInState? LinkState { get; init; }

    public AlloggiatiWebStatus? AlloggiatiStatus { get; init; }

    /// <summary>The legal term of the Alloggiati communication (UTC) while it is not sent.</summary>
    public DateTime? DeadlineAt { get; init; }

    public PaymentRefundStatus? RefundStatus { get; init; }
}

/// <summary>An entry of the timeline.</summary>
/// <param name="Kind">What it is about.</param>
/// <param name="At">
/// When it happened (UTC); <c>null</c> for what happened before the booking kept that time (a stay confirmed, cancelled or checked out
/// before SR-05a: no time is invented) and for a wait that has no instant of its own.
/// </param>
/// <param name="State">Done or current.</param>
/// <param name="Data">What it says besides.</param>
public sealed record BookingTimelineEvent(BookingTimelineKind Kind, DateTime? At, BookingTimelineState State, BookingTimelineData Data);

public sealed record BookingTimelinePayment(PaymentStatus Status, PaymentMethod Method, decimal Amount, DateTime At);

public sealed record BookingTimelineRefund(PaymentRefundStatus Status, decimal Amount, DateTime At);

public sealed record BookingTimelineCheckIn(HostTodayCheckInState State, DateTime? LinkAt, DateTime? CompletedAt);

/// <summary>The Alloggiati communication of the stay.</summary>
/// <param name="Stored">The stored status of the report that stands for the booking; <c>null</c> when it has none yet.</param>
/// <param name="ReportedAt">The time it was sent or declared sent.</param>
public sealed record BookingTimelineAlloggiati(AlloggiatiWebStatus? Stored, DateTime? ReportedAt);

public sealed record BookingTimelineFacts(
    IReadOnlyList<BookingTimelinePayment> Payments,
    IReadOnlyList<BookingTimelineRefund> Refunds,
    BookingTimelineCheckIn CheckIn,
    BookingTimelineAlloggiati Alloggiati);

/// <summary>
/// The timeline of a booking (SR-05a, SR-D17): what happened, in order, and what is waiting right now, derived from the timestamps the
/// booking keeps and from the rows tied to it. There is no event log: a booking from before <see cref="Booking.ConfirmedAt"/>,
/// <see cref="Booking.CancelledAt"/> and <see cref="Booking.CheckedOutAt"/> shows those steps without a time, never with an invented
/// one. The steps still to come (the link to send, the arrival, the departure) are not entries. Pure: nothing is read here, the caller
/// says what "now" is.
/// </summary>
public static class BookingTimeline
{
    /// <summary>
    /// The entries of <paramref name="booking"/> in the order of the stay, by time. An entry without a time takes its place from the date
    /// it belongs to.
    /// </summary>
    /// <param name="booking">The booking itself (its own columns: no property, no guest).</param>
    /// <param name="facts">The rows tied to it.</param>
    /// <param name="now">The instant that decides what is due: today is the Europe/Rome day of it.</param>
    /// <param name="includeAmounts">True for a caller who may read payments: without it an entry says that money moved, never how much.</param>
    public static IReadOnlyList<BookingTimelineEvent> Build(
        Booking booking,
        BookingTimelineFacts facts,
        DateTimeOffset now,
        bool includeAmounts)
    {
        ArgumentNullException.ThrowIfNull(booking);
        ArgumentNullException.ThrowIfNull(facts);

        var entries = new List<Entry>();

        void Add(BookingTimelineKind kind, BookingTimelineState state, DateTime? at, DateTime sortAt, BookingTimelineData? data = null) =>
            entries.Add(new Entry(new BookingTimelineEvent(kind, at, state, data ?? new BookingTimelineData()), sortAt));

        var isRequest = booking.Source == BookingSource.Direct && booking.PaymentOption == PaymentOption.OnSite;
        Add(
            isRequest ? BookingTimelineKind.RequestReceived : BookingTimelineKind.Created,
            BookingTimelineState.Done,
            booking.CreatedAt,
            booking.CreatedAt,
            new BookingTimelineData { Source = booking.Source });

        if (booking.Status == BookingStatus.Pending)
            AddPending(booking, now.UtcDateTime, Add);

        var collected = facts.Payments
            .Where(p => p.Status is PaymentStatus.Completed or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded)
            .ToList();
        foreach (var payment in collected)
        {
            Add(
                BookingTimelineKind.PaymentReceived,
                BookingTimelineState.Done,
                payment.At,
                payment.At,
                new BookingTimelineData { Amount = includeAmounts ? payment.Amount : null, Method = payment.Method });
        }

        foreach (var refund in facts.Refunds.Where(r => r.Status != PaymentRefundStatus.Canceled))
        {
            Add(
                BookingTimelineKind.Refund,
                refund.Status == PaymentRefundStatus.Succeeded ? BookingTimelineState.Done : BookingTimelineState.Current,
                refund.At,
                refund.At,
                new BookingTimelineData { RefundStatus = refund.Status, Amount = includeAmounts ? refund.Amount : null });
        }

        // A stay confirmed before the time was kept is still confirmed: the step is there, without a time. One cancelled before that
        // had no confirmation to show unless it was confirmed on record.
        var isStay = booking.Status is BookingStatus.Confirmed or BookingStatus.CheckedIn or BookingStatus.CheckedOut;
        if (isStay || booking.ConfirmedAt is not null)
        {
            var firstPayment = collected.Count == 0 ? (DateTime?)null : collected.Min(p => p.At);
            Add(
                BookingTimelineKind.Confirmed,
                BookingTimelineState.Done,
                booking.ConfirmedAt,
                booking.ConfirmedAt ?? firstPayment ?? booking.CreatedAt);
        }

        if (isStay)
        {
            var arrived = booking.Status != BookingStatus.Confirmed;
            AddCheckInLink(booking, facts.CheckIn, arrived, Add);

            if (arrived)
            {
                Add(
                    BookingTimelineKind.Arrived,
                    BookingTimelineState.Done,
                    booking.ArrivedAt,
                    booking.ArrivedAt ?? booking.CheckInDate);
            }

            AddAlloggiati(booking, facts.Alloggiati, RomeCalendar.TodayAt(now), Add);

            if (booking.Status == BookingStatus.CheckedOut)
            {
                Add(
                    BookingTimelineKind.CheckedOut,
                    BookingTimelineState.Done,
                    booking.CheckedOutAt,
                    booking.CheckedOutAt ?? booking.CheckOutDate);
            }
        }

        if (booking.Status == BookingStatus.Cancelled)
        {
            Add(
                BookingTimelineKind.Cancelled,
                BookingTimelineState.Done,
                booking.CancelledAt,
                booking.CancelledAt ?? booking.UpdatedAt,
                new BookingTimelineData { Reason = booking.CancellationReason, Note = booking.CancellationNote });
        }

        // By time, then by the order of the stay for two entries at the same instant. LINQ ordering is stable: entries of the same kind
        // and time keep the order they were added in.
        return entries
            .OrderBy(e => e.SortAt)
            .ThenBy(e => e.Event.Kind)
            .Select(e => e.Event)
            .ToList();
    }

    private static void AddPending(
        Booking booking,
        DateTime nowUtc,
        Action<BookingTimelineKind, BookingTimelineState, DateTime?, DateTime, BookingTimelineData?> add)
    {
        switch (OnSiteRequests.StateOf(booking, nowUtc))
        {
            case OnSiteRequestState.AwaitingGuestEmail:
                add(BookingTimelineKind.GuestEmailPending, BookingTimelineState.Current, null, booking.CreatedAt,
                    new BookingTimelineData { ExpiresAt = booking.RequestExpiresAt });
                break;

            case OnSiteRequestState.AwaitingHostApproval:
                add(BookingTimelineKind.ApprovalPending, BookingTimelineState.Current, booking.GuestEmailVerifiedAt,
                    booking.GuestEmailVerifiedAt ?? booking.CreatedAt, new BookingTimelineData { ExpiresAt = booking.RequestExpiresAt });
                break;

            case null when !OnSiteRequests.IsOpenRequest(booking):
                add(
                    booking.Source == BookingSource.Direct ? BookingTimelineKind.PaymentPending : BookingTimelineKind.HostConfirmationPending,
                    BookingTimelineState.Current,
                    null,
                    booking.CreatedAt,
                    null);
                break;
        }
    }

    private static void AddCheckInLink(
        Booking booking,
        BookingTimelineCheckIn checkIn,
        bool arrived,
        Action<BookingTimelineKind, BookingTimelineState, DateTime?, DateTime, BookingTimelineData?> add)
    {
        switch (checkIn.State)
        {
            case HostTodayCheckInState.Completed:
                if (checkIn.LinkAt is { } sentAt)
                {
                    add(BookingTimelineKind.CheckInLinkSent, BookingTimelineState.Done, sentAt, sentAt,
                        new BookingTimelineData { LinkState = HostTodayCheckInState.Completed });
                }

                add(BookingTimelineKind.GuestDocumentsReceived, BookingTimelineState.Done, checkIn.CompletedAt,
                    checkIn.CompletedAt ?? checkIn.LinkAt ?? booking.CreatedAt, null);
                break;

            case HostTodayCheckInState.Sent or HostTodayCheckInState.InProgress or HostTodayCheckInState.Expired:
                add(
                    BookingTimelineKind.CheckInLinkSent,
                    arrived ? BookingTimelineState.Done : BookingTimelineState.Current,
                    checkIn.LinkAt,
                    checkIn.LinkAt ?? booking.CreatedAt,
                    new BookingTimelineData { LinkState = checkIn.State });
                break;
        }
    }

    // The Alloggiati Web communication from the arrival day, as honestly as the Alloggiati page tells it: what is not sent is "to send
    // manually" (CasaZen never transmits); sent only with a receipt or the host's declaration. Before the arrival day it is a step still to come,
    // and a no-show (NF-45) owes none: there is no entry.
    private static void AddAlloggiati(
        Booking booking,
        BookingTimelineAlloggiati alloggiati,
        DateTime today,
        Action<BookingTimelineKind, BookingTimelineState, DateTime?, DateTime, BookingTimelineData?> add)
    {
        var status = AlloggiatiStatusRules.Effective(alloggiati.Stored, booking.CheckInDate, today, booking.NoShowAt is not null);
        if (status is AlloggiatiWebStatus.DaInviare or AlloggiatiWebStatus.NotDue)
            return;

        var arrival = booking.ArrivedAt ?? booking.CheckInDate;
        if (AlloggiatiStatusRules.IsSent(status))
        {
            var sortAt = alloggiati.ReportedAt is { } reportedAt && reportedAt > arrival ? reportedAt : arrival;
            add(BookingTimelineKind.Alloggiati, BookingTimelineState.Done, alloggiati.ReportedAt, sortAt,
                new BookingTimelineData { AlloggiatiStatus = status });
            return;
        }

        add(
            BookingTimelineKind.Alloggiati,
            BookingTimelineState.Current,
            null,
            arrival,
            new BookingTimelineData { AlloggiatiStatus = status, DeadlineAt = AlloggiatiTerms.DeadlineUtc(booking) });
    }

    private sealed record Entry(BookingTimelineEvent Event, DateTime SortAt);
}
