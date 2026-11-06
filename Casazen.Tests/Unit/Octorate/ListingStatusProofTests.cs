using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Photos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The proofs of the status of the listings and of the changes made outside CasaZen (E02/F03/US01/T07, NF-27, NF-38): the status and the choice about
/// a change never reveal or touch a property of another org (permission rule); detection only reads Octorate, flags the change and the groups, and a
/// group other than the descriptions cannot be taken (data rule); a channel moves to published or to fix only from the process of the channel manager.
/// Over an in-memory database and mocks of the client of Octorate.
/// </summary>
public class ListingStatusProofTests
{
    private const string Accommodation = "585492";
    private const string Product = "956528";
    private const string Host = "auth0|host-a";

    private static readonly Guid OrgA = Guid.Parse("4f1e2d3c-6b5a-4978-8a1b-2c3d4e5f6a21");
    private static readonly Guid OrgB = Guid.Parse("9a8b7c6d-5e4f-4a3b-9c2d-1e0f2a3b4c22");
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);

    private readonly string _database = $"listing-status-{Guid.NewGuid():N}";
    private readonly Mock<IOctorateApiClient> _client = new();
    private readonly Mock<IInternalActivityService> _activities = new();
    private readonly Mock<IContentPushService> _push = new();
    private readonly Mock<IPropertySiteProfileService> _profiles = new();
    private readonly Mock<IHostScopeResolver> _scope = new();
    private readonly Mock<IFileStorage> _storage = new();
    private readonly FixedTimeProvider _clock = new(Now);

    public ListingStatusProofTests()
    {
        _activities.Setup(a => a.CreateAsync(It.IsAny<CreateTodoRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
        _scope
            .Setup(s => s.CanReachPropertyAsync(Host, It.IsAny<IReadOnlySet<string>>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    [Fact]
    public async Task ListingStatus_PropertyOfAnotherOrg_NotFound_Test()
    {
        var propertyId = await SeedPropertyAsync(OrgA);
        await SeedRowAsync(propertyId, "homeaway", ChannelPublishStatus.Published, externalChange: true, changedGroups: ["descriptions"]);

        // The status of a property of another org does not exist for the caller; the summary lists only the properties of its own scope.
        await using (var asOrgB = NewDb(new FixedTenant(OrgB)))
        {
            Assert.Null(await Status(asOrgB).GetAsync(propertyId, CancellationToken.None));
            Assert.Empty(await Status(asOrgB).GetSummaryAsync(new HostScope(OrgB), CancellationToken.None));

            // The choice about a change is not reachable either: nothing is read from Octorate, nothing is published, nothing is adopted.
            await Assert.ThrowsAsync<NotFoundException>(
                () => Outside(asOrgB).ResolveAsync(propertyId, "homeaway", OutsideChangeChoice.Take, Host, CancellationToken.None));
        }

        await using (var asOrgA = NewDb(new FixedTenant(OrgA)))
        {
            var own = await Status(asOrgA).GetAsync(propertyId, CancellationToken.None);
            Assert.NotNull(own);
            Assert.Single(await Status(asOrgA).GetSummaryAsync(new HostScope(OrgA), CancellationToken.None));
        }

        _push.Verify(
            p => p.PublishAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlySet<string>?>()),
            Times.Never);
        _profiles.Verify(p => p.SaveAsync(It.IsAny<Guid>(), It.IsAny<Casazen.Core.SiteProfile.SitePropertyProfileInput>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(_client.Invocations);
    }

    [Fact]
    public async Task ListingOutsideChange_NotOverwrittenWithoutChoice_Test()
    {
        // The content changed on Octorate: it is found and flagged with the groups, reading only; what CasaZen holds is not written over it.
        var propertyId = await SeedPropertyAsync(OrgA);
        var original = new OctorateDescriptions { Description = new Dictionary<string, string> { ["IT"] = "Un appartamento luminoso." } };
        var propertyOnOctorate = new OctorateProperty { Id = Accommodation, Name = "Casa del Lago", Address = "Via del Lago 1", City = "Como", PostalCode = "22100" };
        var baseline = new ListingChannelBaseline(
            new Dictionary<string, string>
            {
                [ListingContentGroups.Property] = OctorateContentReader.HashOf(OctorateContentReader.PropertyForm(propertyOnOctorate)),
                [ListingContentGroups.Descriptions] = OctorateContentReader.HashOf(OctorateContentReader.DescriptionsForm(original)),
                [ListingContentGroups.Rooms] = OctorateContentReader.HashOf(OctorateContentReader.RoomsForm([])),
            },
            null,
            []);
        await SeedRowAsync(propertyId, "homeaway", ChannelPublishStatus.Published, baseline: baseline);
        await SeedRowAsync(propertyId, "booking_xml", ChannelPublishStatus.Sending, baseline: baseline);
        _client.Setup(c => c.GetPropertyAsync(Accommodation, It.IsAny<CancellationToken>())).ReturnsAsync(propertyOnOctorate);
        _client.Setup(c => c.GetContentRoomsAsync(Accommodation, Product, It.IsAny<CancellationToken>())).ReturnsAsync(new List<OctorateContentRoom>());
        _client
            .Setup(c => c.GetDescriptionsAsync(Accommodation, Product, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateDescriptions { Description = new Dictionary<string, string> { ["IT"] = "Descrizione cambiata da qualcuno su Octorate." } });
        await using var db = NewDb();

        var groups = await Outside(db).DetectAsync(OrgA, propertyId, CancellationToken.None);

        Assert.Equal([ListingContentGroups.Descriptions], groups);
        await using var check = NewDb();
        var flagged = await check.ListingChannelStates.SingleAsync(s => s.ChannelCode == "homeaway");
        Assert.Equal(Now.UtcDateTime, flagged.ExternalChangeAt);
        Assert.Equal([ListingContentGroups.Descriptions], ListingChannelBaseline.Parse(flagged.ContentHashesJson).ChangedGroups);
        Assert.Null((await check.ListingChannelStates.SingleAsync(s => s.ChannelCode == "booking_xml")).ExternalChangeAt);
        _activities.Verify(
            a => a.CreateAsync(It.Is<CreateTodoRequest>(r => r.Type == ActivityType.ListingChangedOutside && r.EntityId == flagged.Id), It.IsAny<CancellationToken>()),
            Times.Once);

        // Detection reads and nothing else: no operation of the client changes anything on Octorate.
        Assert.NotEmpty(_client.Invocations);
        Assert.All(_client.Invocations, call => Assert.StartsWith("Get", call.Method.Name, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolveAsync_TakeOfAGroupOtherThanTheDescriptions_IsRefusedWithTheGroups_AndKeepRepublishesWithOverwrite()
    {
        var propertyId = await SeedPropertyAsync(OrgA);
        var changed = new ListingChannelBaseline(new Dictionary<string, string> { [ListingContentGroups.Rooms] = "abc" }, null, [ListingContentGroups.Descriptions, ListingContentGroups.Rooms]);
        await SeedRowAsync(propertyId, "homeaway", ChannelPublishStatus.Published, externalChange: true, baseline: changed);
        await using var db = NewDb(new FixedTenant(OrgA));

        var refused = await Assert.ThrowsAsync<DomainRuleException>(
            () => Outside(db).ResolveAsync(propertyId, "homeaway", OutsideChangeChoice.Take, Host, CancellationToken.None));
        await Outside(db).ResolveAsync(propertyId, "homeaway", OutsideChangeChoice.Keep, Host, CancellationToken.None);

        Assert.Equal("outside_change_take_not_supported", refused.Code);
        Assert.Equal([ListingContentGroups.Rooms], Assert.IsAssignableFrom<IEnumerable<string>>(refused.Extensions["groups"]));
        _profiles.Verify(p => p.SaveAsync(It.IsAny<Guid>(), It.IsAny<Casazen.Core.SiteProfile.SitePropertyProfileInput>(), It.IsAny<CancellationToken>()), Times.Never);
        _push.Verify(
            p => p.PublishAsync(propertyId, It.Is<IReadOnlyList<string>>(c => c.SequenceEqual(new[] { "homeaway" })), true, Host, It.IsAny<CancellationToken>(), It.IsAny<IReadOnlySet<string>?>()),
            Times.Once);
        Assert.Empty(_client.Invocations);
    }

    [Fact]
    public async Task SyncPropertyAsync_MovesTheRowsOnlyFromTheProcessOfTheChannelManager()
    {
        var propertyId = await SeedPropertyAsync(OrgA);
        await SeedRowAsync(propertyId, "booking_xml", ChannelPublishStatus.Accepted, baseline: new ListingChannelBaseline(new Dictionary<string, string>(), "p-done", []));
        await SeedRowAsync(propertyId, "homeaway", ChannelPublishStatus.Accepted, baseline: new ListingChannelBaseline(new Dictionary<string, string>(), "p-failed", []));
        await SeedRowAsync(propertyId, "airbnb_xml", ChannelPublishStatus.Accepted, baseline: new ListingChannelBaseline(new Dictionary<string, string>(), "p-unclear", []));
        _client.Setup(c => c.GetContentProcessAsync(Accommodation, "p-done", It.IsAny<CancellationToken>())).ReturnsAsync(new OctorateContentProcess(OctorateProcessState.Done, null));
        _client.Setup(c => c.GetContentProcessAsync(Accommodation, "p-failed", It.IsAny<CancellationToken>())).ReturnsAsync(new OctorateContentProcess(OctorateProcessState.Failed, "Title too long"));
        _client.Setup(c => c.GetContentProcessAsync(Accommodation, "p-unclear", It.IsAny<CancellationToken>())).ReturnsAsync(new OctorateContentProcess(OctorateProcessState.Unknown, null));
        await using var db = NewDb();

        var changed = await Status(db).SyncPropertyAsync(OrgA, propertyId, CancellationToken.None);

        Assert.Equal(2, changed);
        await using var check = NewDb();
        var published = await check.ListingChannelStates.SingleAsync(s => s.ChannelCode == "booking_xml");
        var toFix = await check.ListingChannelStates.SingleAsync(s => s.ChannelCode == "homeaway");
        var unclear = await check.ListingChannelStates.SingleAsync(s => s.ChannelCode == "airbnb_xml");
        Assert.Equal(ChannelPublishStatus.Published, published.Status);
        Assert.Equal(Now.UtcDateTime, published.LastSyncedAt);
        Assert.Equal(ChannelPublishStatus.ToFix, toFix.Status);
        Assert.Equal(ListingChannelErrors.ChannelRefused, toFix.ErrorCode);
        Assert.Equal("Title too long", toFix.ErrorMessage);
        Assert.Equal(ChannelPublishStatus.Accepted, unclear.Status);
        Assert.Null(unclear.LastSyncedAt);
        _activities.Verify(
            a => a.CreateAsync(It.Is<CreateTodoRequest>(r => r.Type == ActivityType.PublicationError && r.EntityId == toFix.Id), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetAsync_ShowsTheCompletenessTheMissingStepsTheAlertAndWhereTheCinIsNotSent()
    {
        var propertyId = await SeedPropertyAsync(OrgA, englishTexts: false);
        await SeedRowAsync(propertyId, "booking_xml", ChannelPublishStatus.Accepted, lastSyncedHoursAgo: 30);
        await SeedRowAsync(propertyId, "expedia", ChannelPublishStatus.ManagedOnChannel);
        await using var db = NewDb(new FixedTenant(OrgA));

        var view = await Status(db).GetAsync(propertyId, CancellationToken.None);

        Assert.NotNull(view);
        var booking = view.Channels.Single(c => c.ChannelCode == "booking_xml");
        var expedia = view.Channels.Single(c => c.ChannelCode == "expedia");
        Assert.True(booking.SyncAlert);
        Assert.Equal(30 * 3600, booking.SyncLagSeconds);
        Assert.InRange(booking.Completeness, 1, 99);
        Assert.Contains(ListingStatusSteps.Descriptions, booking.Missing);
        Assert.Contains(ListingStatusSteps.Photos, booking.Missing);
        Assert.False(expedia.SyncAlert);
        Assert.Equal(100, expedia.Completeness);
        Assert.Equal(["booking_xml"], view.CinNotSentTo);
    }

    private ListingStatusService Status(AppDbContext db)
    {
        var urls = new PublicPhotoUrlService(_storage.Object);
        return new ListingStatusService(
            db,
            _client.Object,
            new OctorateOrgScope(NullTenantContext.Instance),
            new ListingPhotoSync(db, _client.Object, urls, _clock, NullLogger<ListingPhotoSync>.Instance),
            urls,
            new ListingTodos(db, _activities.Object),
            Microsoft.Extensions.Options.Options.Create(Channels()),
            _clock,
            NullLogger<ListingStatusService>.Instance);
    }

    private OutsideChangeService Outside(AppDbContext db) =>
        new(
            db,
            _client.Object,
            new OctorateOrgScope(NullTenantContext.Instance),
            new OctorateContentReader(_client.Object),
            _push.Object,
            _profiles.Object,
            _scope.Object,
            new ListingTodos(db, _activities.Object),
            _clock,
            NullLogger<OutsideChangeService>.Instance);

    private static ListingChannelOptions Channels() => new()
    {
        Channels =
        {
            ["booking_xml"] = new ListingChannelRule { ChannelId = "142", MinPhotos = 3, RequiresCin = true, UsesPushQueue = true },
            ["homeaway"] = new ListingChannelRule { ChannelId = "235", MinPhotos = 3, RequiresCin = false, UsesPushQueue = true },
        },
    };

    private AppDbContext NewDb(ITenantContext? tenant = null) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options, tenant ?? NullTenantContext.Instance);

    private async Task<Guid> SeedPropertyAsync(Guid orgId, bool englishTexts = true)
    {
        await using var db = NewDb();
        var property = new Property
        {
            OrgId = orgId,
            OwnerId = Host,
            Name = "Casa del Lago",
            ListingTitle = "Casa sul lago",
            Description = "Un appartamento luminoso sul lago.",
            Address = "Via del Lago 1",
            City = "Como",
            PostalCode = "22100",
            Country = "IT",
            Latitude = 45.4642m,
            Longitude = 9.19m,
            MaxGuests = 4,
            CinCode = "IT058091C27G5FFZDZ",
            NightlyRate = 100m,
        };
        if (englishTexts)
        {
            property.SiteProfileJson = """{"v":1,"translations":{"en":{"title":"House on the lake","description":"A bright flat on the lake."}}}""";
        }

        db.Properties.Add(property);
        var connection = new OctorateConnection { OrgId = orgId, Environment = OctorateEnvironment.Test, Status = OctorateConnectionStatus.Connected };
        db.OctorateConnections.Add(connection);
        db.OctorateListingMappings.Add(new OctorateListingMapping
        {
            OrgId = orgId,
            ConnectionId = connection.Id,
            OctorateAccommodationId = Accommodation,
            OctorateProductId = Product,
            PropertyId = property.Id,
            Status = OctorateMappingStatus.Confirmed,
        });
        await db.SaveChangesAsync();
        return property.Id;
    }

    private async Task SeedRowAsync(
        Guid propertyId,
        string channel,
        ChannelPublishStatus status,
        bool externalChange = false,
        string[]? changedGroups = null,
        ListingChannelBaseline? baseline = null,
        int? lastSyncedHoursAgo = null)
    {
        await using var db = NewDb();
        var orgId = (await db.Properties.AsNoTracking().SingleAsync(p => p.Id == propertyId)).OrgId;
        var kept = baseline ?? ListingChannelBaseline.Empty;
        if (changedGroups is not null)
            kept = kept.WithChanged(changedGroups);

        db.ListingChannelStates.Add(new ListingChannelState
        {
            OrgId = orgId,
            PropertyId = propertyId,
            ChannelCode = channel,
            Status = status,
            ContentHashesJson = kept.ToJson(),
            ExternalChangeAt = externalChange ? Now.UtcDateTime.AddHours(-1) : null,
            LastSyncedAt = lastSyncedHoursAgo is { } hours ? Now.UtcDateTime.AddHours(-hours) : null,
        });
        await db.SaveChangesAsync();
    }

    private sealed class FixedTenant(Guid orgId) : ITenantContext
    {
        public Guid? OrgId => orgId;

        public bool FilterEnabled => true;
    }
}
