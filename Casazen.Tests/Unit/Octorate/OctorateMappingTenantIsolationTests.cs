using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

public class OctorateMappingTenantIsolationTests
{
    [Fact]
    public async Task OctorateMapping_TenantIsolation_Test()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"octorate-mapping-{Guid.NewGuid()}").Options;
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var mappingId = Guid.NewGuid();

        await using (var system = new AppDbContext(options))
        {
            var connection = new OctorateConnection
            {
                OrgId = orgA,
                Environment = OctorateEnvironment.Test,
                Status = OctorateConnectionStatus.Connected,
            };
            system.OctorateConnections.Add(connection);
            system.OctorateListingMappings.Add(new OctorateListingMapping
            {
                Id = mappingId,
                OrgId = orgA,
                ConnectionId = connection.Id,
                OctorateAccommodationId = "585492",
                SnapshotName = "Casa del Lago",
                Status = OctorateMappingStatus.Proposed,
            });
            system.OctorateOAuthStates.Add(new OctorateOAuthState
            {
                OrgId = orgA,
                UserId = "auth0|owner-a",
                StateHash = new string('a', OctorateOAuthState.StateHashLength),
                RedirectUri = "https://app.example.com/app/short-rent/octorate/callback",
                Environment = OctorateEnvironment.Test,
                ExpiresAt = DateTime.UtcNow.AddMinutes(10),
            });
            await system.SaveChangesAsync();
        }

        await using (var asB = new AppDbContext(options, new RequestTenant(orgB)))
        {
            Assert.Empty(await asB.OctorateListingMappings.ToListAsync());
            Assert.Null(await asB.OctorateListingMappings.FirstOrDefaultAsync(m => m.Id == mappingId));
            Assert.Null(await asB.OctorateListingMappings.FirstOrDefaultAsync(m => m.OctorateAccommodationId == "585492"));
            Assert.Empty(await asB.OctorateOAuthStates.ToListAsync());
            Assert.Empty(await asB.OctorateConnections.ToListAsync());
        }

        await using (var asA = new AppDbContext(options, new RequestTenant(orgA)))
        {
            Assert.Equal(mappingId, Assert.Single(await asA.OctorateListingMappings.ToListAsync()).Id);
            Assert.Single(await asA.OctorateOAuthStates.ToListAsync());
            Assert.Single(await asA.OctorateConnections.ToListAsync());
        }

        Assert.True(typeof(ITenantOwned).IsAssignableFrom(typeof(OctorateListingMapping)));
        Assert.True(typeof(ITenantOwned).IsAssignableFrom(typeof(OctorateOAuthState)));
    }

    private sealed class RequestTenant(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
