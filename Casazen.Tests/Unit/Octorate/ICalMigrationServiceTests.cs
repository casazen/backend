using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The hand-over of the iCal feeds of a channel to Octorate (E02/F02/US01/T03, ADR-002-R11, R14, NF-28): the feeds of the channels the confirmed
/// listing has are deprecated, a feed of channel <c>Other</c> never is, the feed and its blocks are kept, and nothing of another property or
/// another org is touched. The log names the ids, never the address of a feed.
/// </summary>
public class ICalMigrationServiceTests
{
    private const string SecretAddress = "https://secret.example.com/cal.ics?t=SECRET-TOKEN";

    private static readonly Guid OrgA = Guid.Parse("4f1e2d3c-6b5a-4978-8a1b-2c3d4e5f6a11");
    private static readonly Guid OrgB = Guid.Parse("9a8b7c6d-5e4f-4a3b-9c2d-1e0f2a3b4c22");
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheFeedsOfTheChannelsOfTheListing_AreDeprecated_AtTheTimeOfTheRead_AndTheirBlocksAreKept()
    {
        var s = new Scenario();
        var airbnb = await s.AddFeedAsync(ICalFeedChannel.Airbnb);
        var booking = await s.AddFeedAsync(ICalFeedChannel.BookingCom);
        await s.AddBlockAsync(airbnb, "airbnb-1");
        await s.AddBlockAsync(booking, "booking-1");
        await using var db = s.NewDb();

        var deprecated = await s.Service(db).DeprecateCoveredFeedsAsync(s.PropertyId, CancellationToken.None);

        Assert.Equal(new[] { airbnb, booking }.Order(), deprecated.Order());
        await using var read = s.NewDb();
        var feeds = await read.PropertyICalFeeds.AsNoTracking().ToListAsync();
        Assert.All(feeds, f =>
        {
            Assert.True(f.Deprecated);
            Assert.Equal(Now.UtcDateTime, f.DeprecatedAt);
        });
        // The feed stays, and so do the blocks it imported (they are kept as they were).
        Assert.Equal(2, await read.CalendarBlocks.CountAsync());
        Assert.All(await read.CalendarBlocks.ToListAsync(), b => Assert.NotNull(b.FeedId));
    }

    [Fact]
    public async Task AFeedOfChannelOther_IsNeverDeprecatedAutomatically_AndNeitherIsOneOfAChannelTheListingDoesNotHave()
    {
        var s = new Scenario(channelIds: """{ "142": "B-1" }""");
        var airbnb = await s.AddFeedAsync(ICalFeedChannel.Airbnb);
        var other = await s.AddFeedAsync(ICalFeedChannel.Other);
        var booking = await s.AddFeedAsync(ICalFeedChannel.BookingCom);
        await using var db = s.NewDb();

        var deprecated = await s.Service(db).DeprecateCoveredFeedsAsync(s.PropertyId, CancellationToken.None);

        // Octorate does not know the source of an "other" feed, and the listing has Booking.com (142) only.
        Assert.Equal([booking], deprecated);
        await using var read = s.NewDb();
        var feeds = await read.PropertyICalFeeds.AsNoTracking().ToDictionaryAsync(f => f.Id);
        Assert.False(feeds[airbnb].Deprecated);
        Assert.False(feeds[other].Deprecated);
        Assert.Null(feeds[other].DeprecatedAt);
        Assert.True(feeds[booking].Deprecated);
    }

    [Theory]
    [InlineData(OctorateMappingStatus.Proposed)]
    [InlineData(OctorateMappingStatus.Ambiguous)]
    [InlineData(OctorateMappingStatus.Rejected)]
    public async Task NothingIsDeprecated_WhileTheMappingIsNotConfirmed(OctorateMappingStatus status)
    {
        var s = new Scenario(status);
        await s.AddFeedAsync(ICalFeedChannel.Airbnb);
        await s.AddFeedAsync(ICalFeedChannel.BookingCom);
        await using var db = s.NewDb();

        var deprecated = await s.Service(db).DeprecateCoveredFeedsAsync(s.PropertyId, CancellationToken.None);

        Assert.Empty(deprecated);
        await using var read = s.NewDb();
        Assert.All(await read.PropertyICalFeeds.AsNoTracking().ToListAsync(), f => Assert.False(f.Deprecated));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("not json at all")]
    [InlineData("""{ "235": "VRBO-1", "4": "EXP-1" }""")]
    public async Task NothingIsDeprecated_WhenTheListingHasNeitherAirbnbNorBookingCom_OrTheColumnIsNotWhatCasaZenWrote(string channelIds)
    {
        var s = new Scenario(channelIds: channelIds);
        await s.AddFeedAsync(ICalFeedChannel.Airbnb);
        await s.AddFeedAsync(ICalFeedChannel.BookingCom);
        await using var db = s.NewDb();

        var deprecated = await s.Service(db).DeprecateCoveredFeedsAsync(s.PropertyId, CancellationToken.None);

        Assert.Empty(deprecated);
        await using var read = s.NewDb();
        Assert.All(await read.PropertyICalFeeds.AsNoTracking().ToListAsync(), f => Assert.False(f.Deprecated));
    }

