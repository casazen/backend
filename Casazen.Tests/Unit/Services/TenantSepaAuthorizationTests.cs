using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class TenantSepaAuthorizationTests
{
    private static LeaseContract Lease() => new()
    {
        Status = LeaseStatus.Registered,
        ContractType = LeaseContractType.Libero,
        StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        EndDate = new DateTime(2029, 12, 31, 0, 0, 0, DateTimeKind.Utc),
    };
    [Theory]
    [InlineData(LeaseContractType.Libero, 4)]
    [InlineData(LeaseContractType.Concordato, 2)]
    public void AuthorizationFollowsActualContractRenewal(LeaseContractType type, int years)
    {
        var lease = Lease(); lease.ContractType = type;
        var before = TenantSepaAuthorization.BlockedFrom(lease);
        lease.CurrentTermEndDate = LeaseRenewal.NextEnd(lease.CurrentTermEndDate, type)!.Value;
        Assert.Equal(before.AddYears(years), TenantSepaAuthorization.BlockedFrom(lease));
    }
    [Fact]
    public void EarlyTerminationBlocksFromFirstDayOfFollowingMonth()
    {
        var lease = Lease();
        lease.TerminatedOn = lease.CurrentTermEndDate = new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(TenantSepaAuthorization.CanDebit(lease, new(2026, 11, 30)));
        Assert.False(TenantSepaAuthorization.CanDebit(lease, new(2026, 12, 1)));
    }
    [Fact]
    public void AutomaticRequiresSeparateConsentAndNeverCollectsOldArrears()
    {
        var lease = Lease();
        var s = new RentSchedule
        {
            IsActive = true,
            Cadence = RentCadence.Monthly,
            SepaPaymentMethodId = "pm_sepa",
            SepaAutomaticFrom = new(2026, 11, 10)
        };
        var due = new RentLedgerEntry { PeriodStart = new(2026, 11, 1), DueDate = new(2026, 11, 10) };
        Assert.False(TenantSepaAuthorization.CanAutomate(lease, s, due, new(2026, 11, 10)));
        s.SepaAutomaticEnabled = true;
        Assert.True(TenantSepaAuthorization.CanAutomate(lease, s, due, new(2026, 11, 10)));
        due.DueDate = new(2026, 11, 9);
        Assert.False(TenantSepaAuthorization.CanAutomate(lease, s, due, new(2026, 11, 10)));
        due.DueDate = new(2026, 11, 10); due.Status = RentLedgerStatus.Processing;
        Assert.False(TenantSepaAuthorization.CanAutomate(lease, s, due, new(2026, 11, 10)));
        due.Status = RentLedgerStatus.Paid;
        Assert.False(TenantSepaAuthorization.CanAutomate(lease, s, due, new(2026, 11, 10)));
    }
}
