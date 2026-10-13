using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Leases;

/// <summary>LR-01, B2: the last day of notice, the limits of the agenda, and the frequency limit of the reminders.</summary>
public class LongRentDeadlineRulesTests
{
    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(LeaseContractType.Libero, 2028, 8, 31, 2028, 2, 29)] // six months before 31 August 2028, a leap year: 29 February
    [InlineData(LeaseContractType.Libero, 2027, 5, 31, 2026, 11, 30)]
    [InlineData(LeaseContractType.Libero, 2027, 3, 31, 2026, 9, 30)] // 31 March: September has 30 days
    [InlineData(LeaseContractType.Libero, 2026, 8, 31, 2026, 2, 28)]
    [InlineData(LeaseContractType.Concordato, 2029, 11, 30, 2029, 5, 30)]
    [InlineData(LeaseContractType.Concordato, 2026, 12, 31, 2026, 6, 30)]
    public void NoticeDate_ALeaseWithARenewalToRefuse_IsSixMonthsBeforeTheEnd(
        LeaseContractType type, int endYear, int endMonth, int endDay, int year, int month, int day)
    {
        Assert.Equal(new DateOnly(year, month, day), LongRentDeadlineRules.NoticeDate(type, Utc(endYear, endMonth, endDay)));
    }

    [Fact]
    public void NoticeDate_ATransitoryLease_HasNone_ItEndsByItself()
    {
        Assert.Null(LongRentDeadlineRules.NoticeDate(LeaseContractType.Transitorio, Utc(2027, 2, 28)));
    }

    [Fact]
    public void NoticeDate_TakesTheRomeDayOfAnInstant()
    {
        // 31 May 2027, 22:30 UTC is already 1 June in Rome (summer time): six months before is 1 December.
        var instant = new DateTime(2027, 5, 31, 22, 30, 0, DateTimeKind.Utc);

        Assert.Equal(new DateOnly(2026, 12, 1), LongRentDeadlineRules.NoticeDate(LeaseContractType.Libero, instant));
    }

    [Fact]
    public void TheLimits_AreWhatTheSpecAndTheRunbookSay()
    {
        Assert.Equal(6, LongRentDeadlineRules.NoticeMonthsBeforeEnd);
        Assert.Equal(90, LongRentDeadlineRules.DefaultWindowDays);
        Assert.Equal(366, LongRentDeadlineRules.MaxWindowDays);
        Assert.Equal(500, LongRentDeadlineRules.MaxRentItems);
        Assert.Equal(24, RentCharges.DefaultReminderIntervalHours);
        Assert.Equal(300, RentCharges.MaxReminderNoteLength);
        Assert.Equal(50, RentCharges.MaxBulkReminders);
    }

    // --- The frequency limit ----------------------------------------------------------------------------

    private static IConfiguration Config(string? hours)
    {
        var values = new Dictionary<string, string?>();
        if (hours is not null)
            values["RentBilling:ReminderIntervalHours"] = hours;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Theory]
    [InlineData(null, 24)] // not configured: the default of the task
    [InlineData("24", 24)]
    [InlineData("1", 1)]
    [InlineData("72", 72)]
    [InlineData("720", 720)]
    [InlineData("0", 1)] // out of range: kept inside, never "no limit"
    [InlineData("-5", 1)]
    [InlineData("100000", 720)]
    [InlineData("", 24)]
    [InlineData("a day", 24)] // a typo in the environment is the default, not an error on every rent page
    [InlineData("2.5", 24)]
    public void ReminderIntervalHours_IsConfigurable_BetweenOneAndSevenHundredTwenty_DefaultTwentyFour(string? configured, int expected)
    {
        Assert.Equal(expected, RentCharges.GetReminderIntervalHours(Config(configured)));
    }

    [Fact]
    public void ReminderIntervalHours_IsReadFromTheSameSectionAsTheOtherRentSettings()
    {
        Assert.Equal("RentBilling:ReminderIntervalHours", RentCharges.ReminderIntervalHoursSetting);
        Assert.StartsWith("RentBilling:", RentCharges.RequestDaysBeforeDueSetting, StringComparison.Ordinal);
    }
}
