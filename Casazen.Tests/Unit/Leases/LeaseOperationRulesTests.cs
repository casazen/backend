using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Xunit;

namespace Casazen.Tests.Unit.Leases;

/// <summary>
/// LR-OPS-1: when the landlord's notice of non-renewal is on time and where a withdrawal of the tenant ends the lease. The months are the
/// rules' (<see cref="LongRentDeadlineRules.NoticeMonthsBeforeEnd"/>), counted back from the end of the period the lease is in.
/// </summary>
public class LeaseOperationRulesTests
{
    private static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static LeaseContract Lease(LeaseContractType type, DateTime end) => new()
    {
        Status = LeaseStatus.Registered,
        ContractType = type,
        StartDate = Utc(2025, 11, 1),
        EndDate = end,
    };

    [Theory]
    [InlineData(LeaseContractType.Libero, 2029, 10, 31, 2029, 4, 30)]
    [InlineData(LeaseContractType.Concordato, 2028, 10, 31, 2028, 4, 30)]
    public void Disdetta_IsOnTimeUpToTheNoticeMonthsBeforeTheEndOfThePeriod_AndLateTheDayAfter(
        LeaseContractType type, int endYear, int endMonth, int endDay, int lastYear, int lastMonth, int lastDay)
    {
        var lease = Lease(type, Utc(endYear, endMonth, endDay));
        var last = new DateOnly(lastYear, lastMonth, lastDay);

        var onTime = LeaseOperationRules.Disdetta(lease, last);
        var late = LeaseOperationRules.Disdetta(lease, last.AddDays(1));

        Assert.True(onTime.Available);
        Assert.Equal(last, onTime.LastDayToGiveNotice);
        Assert.False(onTime.Late);
        Assert.True(late.Available);
        Assert.True(late.Late);
        Assert.True(onTime.FirstPeriod);
        Assert.Equal(new DateOnly(endYear, endMonth, endDay), onTime.PeriodEnd);
    }

    [Fact]
    public void Disdetta_ATransitoryLeaseHasNoNotice_AndALeaseThatRenewedIsNotInItsFirstPeriod()
    {
        var transitory = LeaseOperationRules.Disdetta(Lease(LeaseContractType.Transitorio, Utc(2026, 11, 30)), new DateOnly(2026, 10, 10));
        Assert.False(transitory.Available);
        Assert.Null(transitory.LastDayToGiveNotice);

        var renewed = Lease(LeaseContractType.Libero, Utc(2029, 10, 31));
        renewed.CurrentTermEndDate = Utc(2033, 10, 31); // the tacit renewal moved the end of the period, not the end the contract states
        var facts = LeaseOperationRules.Disdetta(renewed, new DateOnly(2030, 1, 1));
        Assert.False(facts.FirstPeriod);
        Assert.Equal(new DateOnly(2033, 4, 30), facts.LastDayToGiveNotice);
    }

    [Fact]
    public void Disdetta_IsNotOfferedOnceTheLeaseIsEndedOrTheTenantHasGivenNotice()
    {
        var today = new DateOnly(2026, 10, 10);
        var ended = Lease(LeaseContractType.Libero, Utc(2029, 10, 31));
        ended.TerminatedOn = Utc(2027, 3, 31);
        Assert.False(LeaseOperationRules.Disdetta(ended, today).Available);

        var tenantGaveNotice = Lease(LeaseContractType.Libero, Utc(2029, 10, 31));
        tenantGaveNotice.NoticeOn = Utc(2026, 5, 1);
        tenantGaveNotice.NoticeFrom = LeaseNoticeParty.Tenant;
        Assert.False(LeaseOperationRules.Disdetta(tenantGaveNotice, today).Available);

        var notSigned = Lease(LeaseContractType.Libero, Utc(2029, 10, 31));
        notSigned.Status = LeaseStatus.Draft;
        Assert.False(LeaseOperationRules.Disdetta(notSigned, today).Available);
    }

    [Theory]
    [InlineData(2026, 10, 1, 6, 2027, 4, 1)]
    [InlineData(2026, 8, 31, 6, 2027, 2, 28)] // the day that does not exist in the shorter month is its last day
    [InlineData(2026, 10, 1, 3, 2027, 1, 1)]
    public void WithdrawalEffectiveOn_IsTheDayTheNoticeRunsOut(int year, int month, int day, int months, int endYear, int endMonth, int endDay) =>
        Assert.Equal(new DateOnly(endYear, endMonth, endDay), LeaseOperationRules.WithdrawalEffectiveOn(new DateOnly(year, month, day), months));

    [Fact]
    public void WithdrawalNoticeMonths_SeriousReasonsAlwaysTakeTheRulesNotice_TheContractualFacultyTakesTheOneTyped()
    {
        Assert.Equal(LongRentDeadlineRules.NoticeMonthsBeforeEnd, LeaseOperationRules.WithdrawalNoticeMonths(TenantWithdrawalReason.GraveMotivi, 2));
        Assert.Equal(LongRentDeadlineRules.NoticeMonthsBeforeEnd, LeaseOperationRules.WithdrawalNoticeMonths(TenantWithdrawalReason.ContractualFaculty, null));
        Assert.Equal(3, LeaseOperationRules.WithdrawalNoticeMonths(TenantWithdrawalReason.ContractualFaculty, 3));
    }
}
