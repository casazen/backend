using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Xunit;

namespace Casazen.Tests.Unit.Regulatory;

public class RliRegistrationDeadlineTests
{
    [Theory]
    [InlineData("2026-08-01", "2026-10-01", "2026-08-31")]
    [InlineData("2026-09-20", "2026-09-01", "2026-10-01")]
    [InlineData("2026-09-01", "2026-09-01", "2026-10-01")]
    [InlineData("2028-02-10", "2028-03-01", "2028-03-11")]
    public void Compute_StipulaAndStart_ThirtyDaysFromTheEarlier(string stipula, string start, string expected)
    {
        var deadline = RliRegistrationDeadline.Compute(Date(stipula), Date(start));

        Assert.Equal(Date(expected), deadline);
        Assert.Equal(DateTimeKind.Utc, deadline.Kind);
    }

    [Fact]
    public void Compute_StipulaInstantAfterMidnightInRome_UsesRomeCalendarDay()
    {
        var deadline = RliRegistrationDeadline.Compute(
            new DateTime(2026, 7, 31, 22, 30, 0, DateTimeKind.Utc),
            Date("2026-10-01"));

        Assert.Equal(Date("2026-08-31"), deadline);
    }

    [Fact]
    public void Compute_StartStoredAsRomeMidnightInUtc_UsesRomeCalendarDay()
    {
        var deadline = RliRegistrationDeadline.Compute(
            Date("2026-09-20"),
            new DateTime(2026, 8, 31, 22, 0, 0, DateTimeKind.Utc));

        Assert.Equal(Date("2026-10-01"), deadline);
    }

    [Theory]
    [InlineData(LeaseStatus.Draft)]
    [InlineData(LeaseStatus.AwaitingSignature)]
    [InlineData(LeaseStatus.PartiallySigned)]
    public void Resolve_NotSignedYetStartReached_DeadlineFromStart(LeaseStatus status)
    {
        var deadline = RliRegistrationDeadline.Resolve(status, null, Date("2026-09-01"), Date("2026-09-24"));

        Assert.Equal(Date("2026-10-01"), deadline);
    }

    [Fact]
    public void Resolve_NotSignedYetStartDay_DeadlineFromStart()
    {
        var deadline = RliRegistrationDeadline.Resolve(LeaseStatus.Draft, null, Date("2026-09-24"), Date("2026-09-24"));

        Assert.Equal(Date("2026-10-24"), deadline);
    }

    [Fact]
    public void Resolve_NotSignedYetStartAhead_ToBeDetermined()
    {
        var deadline = RliRegistrationDeadline.Resolve(LeaseStatus.AwaitingSignature, null, Date("2026-10-01"), Date("2026-09-24"));

        Assert.Null(deadline);
    }

    [Theory]
    [InlineData(LeaseStatus.Signed)]
    [InlineData(LeaseStatus.RegistrationPending)]
    [InlineData(LeaseStatus.SentToProvider)]
    [InlineData(LeaseStatus.Registered)]
    public void Resolve_SignedWithoutStipulaDate_ToBeDeterminedNotGuessed(LeaseStatus status)
    {
        var deadline = RliRegistrationDeadline.Resolve(status, null, Date("2026-01-01"), Date("2026-09-24"));

        Assert.Null(deadline);
    }

    [Fact]
    public void Resolve_WithStipula_IgnoresToday()
    {
        var deadline = RliRegistrationDeadline.Resolve(LeaseStatus.Signed, Date("2026-08-01"), Date("2026-10-01"), Date("2026-07-01"));

        Assert.Equal(Date("2026-08-31"), deadline);
    }

    [Theory]
    [InlineData("2026-08-31", "2026-08-16", 15)]
    [InlineData("2026-08-31", "2026-08-31", 0)]
    [InlineData("2026-08-31", "2026-09-01", -1)]
    public void DaysRemaining_CalendarDays_ZeroOnTheDeadlineDay(string deadline, string today, int expected)
    {
        Assert.Equal(expected, RliRegistrationDeadline.DaysRemaining(Date(deadline), Date(today)));
    }

    /// <summary>
    /// LT-04 (PO 2026-10-08): the deadline is a calendar day. No shift to the next working day is applied even when
    /// the deadline falls on a Saturday, a Sunday or a public holiday.
    /// 2026-09-05 (Saturday) + 30 days = 2026-10-05 (Monday) — the stipula is a Saturday; deadline must be Monday
    /// 2026-09-19 (Saturday) + 30 days = 2026-10-19 (Monday) — use a stipula that falls on a Saturday to verify the
    /// result is also a Monday (a non-working day result stays as-is).
    ///
    /// More precisely: stipula 2026-09-06 (Sunday), start 2026-12-01 → deadline 2026-10-06 (Tuesday, not shifted).
    /// For Saturday-deadline: stipula 2026-08-07 (Friday), start 2026-10-01 → deadline = 2026-09-06 (Sunday, no shift).
    /// Let's use stipula 2026-09-05 (Saturday), start 2026-11-01 → deadline = 2026-10-05 (Monday). Still no shift,
    /// the computed day IS Monday so it's trivially correct.
    ///
    /// Best case: stipula that produces a Saturday deadline.
    /// 2026-08-08 (Saturday) + 30 = 2026-09-07 (Monday) — not Saturday.
    /// 2026-08-10 (Monday)   + 30 = 2026-09-09 (Wednesday).
    /// 2026-08-13 (Thursday) + 30 = 2026-09-12 (Saturday) ✓ — deadline must remain Saturday (no shift).
    /// </summary>
    [Fact]
    public void Compute_DeadlineFallsOnSaturday_NoShiftToNextWorkingDay()
    {
        var deadline = RliRegistrationDeadline.Compute(Date("2026-08-13"), Date("2026-10-01"));

        Assert.Equal(DayOfWeek.Saturday, deadline.DayOfWeek);
        Assert.Equal(Date("2026-09-12"), deadline);
    }

    [Theory]
    [InlineData(LeaseStatus.Draft, true)]
    [InlineData(LeaseStatus.AwaitingSignature, true)]
    [InlineData(LeaseStatus.PartiallySigned, true)]
    [InlineData(LeaseStatus.Signed, true)]
    [InlineData(LeaseStatus.RegistrationPending, true)]
    [InlineData(LeaseStatus.SentToProvider, true)]
    [InlineData(LeaseStatus.Registered, false)]
    [InlineData(LeaseStatus.Rejected, false)]
    public void AwaitsRegistration_EveryStatusBeforeRegistration_True(LeaseStatus status, bool expected)
    {
        Assert.Equal(expected, RliRegistrationDeadline.AwaitsRegistration(status));
    }

    [Fact]
    public void RecordStipula_SigningInstant_StoresRomeDateAndDeadline()
    {
        var lease = new LeaseContract { StartDate = Date("2026-10-01") };

        lease.RecordStipula(new DateTime(2026, 8, 1, 15, 45, 0, DateTimeKind.Utc));

        Assert.Equal(Date("2026-08-01"), lease.StipulaDate);
        Assert.Equal(TimeSpan.Zero, lease.StipulaDate!.Value.TimeOfDay);
        Assert.Equal(Date("2026-08-31"), lease.RegistrationDeadline);
    }

    private static DateTime Date(string value) =>
        DateTime.SpecifyKind(DateTime.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeKind.Utc);
}
