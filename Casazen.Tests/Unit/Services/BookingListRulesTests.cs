using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SR-03b: the rules of the booking list that need no database. The chips (each booking in at most one, and the right one), the
/// channel, how much has been paid, and how the filters and the order of a request are written. Today is 9 October 2026 in Rome.
/// </summary>
public class BookingListRulesTests
{
    private static readonly DateTimeOffset NowOffset = HostScopeScenario.Now;
    private static readonly DateTime Now = NowOffset.UtcDateTime;
    private static readonly DateTime Today = RomeCalendar.TodayAt(NowOffset);

    private static DateTime Day(int month, int day) => new(2026, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static Booking Make(
        BookingStatus status,
        DateTime checkIn,
        BookingSource source = BookingSource.Direct,
        PaymentOption option = PaymentOption.Immediate)
    {
        return new Booking
        {
            Status = status,
            Source = source,
            PaymentOption = option,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(3),
            CreatedAt = Now.AddMinutes(-10),
        };
    }

    private static bool InView(Booking booking, BookingListView view)
    {
        var cutoff = CheckoutHolds.CutoffAt(Now, CheckoutHolds.DefaultTtlMinutes);
        return BookingListRules.InView(view, Today, cutoff).Compile()(booking);
    }

    private static readonly BookingListView[] Chips =
        Enum.GetValues<BookingListView>().Where(view => view != BookingListView.All).ToArray();

    // --- The chips ----------------------------------------------------------------------------------------

    private static Booking RequestForTheHost(bool emailConfirmed = true, DateTime? expiresAt = null)
    {
        var request = Make(BookingStatus.Pending, Day(10, 20), option: PaymentOption.OnSite);
        request.GuestEmailVerifiedAt = emailConfirmed ? Now.AddHours(-1) : null;
        request.RequestExpiresAt = expiresAt ?? Now.AddHours(20);
        return request;
    }

    private static Booking HoldWaitingForPayment(DateTime createdAt)
    {
        var hold = Make(BookingStatus.Pending, Day(10, 20));
        hold.StripeSetupIntentId = "seti_test";
        hold.CreatedAt = createdAt;
        return hold;
    }

    public static TheoryData<string, BookingListView> EveryKindOfBooking() => new()
    {
        { "confirmed, arrives after today", BookingListView.Upcoming },
        { "confirmed, arrives tomorrow", BookingListView.Upcoming },
        { "confirmed, arrives today", BookingListView.InProgress },
        { "confirmed, arrived but not registered", BookingListView.All },
        { "checked in", BookingListView.InProgress },
        { "checked in, arrived today", BookingListView.InProgress },
        { "checked out", BookingListView.Completed },
        { "cancelled", BookingListView.Cancelled },
        { "cancelled, arrives today", BookingListView.Cancelled },
        { "hold waiting for the payment", BookingListView.Upcoming },
        { "hold waiting for the payment, expired", BookingListView.All },
        { "pending, entered by the host before they were confirmed", BookingListView.Upcoming },
        { "pending, arrives today", BookingListView.All },
        { "request waiting for the host", BookingListView.ToConfirm },
        { "request waiting for the guest's email", BookingListView.All },
        { "request past its term", BookingListView.All },
    };

    private static Booking BuildKind(string name) => name switch
    {
        "confirmed, arrives after today" => Make(BookingStatus.Confirmed, Day(10, 20)),
        "confirmed, arrives tomorrow" => Make(BookingStatus.Confirmed, Day(10, 10)),
        "confirmed, arrives today" => Make(BookingStatus.Confirmed, Day(10, 9)),
        "confirmed, arrived but not registered" => Make(BookingStatus.Confirmed, Day(10, 7)),
        "checked in" => Make(BookingStatus.CheckedIn, Day(10, 7)),
        "checked in, arrived today" => Make(BookingStatus.CheckedIn, Day(10, 9)),
        "checked out" => Make(BookingStatus.CheckedOut, Day(10, 1)),
        "cancelled" => Make(BookingStatus.Cancelled, Day(10, 20)),
        "cancelled, arrives today" => Make(BookingStatus.Cancelled, Day(10, 9)),
        "hold waiting for the payment" => HoldWaitingForPayment(Now.AddMinutes(-10)),
        "hold waiting for the payment, expired" => HoldWaitingForPayment(Now.AddHours(-2)),
        "pending, entered by the host before they were confirmed" => Make(BookingStatus.Pending, Day(10, 20), BookingSource.Manual),
        "pending, arrives today" => Make(BookingStatus.Pending, Day(10, 9)),
        "request waiting for the host" => RequestForTheHost(),
        "request waiting for the guest's email" => RequestForTheHost(emailConfirmed: false),
        "request past its term" => RequestForTheHost(expiresAt: Now.AddMinutes(-1)),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    [Theory]
    [MemberData(nameof(EveryKindOfBooking))]
    public void InView_EveryBookingIsInTheChipItBelongsTo_AndInNoOther(string kind, BookingListView expectedChip)
    {
        var booking = BuildKind(kind);

        var chipsOfTheBooking = Chips.Where(chip => InView(booking, chip)).ToList();

        Assert.Equal(
            expectedChip == BookingListView.All ? Array.Empty<BookingListView>() : new[] { expectedChip },
            chipsOfTheBooking);
        // Everything is under "all".
        Assert.True(InView(booking, BookingListView.All));
    }

    [Fact]
    public void InView_TodayIsTheRomeDay_NotTheUtcOne()
    {
        // 23:30 UTC of the 9th is half past one of the 10th in Rome (summer time): a stay that arrives "tomorrow" (the 10th) arrives today.
        var lateUtc = new DateTimeOffset(2026, 10, 9, 23, 30, 0, TimeSpan.Zero);
        var today = RomeCalendar.TodayAt(lateUtc);
        var cutoff = CheckoutHolds.CutoffAt(lateUtc.UtcDateTime, CheckoutHolds.DefaultTtlMinutes);
        var arrivesOnThe10th = Make(BookingStatus.Confirmed, Day(10, 10));

        Assert.Equal(Day(10, 10), today);
        Assert.True(BookingListRules.InView(BookingListView.InProgress, today, cutoff).Compile()(arrivesOnThe10th));
        Assert.False(BookingListRules.InView(BookingListView.Upcoming, today, cutoff).Compile()(arrivesOnThe10th));
    }

    [Fact]
    public void InView_ADateStoredWithATime_IsOnItsRomeDay()
    {
        // 22:30 UTC of the 9th is half past midnight of the 10th in Rome (summer time): that arrival is on the 10th.
        var arrival = Make(BookingStatus.Confirmed, new DateTime(2026, 10, 9, 22, 30, 0, DateTimeKind.Utc));

        Assert.False(InView(arrival, BookingListView.InProgress));
        Assert.True(InView(arrival, BookingListView.Upcoming));
    }

    // --- Predicates joined ---------------------------------------------------------------------------------

    [Fact]
    public void AnyOf_And_Or_Not_JoinPredicatesOverTheSameBooking()
    {
        Expression<Func<Booking, bool>> confirmed = b => b.Status == BookingStatus.Confirmed;
        Expression<Func<Booking, bool>> arrivesTomorrow = b => b.CheckInDate == Day(10, 10);
        var booking = Make(BookingStatus.Confirmed, Day(10, 10));
        var other = Make(BookingStatus.Cancelled, Day(10, 11));

        Assert.True(BookingListRules.And(confirmed, arrivesTomorrow).Compile()(booking));
        Assert.False(BookingListRules.And(confirmed, arrivesTomorrow).Compile()(other));
        Assert.True(BookingListRules.Or(confirmed, arrivesTomorrow).Compile()(booking));
        Assert.False(BookingListRules.Or(confirmed, arrivesTomorrow).Compile()(other));
        Assert.False(BookingListRules.Not(confirmed).Compile()(booking));
        Assert.True(BookingListRules.AnyOf([confirmed, arrivesTomorrow]).Compile()(booking));
        Assert.False(BookingListRules.AnyOf([]).Compile()(booking));
    }

    // --- The channel ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(BookingSource.Direct, BookingChannel.Direct)]
    [InlineData(BookingSource.Manual, BookingChannel.Manual)]
    [InlineData(BookingSource.Airbnb, BookingChannel.Airbnb)]
    [InlineData(BookingSource.BookingCom, BookingChannel.BookingCom)]
    [InlineData(BookingSource.Expedia, BookingChannel.Other)]
    [InlineData(BookingSource.Vrbo, BookingChannel.Other)]
    [InlineData(BookingSource.TripAdvisor, BookingChannel.Other)]
    [InlineData(BookingSource.Agoda, BookingChannel.Other)]
    [InlineData(BookingSource.Local, BookingChannel.Other)]
    public void ChannelOf_TheSourceAsTheHostReadsIt(BookingSource source, BookingChannel expected)
    {
        Assert.Equal(expected, BookingListRules.ChannelOf(source));
        Assert.Contains(source, BookingListRules.SourcesOf([expected]));
    }

    [Fact]
    public void SourcesOf_TheChannelsPartitionTheSources_SoNoSourceIsLostOrCountedTwice()
    {
        var all = Enum.GetValues<BookingChannel>().SelectMany(channel => BookingListRules.SourcesOf([channel])).ToList();

        Assert.Equal(Enum.GetValues<BookingSource>().Order(), all.Order());
        Assert.Equal([BookingSource.Direct, BookingSource.Manual], BookingListRules.SourcesOf([BookingChannel.Direct, BookingChannel.Manual]).Order());
        Assert.Empty(BookingListRules.SourcesOf([]));
    }

    // --- What has been paid ---------------------------------------------------------------------------------

    private static BookingPaymentFact Pay(PaymentStatus status, decimal amount, decimal refunded = 0m) => new(status, amount, refunded);

    [Fact]
    public void PaymentStateOf_NothingCameIn_NoneEvenWithPaymentsThatFailedOrAreOpen()
    {
        Assert.Equal(BookingPaymentState.None, BookingListRules.PaymentStateOf(300m, []));
        Assert.Equal(
            BookingPaymentState.None,
            BookingListRules.PaymentStateOf(
                300m, [Pay(PaymentStatus.Failed, 300m), Pay(PaymentStatus.Pending, 300m), Pay(PaymentStatus.Processing, 300m), Pay(PaymentStatus.Canceled, 300m)]));
    }

    [Fact]
    public void PaymentStateOf_PartAndAll_AndMoreThanTheTotal()
    {
        Assert.Equal(BookingPaymentState.Partial, BookingListRules.PaymentStateOf(300m, [Pay(PaymentStatus.Completed, 90m)]));
        Assert.Equal(BookingPaymentState.Paid, BookingListRules.PaymentStateOf(300m, [Pay(PaymentStatus.Completed, 300m)]));
        Assert.Equal(
            BookingPaymentState.Paid,
            BookingListRules.PaymentStateOf(300m, [Pay(PaymentStatus.Completed, 90m), Pay(PaymentStatus.Completed, 210m), Pay(PaymentStatus.Failed, 50m)]));
        Assert.Equal(BookingPaymentState.Paid, BookingListRules.PaymentStateOf(300m, [Pay(PaymentStatus.Completed, 320m)]));
        Assert.Equal(BookingPaymentState.Paid, BookingListRules.PaymentStateOf(0m, [Pay(PaymentStatus.Completed, 10m)]));
    }

    [Fact]
    public void PaymentStateOf_WhatCameInAndWasReturned_IsRefunded_AndWhatWasReturnedInPartIsPartial()
    {
        Assert.Equal(BookingPaymentState.Refunded, BookingListRules.PaymentStateOf(300m, [Pay(PaymentStatus.Refunded, 300m, 300m)]));
        Assert.Equal(BookingPaymentState.Refunded, BookingListRules.PaymentStateOf(300m, [Pay(PaymentStatus.Completed, 300m, 300m)]));
        Assert.Equal(BookingPaymentState.Partial, BookingListRules.PaymentStateOf(300m, [Pay(PaymentStatus.PartiallyRefunded, 300m, 100m)]));
    }

    [Fact]
    public void PaidAmount_IsWhatIsCollected_MinusWhatWasRefunded_LikeTheMoneyOfTheHome()
    {
        var amount = BookingListRules.PaidAmount(
        [
            Pay(PaymentStatus.Completed, 300m),
            Pay(PaymentStatus.PartiallyRefunded, 200m, 50m),
            Pay(PaymentStatus.Refunded, 300m, 300m),
            Pay(PaymentStatus.Failed, 300m),
        ]);

        Assert.Equal(450m, amount);
    }

    // --- The words of a request -----------------------------------------------------------------------------

    [Theory]
    [InlineData(null, BookingListView.All)]
    [InlineData("", BookingListView.All)]
    [InlineData("  ", BookingListView.All)]
    [InlineData("all", BookingListView.All)]
    [InlineData("toConfirm", BookingListView.ToConfirm)]
    [InlineData("TOCONFIRM", BookingListView.ToConfirm)]
    [InlineData(" upcoming ", BookingListView.Upcoming)]
    [InlineData("inProgress", BookingListView.InProgress)]
    [InlineData("completed", BookingListView.Completed)]
    [InlineData("cancelled", BookingListView.Cancelled)]
    public void TryParseView_ByNameWhateverTheCase_NoneIsAll(string? text, BookingListView expected)
    {
        Assert.True(BookingListRules.TryParseView(text, out var view));
        Assert.Equal(expected, view);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("done")]
    [InlineData("upcoming,completed")]
    [InlineData("to confirm")]
    public void TryParseView_ANumberAListOrAnotherWord_IsNotAView(string text)
    {
        Assert.False(BookingListRules.TryParseView(text, out _));
    }

    [Theory]
    [InlineData(new[] { "Airbnb" }, new[] { BookingChannel.Airbnb })]
    [InlineData(new[] { "airbnb", "BOOKINGCOM" }, new[] { BookingChannel.Airbnb, BookingChannel.BookingCom })]
    [InlineData(new[] { "Direct,Manual" }, new[] { BookingChannel.Direct, BookingChannel.Manual })]
    [InlineData(new[] { " Other , Direct ", "Direct" }, new[] { BookingChannel.Other, BookingChannel.Direct })]
    [InlineData(new[] { "" }, new BookingChannel[0])]
    [InlineData(new string[0], new BookingChannel[0])]
    public void TryParseNames_RepeatedOrSeparatedByCommas_ByNameAndAnyCase_WithoutRepeats(string[] values, BookingChannel[] expected)
    {
        Assert.True(BookingListRules.TryParseNames<BookingChannel>(values, out var channels));

        Assert.Equal(expected, channels);
    }

    [Theory]
    [InlineData("Expedia")]
    [InlineData("1")]
    [InlineData("Airbnb,Foo")]
    [InlineData("airbnb booking")]
    public void TryParseNames_AWordThatIsNotAMember_RefusesTheWholeRequest(string value)
    {
        Assert.False(BookingListRules.TryParseNames<BookingChannel>([value], out _));
    }

    [Fact]
    public void TryParseNames_NothingAtAll_IsNoFilter()
    {
        Assert.True(BookingListRules.TryParseNames<HostTodayCheckInState>(null, out var states));
        Assert.Empty(states);
        Assert.True(BookingListRules.TryParseNames<HostTodayCheckInState>(["notsent,SENT"], out var some));
        Assert.Equal([HostTodayCheckInState.NotSent, HostTodayCheckInState.Sent], some);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("", null, false)]
    [InlineData("checkIn", BookingSortKey.CheckIn, false)]
    [InlineData("checkin:desc", BookingSortKey.CheckIn, true)]
    [InlineData("CREATEDAT:ASC", BookingSortKey.CreatedAt, false)]
    [InlineData("total:desc", BookingSortKey.Total, true)]
    [InlineData(" status : desc ", BookingSortKey.Status, true)]
    [InlineData("guest:asc", BookingSortKey.Guest, false)]
    public void TryParseSort_AKeyAndADirection_AscendingWhenTheDirectionIsMissing(string? text, BookingSortKey? key, bool descending)
    {
        Assert.True(BookingListRules.TryParseSort(text, out var sort));

        if (key is null)
            Assert.Null(sort);
        else
            Assert.Equal(new BookingSort(key.Value, descending), sort);
    }

    [Theory]
    [InlineData("price")]
    [InlineData("checkIn:up")]
    [InlineData("checkIn:desc:asc")]
    [InlineData("3")]
    [InlineData(":desc")]
    public void TryParseSort_AnythingElse_IsNotAnOrder(string text)
    {
        Assert.False(BookingListRules.TryParseSort(text, out _));
    }
}
