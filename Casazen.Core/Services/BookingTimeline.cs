using Casazen.Core.Entities;
using Casazen.Core.Regulatory;
using Casazen.Core.Utilities;

namespace Casazen.Core.Services;

/// <summary>
/// What an entry of the timeline of a booking is about (SR-05a). The titles are keys: the words are the client's (IT and EN). The order
/// of the members is the order of a stay and settles two entries at the same instant.
/// </summary>
public enum BookingTimelineKind
{
    /// <summary>The booking was made (<see cref="BookingTimelineData.Source"/> says where from).</summary>
    Created,

    /// <summary>A "pay at the property" request was received: the booking is a request the host answers.</summary>
    RequestReceived,

    /// <summary>The request waits for the guest to confirm the email; <see cref="BookingTimelineData.ExpiresAt"/> is the term.</summary>
    GuestEmailPending,

    /// <summary>The request waits for the host's answer; <see cref="BookingTimelineData.ExpiresAt"/> is the term.</summary>
    ApprovalPending,

    /// <summary>The booking site's checkout waits for the guest's payment.</summary>
    PaymentPending,

    /// <summary>A booking entered by the host waits for the host's confirmation.</summary>
    HostConfirmationPending,

    /// <summary>A payment came in (<see cref="BookingTimelineData.Amount"/> only for a caller who may read payments).</summary>
    PaymentReceived,

    /// <summary>The booking is confirmed: its dates are blocked on every calendar.</summary>
    Confirmed,

    /// <summary>The link of the online check-in went out (<see cref="BookingTimelineData.LinkState"/> says where it stands).</summary>
    CheckInLinkSent,

    /// <summary>The guest submitted the data of the online check-in.</summary>
    GuestDocumentsReceived,

    /// <summary>The host registered the arrival of the guest.</summary>
    Arrived,

    /// <summary>
    /// The Alloggiati Web communication, from the arrival day, as it honestly stands (<see cref="BookingTimelineData.AlloggiatiStatus"/>):
    /// never "sent" without a receipt or the host's declaration. It follows the arrival.
    /// </summary>
    Alloggiati,

    /// <summary>The stay was checked out.</summary>
    CheckedOut,

    /// <summary>The booking was cancelled (<see cref="BookingTimelineData.Reason"/>: by the system or declined; <see cref="BookingTimelineData.Note"/>: the host's).</summary>
    Cancelled,

    /// <summary>A refund of a payment (<see cref="BookingTimelineData.RefundStatus"/>).</summary>
    Refund,
}

/// <summary>Where an entry of the timeline stands.</summary>
public enum BookingTimelineState
{
    /// <summary>It happened.</summary>
    Done,

    /// <summary>It is happening: something waits for the host, the guest or the bank.</summary>
    Current,
}

/// <summary>
/// What an entry of the timeline says besides its kind: each field belongs to a few kinds and is <c>null</c> for the others. Only
/// codes, amounts and dates: no name and no email of anyone.
/// </summary>
public sealed record BookingTimelineData
{
    /// <summary>Where the booking came from (<see cref="BookingTimelineKind.Created"/>, <see cref="BookingTimelineKind.RequestReceived"/>).</summary>
    public BookingSource? Source { get; init; }

    /// <summary>Euros, for a caller who may read payments (<see cref="BookingTimelineKind.PaymentReceived"/>, <see cref="BookingTimelineKind.Refund"/>).</summary>
    public decimal? Amount { get; init; }

    /// <summary>How it was paid (<see cref="BookingTimelineKind.PaymentReceived"/>).</summary>
    public PaymentMethod? Method { get; init; }

    /// <summary>The term of a request that waits (UTC).</summary>
    public DateTime? ExpiresAt { get; init; }

    /// <summary>
    /// Why a booking was cancelled by the system (or the request the host declined), or <see cref="BookingCancellationReason.GuestRequested"/>
    /// when the guest cancelled it themselves; <c>null</c> for the host's cancellation.
    /// </summary>
    public BookingCancellationReason? Reason { get; init; }

    /// <summary>The reason the host wrote when cancelling; host only, never sent to the guest.</summary>
    public string? Note { get; init; }

