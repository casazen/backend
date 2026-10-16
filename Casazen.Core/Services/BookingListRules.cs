using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Utilities;
using Casazen.Core.Validation;

namespace Casazen.Core.Services;

/// <summary>
/// The quick filters (chips) of the booking list (SR-03b); <see cref="All"/> is no filter. The chips never overlap: a booking is in
/// at most one of them (<see cref="BookingListRules.InView"/>), so the counters of the chips never count a booking twice. A booking
/// that is in none (a confirmed stay whose arrival day went by without the arrival being registered, a request whose guest has not
/// confirmed the email, an expired hold) shows under <see cref="All"/> only.
/// </summary>
public enum BookingListView
{
    /// <summary>Every booking.</summary>
    All,

    /// <summary>"Da confermare": a "pay at the property" request the guest confirmed and the host has still to answer.</summary>
    ToConfirm,

    /// <summary>"In arrivo": a confirmed booking, or one waiting for its payment, that arrives after today.</summary>
    Upcoming,

    /// <summary>"In corso": a guest who has arrived, or a confirmed booking that arrives today.</summary>
    InProgress,

    /// <summary>"Concluse": checked out.</summary>
    Completed,

    /// <summary>"Cancellate": cancelled.</summary>
    Cancelled,
}

/// <summary>
/// Where a booking came from, in the words of the channel filter (SR-03b): <see cref="BookingSource"/> grouped as the host reads it.
/// The exact source stays on the booking.
/// </summary>
public enum BookingChannel
{
    /// <summary>The booking site (<see cref="BookingSource.Direct"/>).</summary>
    Direct,

    /// <summary>Entered by the host (<see cref="BookingSource.Manual"/>).</summary>
    Manual,

    /// <summary>Airbnb.</summary>
    Airbnb,

    /// <summary>Booking.com.</summary>
    BookingCom,

    /// <summary>Any other channel: Expedia, Vrbo, Tripadvisor, Agoda, a local one.</summary>
    Other,
}

/// <summary>How much of what a booking costs has come in (SR-03b).</summary>
public enum BookingPaymentState
{
    /// <summary>Nothing came in (and nothing was returned).</summary>
    None,

    /// <summary>Something came in, less than the total.</summary>
    Partial,

    /// <summary>The total came in.</summary>
    Paid,

    /// <summary>What came in was returned: nothing is left.</summary>
    Refunded,
}

/// <summary>What the booking list is ordered by (SR-03b).</summary>
public enum BookingSortKey
{
    /// <summary>The check-in date.</summary>
    CheckIn,

    /// <summary>When the booking was made.</summary>
    CreatedAt,

    /// <summary>The total price.</summary>
    Total,

    /// <summary>The status, in the order of the stay: pending, confirmed, checked in, checked out, cancelled.</summary>
    Status,

    /// <summary>The guest, by last name then first name, any case.</summary>
    Guest,
}

/// <summary>An order of the booking list: the key and the direction. The booking id always follows, as the last criterion.</summary>
public sealed record BookingSort(BookingSortKey Key, bool Descending = false);

/// <summary>One payment of a booking, as far as its state is concerned (<see cref="BookingListRules.PaymentStateOf"/>).</summary>
public readonly record struct BookingPaymentFact(PaymentStatus Status, decimal Amount, decimal RefundedAmount);

/// <summary>
/// The rules of the booking list that are not the shape of a query (SR-03b): the chips, the channel, how much has been paid, and how
/// the filters and the order are written in a request. Each is defined once, here: the list, the counters of the chips and the tests
/// read these.
/// </summary>
public static class BookingListRules
{
    // --- The chips ----------------------------------------------------------------------------------

