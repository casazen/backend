using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// LR-OPS-1: the landlord records the withdrawal (recesso) the tenant sent, and the lease ends on the day the notice runs out through the early
/// termination of LR-TERM, with its rules and its effects (<c>start &lt; last day ≤ end of the period</c>, the rent plan cancelled after it). The
/// refusal writes nothing. "Today" in Rome is 10 October 2026.
/// </summary>
public class LeaseWithdrawalTests : IDisposable
{
    private readonly PropertyModeHarness _h = new();
    private readonly Mock<IRentBillingService> _rent = new();

    public LeaseWithdrawalTests() =>
        _rent.Setup(r => r.CancelInstallmentsAfterAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RentInstallmentsCancelled(0, []));

    public void Dispose() => _h.Dispose();

    private static DateTime Day(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A registered 4+4 lease that began on 1 November 2025 and states its first end on <paramref name="end"/>.</summary>
    private async Task<LeaseContract> RunningLeaseAsync(DateTime? end = null)
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        return await _h.SeedLeaseAsync(property, Day(2025, 11, 1), end ?? Day(2029, 10, 31));
    }

    private async Task RecordAsync(Guid leaseId, DateOnly receivedOn, TenantWithdrawalReason reason = TenantWithdrawalReason.GraveMotivi, int? months = null)
    {
        await using var db = _h.SecondContext();
        await new LeaseTermService(db, _rent.Object, NullLogger<LeaseTermService>.Instance, _h.Clock)
            .RecordWithdrawalAsync(leaseId, receivedOn, reason, months);
    }

    private async Task<LeaseContract> LoadAsync(Guid leaseId)
    {
        await using var db = _h.SecondContext();
        return await db.LeaseContracts.AsNoTracking().Include(l => l.Events).SingleAsync(l => l.Id == leaseId);
    }

    [Fact]
    public async Task AWithdrawalReceived_EndsTheLeaseOnTheDayTheNoticeRunsOut_AndWritesBothEvents()
    {
        var lease = await RunningLeaseAsync();

        await RecordAsync(lease.Id, new DateOnly(2026, 10, 1));

        var ended = await LoadAsync(lease.Id);
        Assert.Equal(Day(2027, 4, 1), ended.TerminatedOn);
        Assert.Equal(Day(2027, 4, 1), ended.CurrentTermEndDate);
        Assert.Equal(Day(2029, 10, 31), ended.EndDate); // the contract still says when it was to end
        Assert.Equal("GraveMotivi:2026-10-01:2027-04-01:6", Assert.Single(ended.Events, e => e.EventType == LeaseEventType.WithdrawalRecorded).Payload);
        Assert.Equal("2027-04-01", Assert.Single(ended.Events, e => e.EventType == LeaseEventType.Terminated).Payload);
        _rent.Verify(r => r.CancelInstallmentsAfterAsync(lease.Id, new DateOnly(2027, 4, 1), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AWithdrawalWhoseNoticeRunsOutAfterThePeriod_IsRefusedLikeAnyEarlyTermination_AndNothingIsWritten()
    {
        var lease = await RunningLeaseAsync(end: Day(2027, 1, 31));

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => RecordAsync(lease.Id, new DateOnly(2026, 10, 10)));

        Assert.Equal("lease_termination_date_invalid", refused.Code);
        Assert.Equal(new DateOnly(2025, 11, 2), refused.Extensions["min"]);
        Assert.Equal(new DateOnly(2027, 1, 31), refused.Extensions["max"]);
        var untouched = await LoadAsync(lease.Id);
        Assert.Null(untouched.TerminatedOn);
        Assert.DoesNotContain(untouched.Events, e => e.EventType is LeaseEventType.WithdrawalRecorded or LeaseEventType.Terminated);
        _rent.Verify(r => r.CancelInstallmentsAfterAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(2026, 10, 11)] // a letter that has not arrived yet
    [InlineData(2025, 10, 31)] // before the lease began
    public async Task ADayOfReceiptInTheFutureOrBeforeTheStart_IsRefused(int year, int month, int day)
    {
        var lease = await RunningLeaseAsync();

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => RecordAsync(lease.Id, new DateOnly(year, month, day)));

        Assert.Equal("lease_withdrawal_date_invalid", refused.Code);
        Assert.Null((await LoadAsync(lease.Id)).TerminatedOn);
    }

    [Fact]
    public async Task TheContractualFaculty_TakesTheNoticeTheContractGives_ButSeriousReasonsAlwaysTakeTheRules()
    {
        var byContract = await RunningLeaseAsync();
        var serious = await RunningLeaseAsync();

        await RecordAsync(byContract.Id, new DateOnly(2026, 10, 1), TenantWithdrawalReason.ContractualFaculty, months: 3);
        await RecordAsync(serious.Id, new DateOnly(2026, 10, 1), TenantWithdrawalReason.GraveMotivi, months: 3);

        Assert.Equal(Day(2027, 1, 1), (await LoadAsync(byContract.Id)).TerminatedOn);
        Assert.Equal(Day(2027, 4, 1), (await LoadAsync(serious.Id)).TerminatedOn);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public async Task ANoticeOutsideOneToTwelveMonths_IsRefused(int months)
    {
        var lease = await RunningLeaseAsync();

        var refused = await Assert.ThrowsAsync<DomainRuleException>(
            () => RecordAsync(lease.Id, new DateOnly(2026, 10, 1), TenantWithdrawalReason.ContractualFaculty, months));

        Assert.Equal("lease_withdrawal_notice_invalid", refused.Code);
        Assert.Null((await LoadAsync(lease.Id)).TerminatedOn);
    }
}
