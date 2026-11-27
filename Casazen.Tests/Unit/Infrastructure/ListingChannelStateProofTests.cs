using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// Proof of the tables of the content of a property on the channels (NF-27, NF-32, NF-38, E02/F03/US01/T01): what an organization has
/// written there is not visible to another one (permission rule, TN-2).
/// </summary>
public class ListingChannelStateProofTests
{
    private sealed class FixedTenantContext(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }

    /// <summary>
    /// Permission rule: the state of a property on a channel (and the sending of its photos) written by organization A is not read by
    /// organization B, whether B asks for every row or for the very property of A, and A still reads its own.
    /// </summary>
    [Fact]
    public async Task ListingChannelState_TenantIsolation_Test()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var propertyOfA = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ListingChannelStateTenancy_{Guid.NewGuid()}")
            .Options;

        await using (var writer = new AppDbContext(options, new FixedTenantContext(orgA)))
        {
            writer.ListingChannelStates.Add(new ListingChannelState
            {
                OrgId = orgA,
                PropertyId = propertyOfA,
                ChannelCode = "booking_xml",
                Status = ChannelPublishStatus.Published,
            });
            writer.PhotoUploadTasks.Add(new PhotoUploadTask
            {
                OrgId = orgA,
                PropertyId = propertyOfA,
                PhotoUrl = "https://example.com/properties/a/photos/1.jpg",
            });
            await writer.SaveChangesAsync();
        }

        await using (var asOrgB = new AppDbContext(options, new FixedTenantContext(orgB)))
        {
            Assert.Empty(await asOrgB.ListingChannelStates.ToListAsync());
            Assert.Empty(await asOrgB.ListingChannelStates.Where(s => s.PropertyId == propertyOfA).ToListAsync());
            Assert.Empty(await asOrgB.PhotoUploadTasks.ToListAsync());
            Assert.Empty(await asOrgB.PhotoUploadTasks.Where(t => t.PropertyId == propertyOfA).ToListAsync());
        }

        await using var asOrgA = new AppDbContext(options, new FixedTenantContext(orgA));
        var state = Assert.Single(await asOrgA.ListingChannelStates.ToListAsync());
        Assert.Equal("booking_xml", state.ChannelCode);
        Assert.Single(await asOrgA.PhotoUploadTasks.ToListAsync());
    }
}