    [Fact]
    public async Task ItIsDoneOnce_ASecondCallChangesNothing_AndKeepsTheDateOfTheFirst()
    {
        var s = new Scenario();
        var airbnb = await s.AddFeedAsync(ICalFeedChannel.Airbnb);
        await using var db = s.NewDb();
        Assert.Equal([airbnb], await s.Service(db).DeprecateCoveredFeedsAsync(s.PropertyId, CancellationToken.None));

        s.Clock.Advance(TimeSpan.FromDays(3));
        await using var again = s.NewDb();
        var second = await s.Service(again).DeprecateCoveredFeedsAsync(s.PropertyId, CancellationToken.None);

        Assert.Empty(second);
        await using var read = s.NewDb();
        Assert.Equal(Now.UtcDateTime, (await read.PropertyICalFeeds.AsNoTracking().SingleAsync()).DeprecatedAt);
    }

    [Fact]
    public async Task OnlyTheFeedsOfTheOrgOfTheMapping_AndOfTheProperty_AreTouched()
    {
        var s = new Scenario();
        var mine = await s.AddFeedAsync(ICalFeedChannel.Airbnb);
        var otherOrg = await s.AddFeedAsync(ICalFeedChannel.Airbnb, orgId: OrgB);
        var otherProperty = await s.AddFeedAsync(ICalFeedChannel.Airbnb, propertyId: Guid.NewGuid());
        await using var db = s.NewDb();

        var deprecated = await s.Service(db).DeprecateCoveredFeedsAsync(s.PropertyId, CancellationToken.None);

        Assert.Equal([mine], deprecated);
        await using var read = s.NewDb();
        var feeds = await read.PropertyICalFeeds.AsNoTracking().ToDictionaryAsync(f => f.Id);
        Assert.True(feeds[mine].Deprecated);
        Assert.False(feeds[otherOrg].Deprecated);
        Assert.False(feeds[otherProperty].Deprecated);
    }

