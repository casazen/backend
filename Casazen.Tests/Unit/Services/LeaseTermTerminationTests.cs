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
/// LR-TERM: an early termination ends a lease that is running, on a day after it began and no later than the end of the period it is in
/// (<c>start &lt; last day ≤ end</c>), and only once the lease has begun. A day equal to the start is not an ending (it left a lease whose
/// start, end and "terminated on" were the same day), a day past the end is not one, and a lease that has not begun is cancelled by a
/// consensual resolution, not ended. The refusal is 422 <c>lease_termination_date_invalid</c> with the first and last day that would do
/// (<c>min</c>, <c>max</c>) and writes nothing; the day that is accepted fixes the end of the period and leaves the end the contract
/// states as it was. "Today" in Rome is 10 October 2026.
/// </summary>
public class LeaseTermTerminationTests : IDisposable
{
    private readonly PropertyModeHarness _h = new();
    private readonly Mock<IRentBillingService> _rent = new();

    public LeaseTermTerminationTests() =>
        _rent.Setup(r => r.CancelInstallmentsAfterAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RentInstallmentsCancelled(0, []));

    public void Dispose() => _h.Dispose();

    private static DateTime Day(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A registered lease of a 4+4 contract that began on 1 November 2025 and states its first end on 31 October 2029.</summary>
    private async Task<LeaseContract> RunningLeaseAsync(DateTime? start = null)
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        return await _h.SeedLeaseAsync(property, start ?? Day(2025, 11, 1), Day(2029, 10, 31));
    }

    private async Task TerminateAsync(Guid leaseId, DateOnly on)
    {
        await using var db = _h.SecondContext();
        await new LeaseTermService(db, _rent.Object, NullLogger<LeaseTermService>.Instance, _h.Clock).TerminateAsync(leaseId, on);
    }

    private async Task<LeaseContract> LoadAsync(Guid leaseId)
    {
        await using var db = _h.SecondContext();
        return await db.LeaseContracts.AsNoTracking().SingleAsync(l => l.Id == leaseId);
    }

    [Theory]
    [InlineData(2025, 11, 1)] // the day the lease began: the lease would begin and end the same day
    [InlineData(2025, 10, 15)] // before it began
    [InlineData(2029, 11, 1)] // the day after the period ends
    public async Task ADayThatIsNotBetweenTheStartExcludedAndTheEnd_IsRefused_WithTheDaysThatWouldDo_AndNothingIsWritten(int year, int month, int day)
    {
        var lease = await RunningLeaseAsync();

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => TerminateAsync(lease.Id, new DateOnly(year, month, day)));

        Assert.Equal("lease_termination_date_invalid", refused.Code);
        Assert.Equal("LeaseTerminationDateInvalid", refused.MessageKey);
        Assert.Equal(new DateOnly(2025, 11, 2), refused.Extensions["min"]);
        Assert.Equal(new DateOnly(2029, 10, 31), refused.Extensions["max"]);
        var untouched = await LoadAsync(lease.Id);
        Assert.Null(untouched.TerminatedOn);
        Assert.Equal(Day(2029, 10, 31), untouched.CurrentTermEndDate);
        _rent.Verify(r => r.CancelInstallmentsAfterAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ALeaseThatHasNotBegun_IsRefused_WhateverTheDay_AndTheTextSaysWhatCancelsIt()
    {
        var lease = await RunningLeaseAsync(start: Day(2026, 11, 1)); // today is 10 October 2026

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => TerminateAsync(lease.Id, new DateOnly(2027, 3, 31)));

        Assert.Equal("lease_termination_date_invalid", refused.Code);
        Assert.Equal("LeaseTerminationNotStarted", refused.MessageKey);
        Assert.Equal(LongRentRules.Current.RegistrationDays, Assert.Single(refused.MessageArgs));
        Assert.Equal(new DateOnly(2026, 11, 2), refused.Extensions["min"]);
        Assert.Null((await LoadAsync(lease.Id)).TerminatedOn);
    }

    [Theory]
    [InlineData(2025, 11, 2)] // the day after it began: the first day that is an ending
    [InlineData(2026, 6, 30)] // a day in the past, for a lease the landlord knows ended earlier
    [InlineData(2027, 12, 31)]
    [InlineData(2029, 10, 31)] // the last day of the period
    public async Task ADayAfterTheStartAndNotPastTheEnd_EndsTheLease_TheEndOfTheContractStaysAsItWas(int year, int month, int day)
    {
        var lease = await RunningLeaseAsync();
        var last = new DateOnly(year, month, day);

        await TerminateAsync(lease.Id, last);

        var ended = await LoadAsync(lease.Id);
        var lastDay = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(lastDay, ended.TerminatedOn);
        Assert.Equal(lastDay, ended.CurrentTermEndDate);
        Assert.Equal(Day(2029, 10, 31), ended.EndDate); // the contract still says when it was to end
        Assert.Equal(Day(2025, 11, 1), ended.StartDate);
        _rent.Verify(r => r.CancelInstallmentsAfterAsync(lease.Id, last, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void ALeaseWhoseEndIsNotAfterItsStart_HasNoEndToOrganize_ButOneThatRunsHas()
    {
        var today = new DateOnly(2026, 10, 9);
        var degenerate = new LeaseFlowInput
        {
            Today = today,
            Status = LeaseStatus.Registered,
            ContractType = LeaseContractType.Libero,
            StartDate = new DateOnly(2026, 11, 1),
            TerminatedOn = new DateOnly(2026, 11, 1),
            CurrentTermEndDate = new DateOnly(2026, 11, 1),
            HasActiveRentPlan = true,
        };

        Assert.NotEqual(LeaseNextStepCode.OrganizeEnd, LeaseFlow.Derive(degenerate).NextStep.Code);
        Assert.Equal(
            LeaseNextStepCode.OrganizeEnd,
            LeaseFlow.Derive(degenerate with { StartDate = new DateOnly(2025, 11, 1), TerminatedOn = new DateOnly(2027, 1, 31), CurrentTermEndDate = new DateOnly(2027, 1, 31) })
                .NextStep.Code);
    }
}
