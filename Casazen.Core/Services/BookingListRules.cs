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
    All,

    ToConfirm,

    Upcoming,

    InProgress,

    Completed,

    Cancelled,
}

public enum BookingChannel
{
    Direct,

    Manual,

    Airbnb,

    BookingCom,

    Expedia,

    Vrbo,

    TripAdvisor,

    Agoda,

    Other,
}

public enum BookingPaymentState
{
    None,

    Partial,

    Paid,

    Refunded,
}

public enum BookingSortKey
{
    CheckIn,

    CreatedAt,

    Total,

    Status,

    Guest,
}

public sealed record BookingSort(BookingSortKey Key, bool Descending = false);

public readonly record struct BookingPaymentFact(PaymentStatus Status, decimal Amount, decimal RefundedAmount);

public static class BookingListRules
{
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

    public static BookingChannel ChannelOf(BookingSource source) => source switch
    {
        BookingSource.Direct => BookingChannel.Direct,
        BookingSource.Manual => BookingChannel.Manual,
        BookingSource.Airbnb => BookingChannel.Airbnb,
        BookingSource.BookingCom => BookingChannel.BookingCom,
        BookingSource.Expedia => BookingChannel.Expedia,
        BookingSource.Vrbo => BookingChannel.Vrbo,
        BookingSource.TripAdvisor => BookingChannel.TripAdvisor,
        BookingSource.Agoda => BookingChannel.Agoda,
        _ => BookingChannel.Other,
    };

    public static IReadOnlyList<BookingSource> SourcesOf(IEnumerable<BookingChannel> channels)
    {
        var wanted = channels.ToHashSet();
        return Enum.GetValues<BookingSource>().Where(source => wanted.Contains(ChannelOf(source))).ToList();
    }

    public static decimal PaidAmount(IEnumerable<BookingPaymentFact> payments) => payments
        .Where(p => p.Status is PaymentStatus.Completed or PaymentStatus.PartiallyRefunded)
        .Sum(p => PaymentCashRules.Collected(p.Amount, p.RefundedAmount));

    public static BookingPaymentState PaymentStateOf(decimal totalPrice, IReadOnlyCollection<BookingPaymentFact> payments)
    {
        var paid = PaidAmount(payments);
        if (paid <= 0m)
            return payments.Any(p => p.Status == PaymentStatus.Refunded || p.RefundedAmount > 0m)
                ? BookingPaymentState.Refunded
                : BookingPaymentState.None;

        return paid >= totalPrice ? BookingPaymentState.Paid : BookingPaymentState.Partial;
    }

    public static bool TryParseView(string? value, out BookingListView view)
    {
        view = BookingListView.All;
        return string.IsNullOrWhiteSpace(value) || EnumNames.TryParseDefined(value, out view);
    }

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

    public static Expression<Func<Booking, bool>> AnyOf(IEnumerable<Expression<Func<Booking, bool>>> predicates)
    {
        var list = predicates.ToList();
        return list.Count == 0 ? b => false : list.Aggregate(Or);
    }

    public static Expression<Func<Booking, bool>> And(Expression<Func<Booking, bool>> left, Expression<Func<Booking, bool>> right) =>
        Join(left, right, Expression.AndAlso);

    public static Expression<Func<Booking, bool>> Or(Expression<Func<Booking, bool>> left, Expression<Func<Booking, bool>> right) =>
        Join(left, right, Expression.OrElse);

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