    [Fact]
    public async Task TheHandOverIsNotOneWay_TheFeedsOfAPropertyNoLongerRead_AreGivenBack_AndTheOthersStayDeprecated()
    {
        var s = new Scenario();
        var noLongerRead = await s.AddFeedAsync(ICalFeedChannel.Airbnb, url: SecretAddress);
        var stillRead = Guid.NewGuid();
        var kept = await s.AddFeedAsync(ICalFeedChannel.Airbnb, propertyId: stillRead);
        var ofAnotherOrg = await s.AddFeedAsync(ICalFeedChannel.BookingCom, orgId: OrgB, propertyId: Guid.NewGuid());
        var neverDeprecated = await s.AddFeedAsync(ICalFeedChannel.Other);
        await s.MarkDeprecatedAsync(noLongerRead, kept, ofAnotherOrg);
        await using var db = s.NewDb();

        // The calendar job reads one property now (the one of `kept`): every other deprecated feed goes back to the import, in every org.
        var given = await s.Service(db).ReinstateFeedsAsync([stillRead], CancellationToken.None);

        Assert.Equal(new[] { noLongerRead, ofAnotherOrg }.Order(), given.Order());
        await using var read = s.NewDb();
        var feeds = await read.PropertyICalFeeds.AsNoTracking().ToDictionaryAsync(f => f.Id);
        Assert.False(feeds[noLongerRead].Deprecated);
        Assert.Null(feeds[noLongerRead].DeprecatedAt);
        Assert.False(feeds[ofAnotherOrg].Deprecated);
        Assert.True(feeds[kept].Deprecated);
        Assert.NotNull(feeds[kept].DeprecatedAt);
        Assert.False(feeds[neverDeprecated].Deprecated);
        // The feed itself is untouched, and the log names the property and the feed, never the address.
        Assert.Equal(SecretAddress, feeds[noLongerRead].ImportUrl);
        Assert.All(s.Log.Lines, line => Assert.DoesNotContain("secret.example.com", line, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(s.Log.Lines, line => line.Contains(noLongerRead.ToString(), StringComparison.Ordinal));

        // Nothing left to give back: a second call changes nothing.
        await using var again = s.NewDb();
        Assert.Empty(await s.Service(again).ReinstateFeedsAsync([stillRead], CancellationToken.None));
    }

    [Fact]
    public async Task AnUnknownProperty_HasNothingToDeprecate()
    {
        var s = new Scenario();
        await s.AddFeedAsync(ICalFeedChannel.Airbnb);
        await using var db = s.NewDb();

        Assert.Empty(await s.Service(db).DeprecateCoveredFeedsAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task TheLog_NamesThePropertyAndTheFeeds_NeverTheAddressOfAFeed()
    {
        var s = new Scenario();
        var airbnb = await s.AddFeedAsync(ICalFeedChannel.Airbnb, url: SecretAddress);
        await using var db = s.NewDb();

        await s.Service(db).DeprecateCoveredFeedsAsync(s.PropertyId, CancellationToken.None);

        var line = Assert.Single(s.Log.Lines);
        Assert.Contains(s.PropertyId.ToString(), line, StringComparison.Ordinal);
        Assert.Contains(airbnb.ToString(), line, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.example.com", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET-TOKEN", line, StringComparison.Ordinal);
    }

    /// <summary>A property of org A with a listing mapped to it, and the feeds the tests add to it.</summary>
    private sealed class Scenario
    {
        private readonly string _database = $"ical-migration-{Guid.NewGuid():N}";

        public Scenario(
            OctorateMappingStatus status = OctorateMappingStatus.Confirmed,
            string channelIds = """{ "274": "A-1", "142": "B-1" }""")
        {
            using var seed = NewDb();
            var connection = new OctorateConnection { OrgId = OrgA, Environment = OctorateEnvironment.Test, Status = OctorateConnectionStatus.Connected };
            var property = new Property
            {
                OrgId = OrgA,
                OwnerId = "auth0|owner-a",
                Name = "Casa del Lago",
                Address = "Via del Lago 1",
                City = "Como",
                MaxGuests = 4,
            };
            seed.OctorateConnections.Add(connection);
            seed.Properties.Add(property);
            seed.OctorateListingMappings.Add(new OctorateListingMapping
            {
                OrgId = OrgA,
                ConnectionId = connection.Id,
                OctorateAccommodationId = "585492",
                OctorateProductId = "956528",
                PropertyId = property.Id,
                Status = status,
                ChannelIdsJson = channelIds,
            });
            seed.SaveChanges();
            PropertyId = property.Id;
        }

        public Guid PropertyId { get; }

        public FakeTimeProvider Clock { get; } = new(Now);

        public CapturingLogger Log { get; } = new();

        public AppDbContext NewDb() =>
            new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options, NullTenantContext.Instance);

        public ICalMigrationService Service(AppDbContext db) => new(db, Clock, Log);

        public async Task<Guid> AddFeedAsync(ICalFeedChannel channel, Guid? orgId = null, Guid? propertyId = null, string url = SecretAddress)
        {
            await using var db = NewDb();
            var feed = new PropertyICalFeed
            {
                PropertyId = propertyId ?? PropertyId,
                OrgId = orgId ?? OrgA,
                Channel = channel,
                ImportUrl = url,
            };
            db.PropertyICalFeeds.Add(feed);
            await db.SaveChangesAsync();
            return feed.Id;
        }

        public async Task MarkDeprecatedAsync(params Guid[] feedIds)
        {
            await using var db = NewDb();
            foreach (var feed in await db.PropertyICalFeeds.Where(f => feedIds.Contains(f.Id)).ToListAsync())
            {
                feed.Deprecated = true;
                feed.DeprecatedAt = Now.UtcDateTime;
            }

            await db.SaveChangesAsync();
        }

        public async Task AddBlockAsync(Guid feedId, string uid)
        {
            await using var db = NewDb();
            db.CalendarBlocks.Add(new CalendarBlock
            {
                PropertyId = PropertyId,
                OrgId = OrgA,
                FeedId = feedId,
                Source = CalendarBlockSource.ICalImport,
                ExternalUid = uid,
                StartUtc = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc),
                EndUtc = new DateTime(2026, 12, 3, 0, 0, 0, DateTimeKind.Utc),
            });
            await db.SaveChangesAsync();
        }
    }

    /// <summary>Keeps the text of every line the service logs.</summary>
    private sealed class CapturingLogger : ILogger<ICalMigrationService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
