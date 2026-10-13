using Casazen.Core.Entities;
using Casazen.Core.Regulatory;
using Casazen.Core.Utilities;
using Xunit;

namespace Casazen.Tests.Unit.Regulatory;

/// <summary>CO-11 (A5-37): arrival day in Europe/Rome, 24-hour and 6-hour terms of art. 109 TULPS.</summary>
public class AlloggiatiTermsTests
{
    [Theory]
    [InlineData(2026, 3, 29, 2026, 3, 28, 23)] // day of the switch to summer time: midnight is still CET
    [InlineData(2026, 3, 30, 2026, 3, 29, 22)] // first day of CEST
    [InlineData(2026, 10, 25, 2026, 10, 24, 22)] // day of the switch back: midnight is still CEST
    [InlineData(2026, 10, 26, 2026, 10, 25, 23)] // first day of CET
    public void StartOfDayUtc_AroundDaylightSavingChanges_ReturnsRomeMidnight(
        int year, int month, int day, int utcYear, int utcMonth, int utcDay, int utcHour)
    {
        var start = RomeCalendar.StartOfDayUtc(new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(utcYear, utcMonth, utcDay, utcHour, 0, 0, DateTimeKind.Utc), start);
        Assert.Equal(DateTimeKind.Utc, start.Kind);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(14, false)]
    public void IsShortStay_ByNights_AppliesSixHoursUpToOneNight(int nights, bool expected)
    {
        var checkIn = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(expected, AlloggiatiTerms.IsShortStay(checkIn, checkIn.AddDays(nights)));
    }

    [Fact]
    public void DeadlineUtc_NoArrivalRecorded_RunsFromRomeMidnightOfCheckIn()
    {
        var booking = new Booking
        {
            CheckInDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc),
        };

        Assert.Equal(new DateTime(2026, 7, 1, 22, 0, 0, DateTimeKind.Utc), AlloggiatiTerms.DeadlineUtc(booking));
    }

    [Fact]
    public void DeadlineUtc_ShortStayWithArrival_IsSixHoursAfterArrival()
    {
        var arrivedAt = new DateTime(2026, 7, 1, 16, 30, 0, DateTimeKind.Utc);
        var booking = new Booking
        {
            CheckInDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc),
            ArrivedAt = arrivedAt,
        };

        Assert.Equal(arrivedAt.AddHours(6), AlloggiatiTerms.DeadlineUtc(booking));
    }

    [Theory]
    [InlineData(null, 2026, 7, 1, AlloggiatiWebStatus.DaInviare)]
    [InlineData(null, 2026, 7, 2, AlloggiatiWebStatus.DaInviareManualmente)]
    [InlineData(AlloggiatiWebStatus.DaInviare, 2026, 7, 2, AlloggiatiWebStatus.DaInviareManualmente)]
    [InlineData(AlloggiatiWebStatus.InviatoManualmente, 2026, 7, 5, AlloggiatiWebStatus.InviatoManualmente)]
    [InlineData(AlloggiatiWebStatus.Errore, 2026, 7, 5, AlloggiatiWebStatus.Errore)]
    public void Effective_StoredStatusAndTodayInRome_NeverTurnsDoneOnItsOwn(
        AlloggiatiWebStatus? stored, int year, int month, int day, AlloggiatiWebStatus expected)
    {
        var checkIn = new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc);
        var today = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(expected, AlloggiatiStatusRules.Effective(stored, checkIn, today));
    }
}
