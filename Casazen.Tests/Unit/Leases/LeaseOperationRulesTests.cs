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

    [Fact]
    public void Renewal_IsOfferedWhileThePeriodRuns_WithTheStandardEndOfTheType_AndNotWhenItCannotBeRecorded()
    {
        var today = new DateOnly(2026, 10, 10);

        var libero = LeaseOperationRules.Renewal(Lease(LeaseContractType.Libero, Utc(2026, 10, 31)), today);
        Assert.True(libero.Available);
        Assert.Equal(new DateOnly(2026, 11, 1), libero.StartsOn);
        Assert.Equal(new DateOnly(2030, 10, 31), libero.StandardEnd);
        Assert.Equal(LongRentRules.LiberoRenewalMonths, libero.StandardMonths);
        Assert.Equal(new DateOnly(2026, 4, 30), libero.OpensOn); // the last day to give notice: from then the parties decide

        // Before that stretch there is nothing to formalize: a renewal moves the end forward, so it cannot be recorded twice in a row.
        var early = LeaseOperationRules.Renewal(Lease(LeaseContractType.Libero, Utc(2029, 10, 31)), today);
        Assert.False(early.Available);
        Assert.Equal(new DateOnly(2029, 4, 30), early.OpensOn);

        var concordato = LeaseOperationRules.Renewal(Lease(LeaseContractType.Concordato, Utc(2026, 10, 31)), today);
        Assert.Equal(new DateOnly(2028, 10, 31), concordato.StandardEnd);
        Assert.Equal(LongRentRules.ConcordatoRenewalMonths, concordato.StandardMonths);

        // A transitory lease never renews; a notice has to be taken back first; a period that is over is the hourly job's; an ended lease is ended.
        Assert.False(LeaseOperationRules.Renewal(Lease(LeaseContractType.Transitorio, Utc(2026, 12, 31)), today).Available);
        var withNotice = Lease(LeaseContractType.Libero, Utc(2026, 10, 31));
        withNotice.NoticeOn = Utc(2026, 4, 1);
        withNotice.NoticeFrom = LeaseNoticeParty.Landlord;
        Assert.False(LeaseOperationRules.Renewal(withNotice, today).Available);
        Assert.False(LeaseOperationRules.Renewal(Lease(LeaseContractType.Libero, Utc(2026, 9, 30)), today).Available);
        var ended = Lease(LeaseContractType.Libero, Utc(2026, 10, 31));
        ended.TerminatedOn = Utc(2026, 8, 31);
        Assert.False(LeaseOperationRules.Renewal(ended, today).Available);
    }

    [Fact]
    public void TransitoryConfirmation_IsTheLandlordsOnlyWhenTheNeedIsHis_AndHasToBeSentBeforeTheLastDay()
    {
        var today = new DateOnly(2026, 10, 10);
        var lease = Lease(LeaseContractType.Transitorio, Utc(2026, 12, 31));
        lease.TransitoryParty = PartyRole.Landlord;

        var landlords = LeaseOperationRules.TransitoryConfirmation(lease, today);
        Assert.True(landlords.Applies);
        Assert.True(landlords.Available);
        Assert.Equal(PartyRole.Landlord, landlords.Holder);
        Assert.Equal(new DateOnly(2026, 12, 31), landlords.ConfirmBy);

        // When the need is the tenant's the tenant confirms it: the lease applies, the landlord has no letter.
        lease.TransitoryParty = PartyRole.Tenant;
        var tenants = LeaseOperationRules.TransitoryConfirmation(lease, today);
        Assert.True(tenants.Applies);
        Assert.False(tenants.Available);

        // A libero lease has no need to confirm.
        Assert.False(LeaseOperationRules.TransitoryConfirmation(Lease(LeaseContractType.Libero, Utc(2029, 10, 31)), today).Applies);
    }
}
