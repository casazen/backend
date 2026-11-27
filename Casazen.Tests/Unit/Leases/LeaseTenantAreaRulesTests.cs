using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Xunit;

namespace Casazen.Tests.Unit.Leases;

public sealed class LeaseTenantAreaRulesTests
{
    // Every status is on one side or the other. A status added to LeaseStatus fails EveryStatusIsDecided until somebody decides
    // whether the tenant may read a lease in that status: the new value is never shown or hidden by default.
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

    // The rule is an expression, so the area applies it in the query itself and a draft never leaves the database.
    private static List<LeaseStatus> Kept(IEnumerable<LeaseStatus> statuses) =>
        statuses.Select(status => new LeaseContract { Status = status })
            .AsQueryable()
            .Where(LeaseTenantAreaRules.VisibleToTenant)
            .Select(lease => lease.Status)
            .ToList();
}
