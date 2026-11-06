using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.SiteProfile;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Photos;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The proofs of the publication of a listing (E02/F03/US01/T02, NF-27, NF-29, NF-32, ADR-036): only the photos of the listing, with their public
/// addresses, go to the channel manager and each goes once (privacy and data rules); a person publishes only a property of its own org within its
/// reach (permission rule); a channel that cannot be published is left to fix and the others go on, and the CIN blocks only the channels that ask for
/// it. Over an in-memory database and mocks of the client of Octorate, of the job queue and of the event outbox.
/// </summary>
public class ListingPublishProofTests
{
    private const string PublicBase = "https://storage.test/public/";
    private const string Host = "auth0|host-a";
    private const string Accommodation = "585492";
    private const string Product = "956528";

    private static readonly Guid OrgA = Guid.Parse("4f1e2d3c-6b5a-4978-8a1b-2c3d4e5f6a11");
    private static readonly Guid OrgB = Guid.Parse("9a8b7c6d-5e4f-4a3b-9c2d-1e0f2a3b4c12");
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);

    private readonly string _database = $"listing-publish-{Guid.NewGuid():N}";
    private readonly Mock<IOctorateApiClient> _client = new();
    private readonly Mock<IChannelMappingService> _mappings = new();
    private readonly Mock<IInventoryScenarioGuard> _scenarios = new();
    private readonly Mock<IOctoratePropertyCreator> _creator = new(MockBehavior.Strict);
    private readonly Mock<IHostScopeResolver> _scope = new();
    private readonly Mock<IFeatureFlags> _flags = new();
    private readonly Mock<IDomainEventRecorder> _recorder = new();
    private readonly Mock<IBackgroundJobClient> _jobs = new();
    private readonly Mock<IInternalActivityService> _activities = new();
    private readonly Mock<IFileStorage> _storage = new();
    private readonly FixedTimeProvider _clock = new(Now);

    public ListingPublishProofTests()
    {
        _flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(true);
        _scope
            .Setup(s => s.CanReachPropertyAsync(Host, It.IsAny<IReadOnlySet<string>>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _storage
            .Setup(s => s.TryGetPublicKey(It.IsAny<string>()))
            .Returns<string>(url => url.StartsWith(PublicBase, StringComparison.Ordinal) ? url[PublicBase.Length..] : null);
        _activities.Setup(a => a.CreateAsync(It.IsAny<CreateTodoRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
    }

    [Fact]
    public void PublicPhotoUrl_OnlyListingPhotos_Test()
    {
        var id = Guid.NewGuid();
        var own = $"{PublicBase}properties/{id}/photos/a1.jpg";
        var urls = new PublicPhotoUrlService(_storage.Object).ListingPhotoUrls(
            id,
            [
                own,
                own,
                $"{PublicBase}properties/{Guid.NewGuid()}/photos/other-property.jpg",
                $"{PublicBase}properties/{id}/documents/passport.pdf",
                $"{PublicBase}properties/{id}/floorplans/plan.png",
                $"{PublicBase}properties/{id}/photos/",
                $"https://evil.example.com/properties/{id}/photos/x.jpg",
                $"properties/{id}/photos/legacy.jpg",
                $"http://storage.test/public/properties/{id}/photos/not-https.jpg",
                "",
            ]);

        Assert.Equal([own], urls);
    }

    [Fact]
    public async Task ListingPhotoSync_SendsOnlyTheListingPhotos_AndEachOnce()
    {
        var propertyId = await SeedPropertyAsync(OrgA, photoCount: 2);
        await using var db = NewDb();
        var property = await db.Properties.AsNoTracking().SingleAsync(p => p.Id == propertyId);
        property.PhotoUrls = [.. property.PhotoUrls, $"{PublicBase}properties/{Guid.NewGuid()}/photos/foreign.jpg", "https://evil.example.com/p.jpg"];
        var uploaded = new List<string>();
        _client
            .Setup(c => c.UploadPhotoAsync(Accommodation, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, url, _) => uploaded.Add(url))
            .ReturnsAsync((string _, string url, CancellationToken _) => new OctoratePhotoQueueEntry($"q-{uploaded.Count}", OctoratePhotoQueueState.Waiting, null, null));
        var sync = new ListingPhotoSync(db, _client.Object, new PublicPhotoUrlService(_storage.Object), _clock, NullLogger<ListingPhotoSync>.Instance);

        await sync.SendAsync(property, Accommodation, CancellationToken.None);
        await sync.SendAsync(property, Accommodation, CancellationToken.None);

        Assert.Equal(2, uploaded.Count);
        Assert.All(uploaded, url => Assert.StartsWith($"{PublicBase}properties/{propertyId}/photos/", url, StringComparison.Ordinal));
        Assert.Equal(2, await NewDb().PhotoUploadTasks.CountAsync(t => t.PropertyId == propertyId && t.Status == PhotoUploadStatus.Uploading));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListingPublish_PropertyOutsideReach_Refused_Test(bool anotherOrg)
    {
        // A property of another org is not even visible to the caller (the tenant filter); one of its own org that it does not reach is refused by the reach.
        var propertyId = await SeedPropertyAsync(OrgA);
        _scope
            .Setup(s => s.CanReachPropertyAsync("auth0|only-some", It.IsAny<IReadOnlySet<string>>(), OrgA, It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var tenant = anotherOrg ? new FixedTenant(OrgB) : new FixedTenant(OrgA);
        await using var db = NewDb(tenant);

        await Assert.ThrowsAsync<NotFoundException>(
            () => Service(db).PublishAsync(propertyId, ["booking_xml"], false, anotherOrg ? Host : "auth0|only-some", CancellationToken.None));

        _jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
        await using var check = NewDb();
        Assert.Empty(await check.ListingChannelStates.ToListAsync());
    }

    [Fact]
    public async Task PublishAsync_AReadyProperty_QueuesTheJobAndAnswersTheRowsSending()
    {
        var propertyId = await SeedPropertyAsync(OrgA);
        await using var db = NewDb(new FixedTenant(OrgA));

        var rows = await Service(db).PublishAsync(propertyId, ["homeaway", "Booking_XML", "booking_xml"], false, Host, CancellationToken.None);

        Assert.Equal(["booking_xml", "homeaway"], rows.Select(r => r.ChannelCode));
        Assert.All(rows, r => Assert.Equal(ChannelPublishStatus.Sending, r.Status));
        _jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once);
        await using var check = NewDb();
        Assert.Equal(2, await check.ListingChannelStates.CountAsync(s => s.Status == ChannelPublishStatus.Sending));
    }

    [Fact]
    public async Task PublishAsync_AChannelChangedOutsideCasaZen_IsRefusedUntilThePersonChoosesToOverwrite()
    {
        // NF-27: nothing is overwritten silently. A publication refused for an unresolved change is the only outcome until the person keeps CasaZen's
        // version (overwrite); the other channel is not put in sending by a publication that was refused.
        var propertyId = await SeedPropertyAsync(OrgA);
        await using (var seed = NewDb())
        {
            seed.ListingChannelStates.Add(new ListingChannelState
            {
                OrgId = OrgA,
                PropertyId = propertyId,
                ChannelCode = "booking_xml",
                Status = ChannelPublishStatus.Published,
                ExternalChangeAt = Now.UtcDateTime.AddHours(-2),
            });
            await seed.SaveChangesAsync();
        }

        await using var db = NewDb(new FixedTenant(OrgA));
        var ex = await Assert.ThrowsAsync<DomainConflictException>(
            () => Service(db).PublishAsync(propertyId, ["booking_xml", "homeaway"], false, Host, CancellationToken.None));

        Assert.Equal("listing_modified_outside", ex.Code);
        Assert.Equal(["booking_xml"], Assert.IsAssignableFrom<IEnumerable<string>>(ex.Extensions["channels"]));
        _jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
        await using (var check = NewDb())
        {
            var only = await check.ListingChannelStates.SingleAsync();
            Assert.Equal(ChannelPublishStatus.Published, only.Status);
        }

        var rows = await Service(db).PublishAsync(propertyId, ["booking_xml"], true, Host, CancellationToken.None);

        Assert.Equal(ChannelPublishStatus.Sending, Assert.Single(rows).Status);
        _jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_WithNoChannelOrAnUnknownOne_IsRefused_NeverReadAsAll()
    {
        var propertyId = await SeedPropertyAsync(OrgA);
        await using var db = NewDb(new FixedTenant(OrgA));

        var none = await Assert.ThrowsAsync<DomainRuleException>(() => Service(db).PublishAsync(propertyId, [], false, Host, CancellationToken.None));
        var unknown = await Assert.ThrowsAsync<DomainRuleException>(() => Service(db).PublishAsync(propertyId, ["expedia"], false, Host, CancellationToken.None));

        Assert.Equal("listing_channels_required", none.Code);
        Assert.Equal("listing_channel_unknown", unknown.Code);
        _jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact]
    public async Task PublishAsync_WithoutTheEnglishText_IsRefusedWithTheMissingOnes()
    {
        var propertyId = await SeedPropertyAsync(OrgA, englishTexts: false);
        await using var db = NewDb(new FixedTenant(OrgA));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => Service(db).PublishAsync(propertyId, ["booking_xml"], false, Host, CancellationToken.None));

        Assert.Equal("listing_content_incomplete", ex.Code);
        Assert.Equal(["en.title", "en.description"], Assert.IsAssignableFrom<IEnumerable<string>>(ex.Extensions["missing"]));
        _jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact]
    public async Task PublishAsync_APropertyWithNoMappingThatWasNotDeclaredNew_IsRefused()
    {
        var propertyId = await SeedPropertyAsync(OrgA, mapped: false);
        _scenarios
            .Setup(s => s.EnsureNewInventoryAsync(OrgA, propertyId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DomainConflictException("octorate_existing_inventory_must_import", "OctorateExistingInventoryMustImport"));
        await using var db = NewDb(new FixedTenant(OrgA));

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => Service(db).PublishAsync(propertyId, ["booking_xml"], false, Host, CancellationToken.None));

        Assert.Equal("octorate_existing_inventory_must_import", ex.Code);
        _creator.Verify(c => c.CreateAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlySet<string>?>()), Times.Never);
        _jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_AChannelWithoutWhatItAsksFor_IsLeftToFixAndTheOthersGoOn()
    {
        // AC-NF29-3, AC-NF29-4: a missing CIN blocks only the channels that ask for it; the other channel is accepted and its content is the baseline.
        var propertyId = await SeedPropertyAsync(OrgA, cin: null, photoCount: 4);
        await SeedRowsAsync(propertyId, "booking_xml", "homeaway");
        _client
            .Setup(c => c.GetConnectionsAsync(Accommodation, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new OctorateChannelConnection("11", "142", "Booking", "h-1", "CONNECTED"), new OctorateChannelConnection("12", "235", "Vrbo", "h-2", "CONNECTED")]);
        _client
            .Setup(c => c.UploadPhotoAsync(Accommodation, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctoratePhotoQueueEntry("q-1", OctoratePhotoQueueState.Waiting, null, null));
        _client
            .Setup(c => c.PushChannelAsync(Accommodation, Product, It.Is<OctorateChannelTarget>(t => t.PortalCode == "homeaway"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateChannelPush("proc-9"));
        _client.Setup(c => c.GetPropertyAsync(Accommodation, It.IsAny<CancellationToken>())).ReturnsAsync(new OctorateProperty { Id = Accommodation, Name = "Casa del Lago" });
        _client.Setup(c => c.GetContentRoomsAsync(Accommodation, Product, It.IsAny<CancellationToken>())).ReturnsAsync(new List<OctorateContentRoom>());
        await using var db = NewDb();

        await Service(db).RunAsync(propertyId, ["booking_xml", "homeaway"], false, Host, null, CancellationToken.None);

        await using var check = NewDb();
        var booking = await check.ListingChannelStates.SingleAsync(s => s.ChannelCode == "booking_xml");
        var vrbo = await check.ListingChannelStates.SingleAsync(s => s.ChannelCode == "homeaway");
        Assert.Equal(ChannelPublishStatus.ToFix, booking.Status);
        Assert.Equal(ListingChannelErrors.CinMissing, booking.ErrorCode);
        Assert.Equal(ChannelPublishStatus.Accepted, vrbo.Status);
        Assert.Null(vrbo.ErrorCode);
        Assert.Equal(1, vrbo.ContentVersion);
        Assert.Equal(Now.UtcDateTime, vrbo.LastSyncedAt);
        var baseline = ListingChannelBaseline.Parse(vrbo.ContentHashesJson);
        Assert.Equal("proc-9", baseline.ProcessId);
        Assert.Equal(ListingContentGroups.All.Order(), baseline.Hashes.Keys.Order());
        _client.Verify(c => c.PushChannelAsync(Accommodation, Product, It.Is<OctorateChannelTarget>(t => t.PortalCode == "booking_xml"), It.IsAny<CancellationToken>()), Times.Never);
        _activities.Verify(
            a => a.CreateAsync(It.Is<CreateTodoRequest>(r => r.Type == ActivityType.PublicationError && r.EntityId == booking.Id), It.IsAny<CancellationToken>()),
            Times.Once);
        _recorder.Verify(
            r => r.Record(DomainEventType.ListingPublished, propertyId, It.IsAny<object>(), It.IsAny<string?>(), OrgA),
            Times.Once);
        Assert.DoesNotContain(
            _client.Invocations,
            call => call.Method.Name is nameof(IOctorateApiClient.CreatePropertyAsync));
    }

    private ContentPushService Service(AppDbContext db)
    {
        var urls = new PublicPhotoUrlService(_storage.Object);
        return new ContentPushService(
            db,
            _client.Object,
            new OctorateOrgScope(NullTenantContext.Instance),
            _mappings.Object,
            _scenarios.Object,
            _creator.Object,
            _scope.Object,
            _flags.Object,
            _recorder.Object,
            _jobs.Object,
            new OctorateContentReader(_client.Object),
            new ListingPhotoSync(db, _client.Object, urls, _clock, NullLogger<ListingPhotoSync>.Instance),
            urls,
            new ListingTodos(db, _activities.Object),
            Microsoft.Extensions.Options.Options.Create(Channels()),
            _clock,
            NullLogger<ContentPushService>.Instance);
    }

    /// <summary>Booking.com and Vrbo with a push queue; Booking.com asks for a CIN, Vrbo does not.</summary>
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

    private async Task SeedRowsAsync(Guid propertyId, params string[] channels)
    {
        await using var db = NewDb();
        foreach (var channel in channels)
            db.ListingChannelStates.Add(new ListingChannelState { OrgId = OrgA, PropertyId = propertyId, ChannelCode = channel, Status = ChannelPublishStatus.Sending });

        await db.SaveChangesAsync();
    }

    /// <summary>A property with its Italian texts and, by default, the English ones, photos in its own folder, and a confirmed mapping with a room.</summary>
    private async Task<Guid> SeedPropertyAsync(
        Guid orgId,
        bool englishTexts = true,
        int photoCount = 5,
        string? cin = "IT058091C27G5FFZDZ",
        bool mapped = true)
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
            CinCode = cin,
        };
        property.PhotoUrls = [.. Enumerable.Range(1, photoCount).Select(i => $"{PublicBase}properties/{property.Id}/photos/p{i}.jpg")];
        if (englishTexts)
        {
            var document = new SitePropertyProfileDocument
            {
                Translations = new Dictionary<string, ListingTexts> { ["en"] = new ListingTexts(Title: "House on the lake", Description: "A bright flat on the lake.") },
            };
            property.SiteProfileJson = JsonSerializer.Serialize(
                document,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        }

        db.Properties.Add(property);
        if (mapped)
        {
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
        }

        await db.SaveChangesAsync();
        return property.Id;
    }

    private sealed class FixedTenant(Guid orgId) : ITenantContext
    {
        public Guid? OrgId => orgId;

        public bool FilterEnabled => true;
    }
}