    /// <summary>
    /// The bookings of a chip on <paramref name="todayInRome"/> (SR-03b); <see cref="BookingListView.All"/> keeps every booking.
    /// Days are Europe/Rome calendar dates (<see cref="RomeCalendar"/>, like <see cref="StayKpiRules"/>), so there is no clock in the
    /// rule: the caller says what today is.
    /// <list type="bullet">
    /// <item><see cref="BookingListView.ToConfirm"/>: a "pay at the property" request waiting for the host
    /// (<see cref="OnSiteRequests.IsAwaitingHostApproval"/>).</item>
    /// <item><see cref="BookingListView.Cancelled"/>: <see cref="BookingStatus.Cancelled"/>. <see cref="BookingListView.Completed"/>:
    /// <see cref="BookingStatus.CheckedOut"/>.</item>
    /// <item><see cref="BookingListView.InProgress"/>: <see cref="BookingStatus.CheckedIn"/>, or <see cref="BookingStatus.Confirmed"/>
    /// whose check-in date is today (until the host registers the arrival).</item>
    /// <item><see cref="BookingListView.Upcoming"/>: <see cref="BookingStatus.Confirmed"/>, or <see cref="BookingStatus.Pending"/>
    /// waiting for its payment (not a "pay at the property" request, not an expired hold: <see cref="CheckoutHolds.IsExpired"/>), whose
    /// check-in date is after today.</item>
    /// </list>
    /// </summary>
    public static Expression<Func<Booking, bool>> InView(BookingListView view, DateTime todayInRome, HoldExpiryCutoff cutoff)
    {
        var todayStart = RomeCalendar.StartOfDayUtc(todayInRome.Date);
        var tomorrowStart = RomeCalendar.StartOfDayUtc(todayInRome.Date.AddDays(1));

        switch (view)
        {
            case BookingListView.ToConfirm:
                return OnSiteRequests.IsAwaitingHostApproval(cutoff.NowUtc);

            case BookingListView.Cancelled:
                return b => b.Status == BookingStatus.Cancelled;

            case BookingListView.Completed:
                return b => b.Status == BookingStatus.CheckedOut;

            case BookingListView.InProgress:
                return b => b.Status == BookingStatus.CheckedIn
                    || (b.Status == BookingStatus.Confirmed && b.CheckInDate >= todayStart && b.CheckInDate < tomorrowStart);

            case BookingListView.Upcoming:
                Expression<Func<Booking, bool>> arrivesAfterToday = b => b.CheckInDate >= tomorrowStart;
                Expression<Func<Booking, bool>> confirmed = b => b.Status == BookingStatus.Confirmed;
                Expression<Func<Booking, bool>> waitingForPayment = b =>
                    b.Status == BookingStatus.Pending && b.PaymentOption != PaymentOption.OnSite;
                return And(arrivesAfterToday, Or(confirmed, And(waitingForPayment, Not(CheckoutHolds.IsExpired(cutoff)))));

            default:
                return b => true;
        }
    }

    // --- The channel --------------------------------------------------------------------------------

    /// <summary>The channel of a source: the booking site, the host, Airbnb, Booking.com, or any other.</summary>
    public static BookingChannel ChannelOf(BookingSource source) => source switch
    {
        BookingSource.Direct => BookingChannel.Direct,
        BookingSource.Manual => BookingChannel.Manual,
        BookingSource.Airbnb => BookingChannel.Airbnb,
        BookingSource.BookingCom => BookingChannel.BookingCom,
        _ => BookingChannel.Other,
    };

    /// <summary>The sources of the channels: what a channel filter asks the database for.</summary>
    public static IReadOnlyList<BookingSource> SourcesOf(IEnumerable<BookingChannel> channels)
    {
        var wanted = channels.ToHashSet();
        return Enum.GetValues<BookingSource>().Where(source => wanted.Contains(ChannelOf(source))).ToList();
    }

    // --- The money ----------------------------------------------------------------------------------

    /// <summary>
    /// What came in for the booking: the payments that are collected (<see cref="PaymentCashRules.CollectedBetweenInstants"/>: completed
    /// or partly refunded) minus what was refunded, before any OTA withholding, as the Home counts the money collected.
    /// </summary>
    public static decimal PaidAmount(IEnumerable<BookingPaymentFact> payments) => payments
        .Where(p => p.Status is PaymentStatus.Completed or PaymentStatus.PartiallyRefunded)
        .Sum(p => PaymentCashRules.Collected(p.Amount, p.RefundedAmount));

