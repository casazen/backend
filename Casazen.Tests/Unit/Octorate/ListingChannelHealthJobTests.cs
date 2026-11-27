using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The daily check of the channels that went silent (E02/F03/US01/T07, NF-38, ADR-034-R3): one «To do» for a pushed channel not confirmed for 24 hours,
/// closed when it is fresh again. Run over the real «To do» service on an in-memory database, whose property filters (the deleted properties) are the
/// ones of production: a deleted property keeps its channel rows, and the service cannot raise an item for it.
/// </summary>
public class ListingChannelHealthJobTests
{
    private static readonly Guid OrgId = Guid.Parse("4f1e2d3c-6b5a-4978-8a1b-2c3d4e5f6a31");
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);

    private readonly string _database = $"listing-health-{Guid.NewGuid():N}";
    private readonly Casazen.Tests.Unit.FixedTimeProvider _clock = new(Now);

    [Fact]
    public async Task ExecuteAsync_ASilentChannel_RaisesOneItem_AndTheItemClosesWhenTheChannelIsFreshAgain()
    {
        var silent = await SeedAsync(deleted: false, lastSyncedHoursAgo: 30);
        var fresh = await SeedAsync(deleted: false, lastSyncedHoursAgo: 2);

        await RunAsync();
        await RunAsync();

        await using (var check = NewDb())
        {
            var item = Assert.Single(await check.InternalActivities.AsNoTracking().ToListAsync());
            Assert.Equal(nameof(ActivityType.IntegrationError), item.ActivityType);
            Assert.Equal(silent.RowId, item.EntityId);
            Assert.Equal(silent.PropertyId, item.PropertyId);
            Assert.Null(item.CompletedAt);
            Assert.NotEqual(fresh.RowId, item.EntityId);
        }

        // The channel manager answers again: the row is fresh and the next run closes the item.
        await using (var db = NewDb())
        {
            (await db.ListingChannelStates.SingleAsync(s => s.Id == silent.RowId)).LastSyncedAt = Now.UtcDateTime.AddMinutes(-5);
            await db.SaveChangesAsync();
        }

        await RunAsync();

        await using var closed = NewDb();
        var after = Assert.Single(await closed.InternalActivities.AsNoTracking().ToListAsync());
        Assert.NotNull(after.CompletedAt);
        Assert.Equal(InternalActivity.ClosedBySystem, after.ClosedBy);
    }

    [Fact]
    public async Task ExecuteAsync_ASilentChannelOfADeletedProperty_DoesNotStopTheRun_AndRaisesNothingForIt()
    {
        // A deleted property keeps its channel rows (no cleanup) and the «To do» service cannot see the property: it would throw «not found» at the
        // first such row, and the run would end before the other channels were looked at, every day.
        var gone = await SeedAsync(deleted: true, lastSyncedHoursAgo: 30);
        var silent = await SeedAsync(deleted: false, lastSyncedHoursAgo: 30);

        await RunAsync();

        await using var check = NewDb();
        var item = Assert.Single(await check.InternalActivities.AsNoTracking().ToListAsync());
        Assert.Equal(silent.RowId, item.EntityId);
        Assert.DoesNotContain(await check.InternalActivities.AsNoTracking().ToListAsync(), a => a.EntityId == gone.RowId);
    }

    [Fact]
    public async Task ExecuteAsync_AnItemOfAPropertyThatWasDeletedMeanwhile_IsClosed()
    {
        var channel = await SeedAsync(deleted: false, lastSyncedHoursAgo: 30);
        await RunAsync();
        await using (var db = NewDb())
        {
            (await db.Properties.SingleAsync(p => p.Id == channel.PropertyId)).IsDeleted = true;
            await db.SaveChangesAsync();
        }

        await RunAsync();

        await using var check = NewDb();
        Assert.NotNull(Assert.Single(await check.InternalActivities.AsNoTracking().ToListAsync()).CompletedAt);
    }

    private async Task RunAsync()
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(true);
        await using var db = NewDb();
        var job = new ListingChannelHealthJob(
            db,
            new ListingTodos(db, new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, _clock)),
            flags.Object,
            _clock,
            NullLogger<ListingChannelHealthJob>.Instance);

        await job.ExecuteAsync(CancellationToken.None);
    }

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options, NullTenantContext.Instance);

    /// <summary>A property (deleted or not) with one published channel last confirmed <paramref name="lastSyncedHoursAgo"/> hours ago.</summary>
    private async Task<(Guid PropertyId, Guid RowId)> SeedAsync(bool deleted, int lastSyncedHoursAgo)
    {
        await using var db = NewDb();
        var property = new Property
        {
            OrgId = OrgId,
            OwnerId = "auth0|host",
            Name = "Casa del Lago",
            Address = "Via del Lago 1",
            City = "Como",
            IsDeleted = deleted,
        };
        db.Properties.Add(property);
        var row = new ListingChannelState
        {
            OrgId = OrgId,
            PropertyId = property.Id,
            ChannelCode = "booking_xml",
            Status = ChannelPublishStatus.Published,
            LastSyncedAt = Now.UtcDateTime.AddHours(-lastSyncedHoursAgo),
        };
        db.ListingChannelStates.Add(row);
        await db.SaveChangesAsync();
        return (property.Id, row.Id);
    }
}
