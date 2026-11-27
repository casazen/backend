using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Marketplace;

/// <summary>
/// E09/F01/US01/T01, NF-54: an activation of the marketplace is tenant-owned, so the global tenant filter hides one organization's
/// activations from every request of another, whatever the query asks. Data rule: what an org has activated (a channel for a property, a
/// partner it declared) is never read in the context of another org.
/// </summary>
public class MarketplaceActivationTenantTests
{
    [Fact]
    public async Task MarketplaceActivation_TenantIsolation_Test()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"marketplace-activation-{Guid.NewGuid()}").Options;
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var propertyA = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var partnerId = Guid.NewGuid();

        // Written as a job would: no request, no tenant filter.
        await using (var system = new AppDbContext(options))
        {
            system.MarketplaceActivations.Add(new MarketplaceActivation
            {
                Id = channelId,
                OrgId = orgA,
                PropertyId = propertyA,
                Kind = MarketplaceActivationKind.Channel,
                TargetCode = "portal-one",
                Status = MarketplaceActivationStatus.Active,
            });
            system.MarketplaceActivations.Add(new MarketplaceActivation
            {
                Id = partnerId,
                OrgId = orgA,
                Kind = MarketplaceActivationKind.Partner,
                TargetCode = "partner-one",
                Status = MarketplaceActivationStatus.Active,
                IsDeclared = true,
                DeclaredAt = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc),
            });
            await system.SaveChangesAsync();
        }

        // A request of org B sees nothing of org A: not the list, not a row by its id, not a row by its target, not the count.
        await using (var asB = new AppDbContext(options, new RequestTenant(orgB)))
        {
            Assert.Empty(await asB.MarketplaceActivations.ToListAsync());
            Assert.Null(await asB.MarketplaceActivations.FirstOrDefaultAsync(a => a.Id == channelId));
            Assert.Null(await asB.MarketplaceActivations.FirstOrDefaultAsync(a => a.TargetCode == "partner-one"));
            Assert.Null(await asB.MarketplaceActivations.FirstOrDefaultAsync(a => a.PropertyId == propertyA));
            Assert.Equal(0, await asB.MarketplaceActivations.CountAsync());
        }

        // A request of org A sees its own rows.
        await using (var asA = new AppDbContext(options, new RequestTenant(orgA)))
        {
            var rows = await asA.MarketplaceActivations.OrderBy(a => a.TargetCode).ToListAsync();
            Assert.Equal(new[] { "partner-one", "portal-one" }, rows.Select(a => a.TargetCode));
        }

        Assert.True(typeof(ITenantOwned).IsAssignableFrom(typeof(MarketplaceActivation)));
    }

    /// <summary>The tenant of an authenticated request of one org.</summary>
    private sealed class RequestTenant(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