    /// <summary>
    /// How much of <paramref name="totalPrice"/> has come in: <see cref="BookingPaymentState.Paid"/> when all of it,
    /// <see cref="BookingPaymentState.Partial"/> when part, <see cref="BookingPaymentState.Refunded"/> when something came in and all
    /// of it was returned, <see cref="BookingPaymentState.None"/> when nothing ever did (a failed or open payment is nothing).
    /// </summary>
    public static BookingPaymentState PaymentStateOf(decimal totalPrice, IReadOnlyCollection<BookingPaymentFact> payments)
    {
        var paid = PaidAmount(payments);
        if (paid <= 0m)
            return payments.Any(p => p.Status == PaymentStatus.Refunded || p.RefundedAmount > 0m)
                ? BookingPaymentState.Refunded
                : BookingPaymentState.None;

        return paid >= totalPrice ? BookingPaymentState.Paid : BookingPaymentState.Partial;
    }

    // --- The words of a request ---------------------------------------------------------------------

    /// <summary>
    /// The chip named by <paramref name="value"/> (any case, one name); no value is <see cref="BookingListView.All"/>. False for
    /// anything else, numbers and lists included.
    /// </summary>
    public static bool TryParseView(string? value, out BookingListView view)
    {
        view = BookingListView.All;
        return string.IsNullOrWhiteSpace(value) || EnumNames.TryParseDefined(value, out view);
    }

    /// <summary>
    /// The members of <typeparamref name="TEnum"/> named by <paramref name="values"/>, repeated or separated by commas, by name and any
    /// case, without repeats; none is an empty list. False for anything else, numbers included (a number would bind to a member that
    /// does not exist).
    /// </summary>
    public static bool TryParseNames<TEnum>(IEnumerable<string>? values, out IReadOnlyList<TEnum> parsed) where TEnum : struct, Enum
    {
        var list = new List<TEnum>();
        parsed = list;
        foreach (var token in (values ?? []).SelectMany(
                     v => (v ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)))
        {
            if (!EnumNames.TryParseDefined<TEnum>(token, out var member))
                return false;

            if (!list.Contains(member))
                list.Add(member);
        }

        return true;
    }

    /// <summary>
    /// The order named by <paramref name="value"/>: a <see cref="BookingSortKey"/> and, after a colon, <c>asc</c> or <c>desc</c> (any
    /// case; <c>asc</c> when missing), like <c>checkIn:desc</c>. No value is no order (<paramref name="sort"/> is null and the list
    /// keeps its own: the latest check-in first). False for anything else.
    /// </summary>
    public static bool TryParseSort(string? value, out BookingSort? sort)
    {
        sort = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var parts = value.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length > 2 || !EnumNames.TryParseDefined<BookingSortKey>(parts[0], out var key))
            return false;

        var descending = false;
        if (parts.Length == 2)
        {
            if (string.Equals(parts[1], "desc", StringComparison.OrdinalIgnoreCase))
                descending = true;
            else if (!string.Equals(parts[1], "asc", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        sort = new BookingSort(key, descending);
        return true;
    }

    // --- Predicates over one booking, joined ---------------------------------------------------------

    /// <summary>One predicate that holds when any of <paramref name="predicates"/> does; false for none.</summary>
    public static Expression<Func<Booking, bool>> AnyOf(IEnumerable<Expression<Func<Booking, bool>>> predicates)
    {
        var list = predicates.ToList();
        return list.Count == 0 ? b => false : list.Aggregate(Or);
    }

    /// <summary>One predicate that holds when both do.</summary>
    public static Expression<Func<Booking, bool>> And(Expression<Func<Booking, bool>> left, Expression<Func<Booking, bool>> right) =>
        Join(left, right, Expression.AndAlso);

    /// <summary>One predicate that holds when either does.</summary>
    public static Expression<Func<Booking, bool>> Or(Expression<Func<Booking, bool>> left, Expression<Func<Booking, bool>> right) =>
        Join(left, right, Expression.OrElse);

    /// <summary>One predicate that holds when <paramref name="predicate"/> does not.</summary>
    public static Expression<Func<Booking, bool>> Not(Expression<Func<Booking, bool>> predicate) =>
        Expression.Lambda<Func<Booking, bool>>(Expression.Not(predicate.Body), predicate.Parameters);

    private static Expression<Func<Booking, bool>> Join(
        Expression<Func<Booking, bool>> left,
        Expression<Func<Booking, bool>> right,
        Func<Expression, Expression, BinaryExpression> combine)
    {
        var booking = left.Parameters[0];
        var rightBody = new ParameterReplacer(right.Parameters[0], booking).Visit(right.Body)!;
        return Expression.Lambda<Func<Booking, bool>>(combine(left.Body, rightBody), booking);
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
