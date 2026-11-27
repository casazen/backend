using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Xunit;

namespace Casazen.Tests.Unit.Leases;

public sealed class LeaseTenantAreaRulesTests
{
    private static readonly LeaseStatus[] Hidden = [LeaseStatus.Draft];

    private static readonly LeaseStatus[] Shown =
    [
        LeaseStatus.AwaitingSignature,
        LeaseStatus.PartiallySigned,
        LeaseStatus.Signed,
        LeaseStatus.RegistrationPending,
        LeaseStatus.SentToProvider,
        LeaseStatus.Registered,
        LeaseStatus.Rejected,
    ];

    [Fact]
    public void EveryStatusIsDecided() =>
        Assert.Equal(Enum.GetValues<LeaseStatus>().Order(), Hidden.Concat(Shown).Order());

    [Fact]
    public void VisibleToTenant_HidesTheDraft() =>
        Assert.Empty(Kept(Hidden));

    [Fact]
    public void VisibleToTenant_KeepsEveryOtherStatus() =>
        Assert.Equal(Shown, Kept(Shown));

    private static List<LeaseStatus> Kept(IEnumerable<LeaseStatus> statuses) =>
        statuses.Select(status => new LeaseContract { Status = status })
            .AsQueryable()
            .Where(LeaseTenantAreaRules.VisibleToTenant)
            .Select(lease => lease.Status)
            .ToList();
}
