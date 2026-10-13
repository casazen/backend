using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-02 (D13, D35): the seats of an org are its active members plus its pending invitations that have not expired, against
/// what the effective plan allows. The accountant counts, a deactivated member does not, an expiry frees the seat the moment it
/// passes, and an org whose subscription is not in good standing has the Starter number whatever it stored.
/// </summary>
public class OrgSeatServiceTests
{
    private readonly OrgInvitationTestKit _kit = new();

    private async Task<OrgSeatUsage> UsageAsync(Guid orgId)
    {
        await using var db = _kit.NewDb();
        return await _kit.Seats(db).GetUsageAsync(orgId);
    }

    [Theory]
    [InlineData(PlanTier.Starter, SubscriptionStatus.None, 2)]
    [InlineData(PlanTier.Pro, SubscriptionStatus.Active, 10)]
    [InlineData(PlanTier.Pro, SubscriptionStatus.Trialing, 10)]
    [InlineData(PlanTier.Scale, SubscriptionStatus.Active, int.MaxValue)]
    [InlineData(PlanTier.Pro, SubscriptionStatus.None, 2)]
    [InlineData(PlanTier.Pro, SubscriptionStatus.Unpaid, 2)]
    [InlineData(PlanTier.Pro, SubscriptionStatus.Canceled, 2)]
    [InlineData(PlanTier.Scale, SubscriptionStatus.Incomplete, 2)]
    public async Task GetUsageAsync_TheLimitIsTheOneOfTheEffectivePlan(PlanTier tier, SubscriptionStatus status, int expectedMax)
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: tier, status: status);

        var usage = await UsageAsync(org.Id);

        Assert.Equal(expectedMax, usage.Max);
        Assert.Equal(expectedMax == int.MaxValue, usage.IsUnlimited);
    }

    [Fact]
    public async Task GetUsageAsync_TheOwnerAloneUsesOneSeat()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();

        var usage = await UsageAsync(org.Id);

        Assert.Equal((1, 0, 1, 9), (usage.ActiveMembers, usage.PendingInvitations, usage.Used, usage.Available));
        Assert.True(usage.CanInvite);
    }

    [Fact]
    public async Task GetUsageAsync_CountsActiveMembersAndOpenInvitationsOnly()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedMemberAsync(org.Id, "auth0|active", OrgRole.Collaborator);
        await _kit.SeedMemberAsync(org.Id, "auth0|accountant", OrgRole.Accountant);
        await _kit.SeedMemberAsync(org.Id, "auth0|gone", OrgRole.Collaborator, status: OrgMemberStatus.Deactivated);
        await _kit.SeedInvitationAsync(org.Id, "open@example.com");
        await _kit.SeedInvitationAsync(org.Id, "overdue@example.com", expiresAt: _kit.Now.AddSeconds(-1));
        await _kit.SeedInvitationAsync(org.Id, "marked@example.com", status: OrgInvitationStatus.Expired, closedAt: _kit.Now);
        await _kit.SeedInvitationAsync(org.Id, "accepted@example.com", status: OrgInvitationStatus.Accepted, closedAt: _kit.Now);
        await _kit.SeedInvitationAsync(org.Id, "revoked@example.com", status: OrgInvitationStatus.Revoked, closedAt: _kit.Now);

        var usage = await UsageAsync(org.Id);

        Assert.Equal((3, 1), (usage.ActiveMembers, usage.PendingInvitations));
        Assert.Equal(4, usage.Used);
    }

    [Fact]
    public async Task GetUsageAsync_ASeatIsFreeTheMomentTheExpiryPasses()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Starter, status: SubscriptionStatus.None);
        await _kit.SeedInvitationAsync(org.Id, "anna@example.com", expiresAt: _kit.Now.AddMinutes(10));

        Assert.False((await UsageAsync(org.Id)).CanInvite);
        _kit.Clock.Advance(TimeSpan.FromMinutes(10));

        Assert.True((await UsageAsync(org.Id)).CanInvite);
    }

    [Fact]
    public async Task GetUsageAsync_NeverCountsThePeopleOfAnotherOrg()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-a");
        var (other, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-b");
        await _kit.SeedMemberAsync(other.Id, "auth0|stranger", OrgRole.Admin);
        await _kit.SeedInvitationAsync(other.Id, "x@example.com");

        var usage = await UsageAsync(org.Id);

        Assert.Equal((1, 0), (usage.ActiveMembers, usage.PendingInvitations));
    }

    [Fact]
    public async Task GetUsageAsync_AnOrgThatDoesNotExist_GetsTheStarterLimit()
    {
        var usage = await UsageAsync(Guid.NewGuid());

        Assert.Equal((2, 0), (usage.Max, usage.Used));
    }

    [Fact]
    public async Task GetUsageAsync_OverTheLimitAfterADowngrade_NeverReportsNegativeSeats()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Pro, status: SubscriptionStatus.Unpaid);
        for (var i = 0; i < 3; i++)
            await _kit.SeedMemberAsync(org.Id, $"auth0|m{i}", OrgRole.Collaborator);

        var usage = await UsageAsync(org.Id);

        Assert.Equal((2, 4), (usage.Max, usage.Used));
        Assert.Equal(0, usage.Available);
        Assert.False(usage.CanInvite);
    }

    [Fact]
    public async Task EnsureSeatAvailableAsync_WithASeatFree_Passes_WithNoneItIsAConflict()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: PlanTier.Starter, status: SubscriptionStatus.None);
        await using (var db = _kit.NewDb())
            await _kit.Seats(db).EnsureSeatAvailableAsync(org.Id);

        await _kit.SeedInvitationAsync(org.Id, "anna@example.com");

        await using var full = _kit.NewDb();
        var error = await Assert.ThrowsAsync<DomainConflictException>(() => _kit.Seats(full).EnsureSeatAvailableAsync(org.Id));
        Assert.Equal(OrgSeatErrors.LimitReached, error.Code);
    }

    [Fact]
    public void OrgSeatUsage_Unlimited_NeverRunsOut()
    {
        var usage = new OrgSeatUsage(int.MaxValue, 5_000, 500);

        Assert.True(usage.IsUnlimited);
        Assert.True(usage.CanInvite);
        Assert.Equal(int.MaxValue, usage.Available);
        Assert.Equal(5_500, usage.Used);
    }
}
