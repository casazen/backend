using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// LR-OPS-1b: the landlord records the renewal of a lease before the end of its period. The new period is checked against the other leases of
/// the property (LR-OVERLAP, 409 <c>lease_period_overlaps</c>, nothing written), the end of the period moves forward by the months of the type or
/// to the day the parties agreed on, the rent plan is extended in the same step, and a lease with a notice, ended, or whose period is over
/// is refused. "Today" in Rome is 10 October 2026.
/// </summary>
public class LeaseRenewalTests : IDisposable
{
    private readonly PropertyModeHarness _h = new();
    private readonly Mock<IRentBillingService> _rent = new();

    public LeaseRenewalTests() =>
        _rent.Setup(r => r.ExtendScheduleToCurrentTermAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);

    public void Dispose() => _h.Dispose();

    private static DateTime Day(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A registered 4+4 lease of a property that began on 1 November 2022 and ends on 31 October 2026, three weeks from today.</summary>
    private async Task<(Property Property, LeaseContract Lease)> LeaseAsync()
    {
        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        return (property, await _h.SeedLeaseAsync(property, Day(2022, 11, 1), Day(2026, 10, 31)));
    }

    private async Task RenewAsync(Guid leaseId, DateOnly? agreedEnd = null)
    {
        await using var db = _h.SecondContext();
        await new LeaseTermService(db, _rent.Object, NullLogger<LeaseTermService>.Instance, _h.Clock).RecordRenewalAsync(leaseId, agreedEnd);
    }

    private async Task<LeaseContract> LoadAsync(Guid leaseId)
    {
        await using var db = _h.SecondContext();
        return await db.LeaseContracts.AsNoTracking().Include(l => l.Events).SingleAsync(l => l.Id == leaseId);
    }

    [Fact]
    public async Task TheStandardRenewal_MovesTheEndByTheMonthsOfTheType_ExtendsTheRentPlanAndWritesTheEvent()
    {
        var (_, lease) = await LeaseAsync();

        await RenewAsync(lease.Id);

        var renewed = await LoadAsync(lease.Id);
        Assert.Equal(Day(2030, 10, 31), renewed.CurrentTermEndDate); // 48 months more, the end the contract states does not move
        Assert.Equal(Day(2026, 10, 31), renewed.EndDate);
        Assert.Equal("Standard:2026-11-01:2030-10-31", Assert.Single(renewed.Events, e => e.EventType == LeaseEventType.RenewalRecorded).Payload);
        _rent.Verify(r => r.ExtendScheduleToCurrentTermAsync(lease.Id, It.IsAny<CancellationToken>()), Times.Once);

        // The end moved forward, so the last stretch before the next end is years away: a second click cannot add another period.
        Assert.Equal("lease_renewal_not_yet", (await Assert.ThrowsAsync<DomainRuleException>(() => RenewAsync(lease.Id))).Code);
        Assert.Equal(Day(2030, 10, 31), (await LoadAsync(lease.Id)).CurrentTermEndDate);
    }

    [Fact]
    public async Task ARenewalThatWouldCoverTheDaysOfAnotherLeaseOfTheProperty_IsRefused_AndNothingIsWritten()
    {
        var (property, lease) = await LeaseAsync();
        var next = await _h.SeedLeaseAsync(property, Day(2027, 6, 1), Day(2031, 5, 31)); // the next tenant, who starts inside the renewed period

        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => RenewAsync(lease.Id));

        Assert.Equal("lease_period_overlaps", refused.Code);
        Assert.Equal(next.Id, refused.Extensions["conflictingLeaseId"]);
        var untouched = await LoadAsync(lease.Id);
        Assert.Equal(Day(2026, 10, 31), untouched.CurrentTermEndDate);
        Assert.DoesNotContain(untouched.Events, e => e.EventType == LeaseEventType.RenewalRecorded);
        _rent.Verify(r => r.ExtendScheduleToCurrentTermAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnExtensionAgreedOnADay_MustEndAfterThePeriod_AndIsRecordedAsAgreed()
    {
        var (_, lease) = await LeaseAsync();

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => RenewAsync(lease.Id, new DateOnly(2026, 10, 31))); // not after the end of the period
        Assert.Equal("lease_renewal_end_invalid", refused.Code);
        Assert.Null((await LoadAsync(lease.Id)).Events.SingleOrDefault(e => e.EventType == LeaseEventType.RenewalRecorded));

        await RenewAsync(lease.Id, new DateOnly(2028, 4, 30));

        var extended = await LoadAsync(lease.Id);
        Assert.Equal(Day(2028, 4, 30), extended.CurrentTermEndDate);
        Assert.Equal("Agreed:2026-11-01:2028-04-30", Assert.Single(extended.Events, e => e.EventType == LeaseEventType.RenewalRecorded).Payload);
    }

    [Fact]
    public async Task ALeaseWithANotice_OneNotYetNearItsEnd_AnEndedOne_OneWhosePeriodIsOver_AndATransitoryOne_AreRefused()
    {
        var (_, withNotice) = await LeaseAsync();
        await using (var db = _h.SecondContext())
        {
            var tracked = await db.LeaseContracts.SingleAsync(l => l.Id == withNotice.Id);
            tracked.NoticeOn = Day(2026, 5, 1);
            tracked.NoticeFrom = LeaseNoticeParty.Landlord;
            await db.SaveChangesAsync();
        }

        Assert.Equal("lease_renewal_blocked_by_notice", (await Assert.ThrowsAsync<DomainConflictException>(() => RenewAsync(withNotice.Id))).Code);

        // Not before the last stretch of the period: a lease that ends in 2029 has nothing to formalize in October 2026.
        var early = await _h.SeedLeaseAsync(await _h.SeedPropertyAsync(RentalMode.Long), Day(2025, 11, 1), Day(2029, 10, 31));
        Assert.Equal("lease_renewal_not_yet", (await Assert.ThrowsAsync<DomainRuleException>(() => RenewAsync(early.Id))).Code);

        var property = await _h.SeedPropertyAsync(RentalMode.Long);
        var ended = await _h.SeedLeaseAsync(property, Day(2022, 11, 1), Day(2026, 10, 31), terminatedOn: Day(2026, 3, 31));
        Assert.Equal("lease_already_terminated", (await Assert.ThrowsAsync<DomainConflictException>(() => RenewAsync(ended.Id))).Code);

        var over = await _h.SeedLeaseAsync(await _h.SeedPropertyAsync(RentalMode.Long), Day(2022, 7, 1), Day(2026, 6, 30)); // the hourly job renews this one
        Assert.Equal("lease_renewal_period_over", (await Assert.ThrowsAsync<DomainConflictException>(() => RenewAsync(over.Id))).Code);

        var transitory = await _h.SeedLeaseAsync(await _h.SeedPropertyAsync(RentalMode.Long), Day(2026, 5, 1), Day(2026, 12, 31));
        await using (var db = _h.SecondContext())
        {
            (await db.LeaseContracts.SingleAsync(l => l.Id == transitory.Id)).ContractType = LeaseContractType.Transitorio;
            await db.SaveChangesAsync();
        }

        Assert.Equal("lease_type_has_no_renewal", (await Assert.ThrowsAsync<DomainRuleException>(() => RenewAsync(transitory.Id))).Code);
        _rent.Verify(r => r.ExtendScheduleToCurrentTermAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