    /// <summary><see cref="HostTodayCheckInState.Sent"/>, <see cref="HostTodayCheckInState.InProgress"/>, <see cref="HostTodayCheckInState.Expired"/> or <see cref="HostTodayCheckInState.Completed"/>.</summary>
    public HostTodayCheckInState? LinkState { get; init; }

    /// <summary>The Alloggiati Web status as the host reads it.</summary>
    public AlloggiatiWebStatus? AlloggiatiStatus { get; init; }

    /// <summary>The legal term of the Alloggiati communication (UTC) while it is not sent.</summary>
    public DateTime? DeadlineAt { get; init; }

    /// <summary>Where the refund stands.</summary>
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

/// <summary>A payment of the booking, as far as the timeline is concerned.</summary>
/// <param name="At">When it came in: <see cref="Payment.ProcessedAt"/>, else <see cref="Payment.CreatedAt"/> (as the fiscal reports read it).</param>
public sealed record BookingTimelinePayment(PaymentStatus Status, PaymentMethod Method, decimal Amount, DateTime At);

/// <summary>A refund of a payment of the booking.</summary>
/// <param name="At">When it succeeded (<see cref="PaymentRefund.CompletedAt"/>), else when it was asked.</param>
public sealed record BookingTimelineRefund(PaymentRefundStatus Status, decimal Amount, DateTime At);

/// <summary>Where the online check-in of the stay stands (the state of <see cref="HostTodayCheckInState"/>).</summary>
/// <param name="State">The state of the links of the stay, as the Home reads it.</param>
/// <param name="LinkAt">When the link that counts went out; <c>null</c> when none was usable.</param>
/// <param name="CompletedAt">When the guest submitted the data; <c>null</c> before.</param>
public sealed record BookingTimelineCheckIn(HostTodayCheckInState State, DateTime? LinkAt, DateTime? CompletedAt);

/// <summary>The Alloggiati communication of the stay.</summary>
/// <param name="Stored">The stored status of the report that stands for the booking; <c>null</c> when it has none yet.</param>
/// <param name="ReportedAt">The time it was sent or declared sent.</param>
public sealed record BookingTimelineAlloggiati(AlloggiatiWebStatus? Stored, DateTime? ReportedAt);

/// <summary>What the timeline of a booking is built from, besides the booking: the rows of the other tables that tell its history.</summary>
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

        // The birth of the booking: a request for the host to answer, or a booking.
        var isRequest = booking.Source == BookingSource.Direct && booking.PaymentOption == PaymentOption.OnSite;
        Add(
            isRequest ? BookingTimelineKind.RequestReceived : BookingTimelineKind.Created,
            BookingTimelineState.Done,
            booking.CreatedAt,
            booking.CreatedAt,
            new BookingTimelineData { Source = booking.Source });

        if (booking.Status == BookingStatus.Pending)
            AddPending(booking, now.UtcDateTime, Add);

        // Money that came in, and went back. Only what Stripe or the host recorded as collected counts as received.
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

    // A booking that is not confirmed yet: what it waits for.
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

            // A request past its term waits for the job that cancels it: nothing to say. Any other pending booking waits for its payment
            // (the checkout of the booking site) or for the host (a booking the host entered).
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

    // The link of the online check-in that went out: waiting for the guest before the arrival, or the guest's data received. A link that
    // was never sent is a step still to come, not an entry; once the guest is there the link is no longer something the host waits on.
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
    // manually" (CasaZen never transmits); sent only with a receipt or the host's declaration. Before the arrival day it is a step still to come.
    private static void AddAlloggiati(
        Booking booking,
        BookingTimelineAlloggiati alloggiati,
        DateTime today,
        Action<BookingTimelineKind, BookingTimelineState, DateTime?, DateTime, BookingTimelineData?> add)
    {
        var status = AlloggiatiStatusRules.Effective(alloggiati.Stored, booking.CheckInDate, today);
        if (status == AlloggiatiWebStatus.DaInviare)
            return;

        // The communication follows the arrival, whatever time the declaration of the host carries.
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
