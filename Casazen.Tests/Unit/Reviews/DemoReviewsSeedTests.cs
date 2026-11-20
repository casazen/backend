using Casazen.Core.Assistant;
using Casazen.Core.Demo;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Messages;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Reviews;

/// <summary>
/// E05/F06/US01/T03 (demo-seed rule): the reviews the showcase writes for the hub, written by the real seed code over the houses and stays of the catalog (the markers the reset leaves) and read back
/// by the hub itself: a review of a direct stay on the booking site and one of an Airbnb stay, each with the draft of its reply waiting in the approval queue, marked as showcase data so the reset deletes
/// them, with their drafts, and only them.
/// </summary>
public class DemoReviewsSeedTests : IAsyncLifetime
{
    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private HostScopeWorld _world = null!;
    private Guid _directBookingId;
    private Guid _channelBookingId;

    public async Task InitializeAsync()
    {
        _world = await HostScopeScenario.SeedAsync(_db);
        var orgId = _world.OrgId;

        // The two past stays the reviews are about, as the reset creates them: a booking of the public checkout and one that came from Airbnb, both of the house `appartamento-brera`.
        var direct = Stay(orgId, _world.Granted.Id, BookingSource.Direct, HostScopeScenario.Day(2026, 8, 20));
        var channel = Stay(orgId, _world.Granted.Id, BookingSource.Airbnb, HostScopeScenario.Day(2026, 9, 10));
        _db.Guests.AddRange(direct.Guest, channel.Guest);
        _db.Bookings.AddRange(direct.Booking, channel.Booking);
        _directBookingId = direct.Booking.Id;
        _channelBookingId = channel.Booking.Id;

        _db.TestDataMarkers.AddRange(
            Marker(orgId, TestDataEntities.Property, _world.Granted.Id, "appartamento-brera"),
            Marker(orgId, TestDataEntities.Booking, direct.Booking.Id, "tassa-da-versare"),
            Marker(orgId, TestDataEntities.Booking, channel.Booking.Id, "tassa-senza-importo"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private static (Guest Guest, Booking Booking) Stay(Guid orgId, Guid propertyId, BookingSource source, DateTime checkIn)
    {
        var guest = new Guest { OrgId = orgId, FirstName = "Omar", LastName = "Haddad", Email = $"ospite.demo.{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            OrgId = orgId,
            PropertyId = propertyId,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(2),
            NumberOfGuests = 2,
            Status = BookingStatus.CheckedOut,
            Source = source,
            BasePrice = 280m,
            TotalPrice = 280m,
        };
        return (guest, booking);
    }

    private static TestDataMarker Marker(Guid orgId, string entityType, Guid entityId, string templateKey) => new()
    {
        OrgId = orgId,
        EntityType = entityType,
        EntityId = entityId,
        Origin = TestDataOrigins.Showcase,
        TemplateKey = templateKey,
    };

    private DemoEnvironmentService Seeder() => new(
        _db,
        Mock.Of<IFileStorage>(),
        Mock.Of<IPropertyComplianceStatusService>(),
        Options.Create(new ComplianceOptions()),
        Mock.Of<IDemoEnvironmentGate>(),
        NullLogger<DemoEnvironmentService>.Instance,
        new FixedTimeProvider(MessageHubTestKit.Now),
        Mock.Of<IServiceScopeFactory>(),
        Mock.Of<ICrmContactIndex>(),
        Mock.Of<IFeatureFlags>(),
        Mock.Of<IHostEnvironment>(e => e.EnvironmentName == Environments.Development));

    private async Task SeedAllAsync()
    {
        var seeder = Seeder();
        foreach (var template in DemoShowcaseCatalog.Reviews)
            await seeder.CreateReviewAsync(_world.OrgId, template, CancellationToken.None);
    }

    // ─── The catalog ───

    [Fact]
    public void TheCatalog_HasAReviewOfADirectStayAndOneOfAnAirbnbStay_EachWithItsDraft_AllSayingTest()
    {
        var reviews = DemoShowcaseCatalog.Reviews;
        Assert.Contains(DemoShowcaseCatalog.Scenario.Reviews, DemoShowcaseCatalog.Scenarios);
        Assert.All(reviews, r => Assert.Equal(DemoShowcaseCatalog.Scenario.Reviews, r.Scenario));
        Assert.Equal(reviews.Count, reviews.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count());
        var direct = Assert.Single(reviews, r => r.Kind == DemoReviewKind.Direct);
        var airbnb = Assert.Single(reviews, r => r.Kind == DemoReviewKind.Airbnb);
        Assert.Equal(2, reviews.Count);

        foreach (var review in reviews)
        {
            var property = Assert.Single(DemoShowcaseCatalog.Properties, p => p.Key == review.PropertyKey);
            var booking = Assert.Single(property.Bookings, b => b.Key == review.BookingKey);

            // The stay is over, of the right kind, and the guest is at example.com; the words and the draft say TEST and keep the rules of a reply.
            Assert.Equal(BookingStatus.CheckedOut, booking.Status);
            Assert.EndsWith("@example.com", booking.Email, StringComparison.Ordinal);
            Assert.Contains("TEST", review.Text, StringComparison.Ordinal);
            Assert.Contains("TEST", review.DraftReply, StringComparison.Ordinal);
            Assert.InRange(review.Rating, 1m, 5m);
            Assert.Null(ReviewReplyPromptBuilder.Violation(review.DraftReply));
            Assert.True(review.DraftReply.Length <= ReviewRules.ReplyMaxLength);
            Assert.True(review.Text.Length <= ReviewRules.TextMaxLength);

            // Published after the stay ended, and before the reset.
            var endedDaysAgo = -(booking.CheckInOffsetDays + booking.Nights);
            Assert.InRange(review.PublishedDaysAgo, 0, endedDaysAgo);
        }

        Assert.Equal(BookingSource.Direct, DemoShowcaseCatalog.Properties.SelectMany(p => p.Bookings).Single(b => b.Key == direct.BookingKey).Source);
        Assert.True(FiscalCopy.IsOtaBookingSource(DemoShowcaseCatalog.Properties.SelectMany(p => p.Bookings).Single(b => b.Key == airbnb.BookingKey).Source));
        Assert.Equal(Math.Truncate(direct.Rating), direct.Rating);
    }

    // ─── The seed ───

    [Fact]
    public async Task TheShowcase_WritesTheTwoReviewsAndTheirDrafts_ThatAgreeWithTheTemplates_AndMarksThem()
    {
        await SeedAllAsync();
        var now = MessageHubTestKit.Now.UtcDateTime;
        var directTemplate = Assert.Single(DemoShowcaseCatalog.Reviews, r => r.Kind == DemoReviewKind.Direct);
        var airbnbTemplate = Assert.Single(DemoShowcaseCatalog.Reviews, r => r.Kind == DemoReviewKind.Airbnb);

        // The review of the booking site: as the guest would have sent it, with no answer of the host.
        var site = await _db.Reviews.AsNoTracking().SingleAsync(r => r.BookingId == _directBookingId);
        Assert.Equal(_world.Granted.Id, site.PropertyId);
        Assert.Equal((int)directTemplate.Rating, site.Rating);
        Assert.Equal(directTemplate.Text, site.Text);
        Assert.Equal(ReviewRules.AnonymousAuthorName, site.AuthorName);
        Assert.Equal(ReviewStatus.Published, site.Status);
        Assert.Equal(now.AddDays(-directTemplate.PublishedDaysAgo), site.PublishedAt);
        Assert.Equal(RomeCalendar.DateInRome(HostScopeScenario.Day(2026, 8, 22)).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), site.StayEndedOn);
        Assert.Null(site.HostReply);

        // The review of the channel: the row the sync would write, with no channel manager behind it.
        var channel = await _db.OtaReviews.AsNoTracking().SingleAsync(r => r.BookingId == _channelBookingId);
        Assert.Equal(OtaChannel.Airbnb, channel.Channel);
        Assert.True(channel.GuestReviewPublished);
        Assert.Equal(airbnbTemplate.Rating, channel.GuestRating);
        Assert.Equal(airbnbTemplate.Text, channel.GuestReviewText);
        Assert.Equal(now.AddDays(-airbnbTemplate.PublishedDaysAgo), channel.GuestReviewPublishedAt);
        Assert.StartsWith("TEST-", channel.OctorateRefer, StringComparison.Ordinal);
        Assert.Equal(OtaReviewActionState.None, channel.HostReplyState);
        Assert.Equal(HostReviewWindow.Open, channel.HostReviewWindow);
        Assert.Null(channel.NextPollAt);

        // The drafts wait in the approval queue, written as the assistant would have written them, never sent.
        var items = await _db.ApprovalQueueItems.AsNoTracking().Where(i => i.ItemType == ApprovalItemTypes.ReviewReply).ToListAsync();
        Assert.Equal(2, items.Count);
        Assert.All(items, i =>
        {
            Assert.Equal(_world.OrgId, i.OrgId);
            Assert.Equal(ApprovalStatus.Pending, i.Status);
            Assert.True(i.IsAiGenerated);
            Assert.Equal(ApprovalOriginActions.SendReviewReply, i.OriginAction);
            Assert.Equal(ApprovalOriginActionStatus.Pending, i.OriginActionStatus);
            Assert.Equal(_world.Granted.Id, i.PropertyId);
            Assert.Equal(now.AddDays(ApprovalQueueRules.ExpirationDays), i.ExpiresAt);
        });
        var siteItem = Assert.Single(items, i => i.ItemId == site.Id);
        var channelItem = Assert.Single(items, i => i.ItemId == channel.Id);
        Assert.Equal(MessageThreadKey.ForReview(site.Id).ToString(), siteItem.ThreadKey);
        Assert.Equal(MessageThreadKey.ForOtaReview(channel.Id).ToString(), channelItem.ThreadKey);
        Assert.Equal(directTemplate.DraftReply, siteItem.ContentText);
        Assert.Equal(airbnbTemplate.DraftReply, channelItem.ContentText);

        // Marked as showcase data, one marker for each row.
        var markers = await _db.TestDataMarkers.AsNoTracking()
            .Where(m => m.EntityType == TestDataEntities.Review || m.EntityType == TestDataEntities.OtaReview || m.EntityType == TestDataEntities.ApprovalQueueItem)
            .ToListAsync();
        Assert.Equal(4, markers.Count);
        Assert.All(markers, m => Assert.Equal(TestDataOrigins.Showcase, m.Origin));
        Assert.Contains(markers, m => m.EntityType == TestDataEntities.Review && m.EntityId == site.Id && m.TemplateKey == directTemplate.Key);
        Assert.Contains(markers, m => m.EntityType == TestDataEntities.OtaReview && m.EntityId == channel.Id && m.TemplateKey == airbnbTemplate.Key);
        Assert.Equal(2, markers.Count(m => m.EntityType == TestDataEntities.ApprovalQueueItem));
    }

    [Fact]
    public async Task TheHub_ListsTheTwoDemoReviews_AsReviewsToAnswer_AndOpensThemBothWithTheirWords()
    {
        await SeedAllAsync();
        var hub = new MessageHubService(
            _db,
            Mock.Of<IGuestMessageService>(),
            Mock.Of<IChannelMappingService>(),
            Mock.Of<IOtaAttachmentFetcher>(),
            NullLogger<MessageHubService>.Instance,
            new FixedTimeProvider(MessageHubTestKit.Now));

        var page = await hub.ListAsync(_world.OrgWide, new MessageHubFilter(Kind: MessageHubThreadKind.Review), 1, 20);
        var site = await _db.Reviews.AsNoTracking().SingleAsync(r => r.BookingId == _directBookingId);
        var detail = await hub.GetReviewThreadAsync(_world.OrgWide, MessageThreadKey.ForReview(site.Id), includeAmounts: false);

        Assert.Equal(2, page.TotalCount);
        Assert.All(page.Items, i =>
        {
            Assert.Equal(MessageHubThreadKind.Review, i.Kind);
            Assert.True(i.NeedsReply);
            Assert.Contains("TEST", i.Preview, StringComparison.Ordinal);
            Assert.Equal(_world.Granted.Id, i.PropertyId);
        });
        Assert.Contains("TEST", detail.Text, StringComparison.Ordinal);
        Assert.True(detail.Capabilities.CanReply);

        // The default view has no review.
        Assert.Empty((await hub.ListAsync(_world.OrgWide, new MessageHubFilter(), 1, 20)).Items);
    }

    [Fact]
    public async Task TheReset_DeletesTheShowcaseReviewsAndTheirDrafts_ByTheirMarkers_AndNothingElse()
    {
        await SeedAllAsync();

        // A review the guest left and a draft the host decided, with no marker, in this org; a marked review of another org.
        var theirs = Stay(_world.OrgId, _world.Hidden.Id, BookingSource.Direct, HostScopeScenario.Day(2026, 9, 1));
        var otherOrgGuest = Stay(_world.OtherOrgId, _world.OtherOrgProperty.Id, BookingSource.Direct, HostScopeScenario.Day(2026, 9, 1));
        _db.Guests.AddRange(theirs.Guest, otherOrgGuest.Guest);
        _db.Bookings.AddRange(theirs.Booking, otherOrgGuest.Booking);
        var real = new Review
        {
            OrgId = _world.OrgId,
            PropertyId = _world.Hidden.Id,
            BookingId = theirs.Booking.Id,
            Rating = 4,
            Text = "Una recensione vera",
            AuthorName = "Ospite",
            StayEndedOn = HostScopeScenario.Day(2026, 9, 3),
            PublishedAt = MessageHubTestKit.Now.UtcDateTime,
        };
        var foreign = new Review
        {
            OrgId = _world.OtherOrgId,
            PropertyId = _world.OtherOrgProperty.Id,
            BookingId = otherOrgGuest.Booking.Id,
            Rating = 5,
            Text = "Un'altra organizzazione",
            AuthorName = "Ospite",
            StayEndedOn = HostScopeScenario.Day(2026, 9, 3),
            PublishedAt = MessageHubTestKit.Now.UtcDateTime,
        };
        _db.Reviews.AddRange(real, foreign);
        _db.ApprovalQueueItems.Add(new ApprovalQueueItem
        {
            OrgId = _world.OrgId,
            ItemType = ApprovalItemTypes.ReviewReply,
            ItemId = real.Id,
            ContentText = "Grazie!",
            Status = ApprovalStatus.Pending,
            ThreadKey = MessageThreadKey.ForReview(real.Id).ToString(),
            CreatedAt = MessageHubTestKit.Now.UtcDateTime,
            ExpiresAt = MessageHubTestKit.Now.UtcDateTime.AddDays(7),
        });
        _db.TestDataMarkers.Add(Marker(_world.OtherOrgId, TestDataEntities.Review, foreign.Id, "recensione-diretta"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await Seeder().RemoveShowcaseReviewsAsync(_world.OrgId, CancellationToken.None);

        Assert.Empty(await _db.Reviews.AsNoTracking().Where(r => r.BookingId == _directBookingId).ToListAsync());
        Assert.Empty(await _db.OtaReviews.AsNoTracking().ToListAsync());
        var left = await _db.ApprovalQueueItems.AsNoTracking().Where(i => i.ItemType == ApprovalItemTypes.ReviewReply).ToListAsync();
        Assert.Equal(real.Id, Assert.Single(left).ItemId);
        Assert.Empty(await _db.TestDataMarkers.AsNoTracking()
            .Where(m => m.OrgId == _world.OrgId
                        && (m.EntityType == TestDataEntities.Review || m.EntityType == TestDataEntities.OtaReview || m.EntityType == TestDataEntities.ApprovalQueueItem))
            .ToListAsync());

        // What is not the showcase's is untouched, in this org and in the other.
        Assert.NotNull(await _db.Reviews.AsNoTracking().SingleOrDefaultAsync(r => r.Id == real.Id));
        Assert.NotNull(await _db.Reviews.AsNoTracking().SingleOrDefaultAsync(r => r.Id == foreign.Id));
        Assert.Single(await _db.TestDataMarkers.AsNoTracking().Where(m => m.OrgId == _world.OtherOrgId && m.EntityType == TestDataEntities.Review).ToListAsync());

        // The next reset writes them again: removing and seeding is the cycle of a reset.
        await SeedAllAsync();
        var again = await _db.ApprovalQueueItems.AsNoTracking().Where(i => i.ItemType == ApprovalItemTypes.ReviewReply && i.OrgId == _world.OrgId).ToListAsync();
        Assert.Equal(3, again.Count);
    }

    [Fact]
    public async Task AReviewWhoseHouseOrStayWasNotCreated_IsSkipped_NothingIsWritten()
    {
        var seeder = Seeder();

        await seeder.CreateReviewAsync(_world.OrgId, new DemoReviewTemplate("senza-casa", DemoReviewKind.Direct, "casa-che-non-c-e", "tassa-da-versare", 5m, "Bello (TEST)", 10, "Grazie (TEST)", DemoShowcaseCatalog.Scenario.Reviews), CancellationToken.None);
        await seeder.CreateReviewAsync(_world.OrgId, new DemoReviewTemplate("senza-soggiorno", DemoReviewKind.Airbnb, "appartamento-brera", "soggiorno-che-non-c-e", 5m, "Bello (TEST)", 10, "Grazie (TEST)", DemoShowcaseCatalog.Scenario.Reviews), CancellationToken.None);

        Assert.Empty(await _db.Reviews.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.OtaReviews.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.ApprovalQueueItems.AsNoTracking().Where(i => i.ItemType == ApprovalItemTypes.ReviewReply).ToListAsync());
    }
}
