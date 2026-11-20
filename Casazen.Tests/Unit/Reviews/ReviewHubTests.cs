using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Messages;
using Casazen.Tests.Unit.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Reviews;

/// <summary>
/// The reviews in the hub of messages (E05/F06/US01/T03, NF-69): listed under <c>kind=review</c> next to the conversations, both sources (the booking site and a channel) in one paged statement,
/// newest first, narrowed to the reach of the caller, never in the default view; and opened with their own detail (the stars, the words, the reply and the host's review with their states, the booking
/// card, what the channel allows). A review is not a conversation: its key is a 404 for the endpoints of a conversation, and theirs for the detail of a review.
/// </summary>
public class ReviewHubTests
{
    private const string User = "auth0|titolare";

    private sealed record Seeded(
        OtaReviewHarness H,
        OtaReview Granted,
        OtaReview Hidden,
        Review Site,
        ReviewStay SiteStay,
        OtaReview Unpublished,
        OtaReview Other);

    /// <summary>
    /// Four published reviews and a row that is not one: the review of a stay of the booking site (an hour ago) and the Airbnb review of the granted house (two hours ago) and of the hidden one (five
    /// hours ago), both of the org; the Airbnb review of another org; and a read that found no review on a stay of the granted house.
    /// </summary>
    private static async Task<Seeded> SeedAsync()
    {
        var h = await OtaReviewHarness.CreateAsync();
        var orgId = h.Granted.OrgId;

        h.ChannelHasReview(h.Granted, "Posto bellissimo, host gentilissimo. " + new string('x', 150), 5m);
        var granted = await h.SyncAsync(h.Granted);
        await h.SetPublishedAtAsync(h.Granted, 120);

        h.ChannelHasReview(h.Hidden, "Casa pulita ma rumorosa.", 3m);
        var hidden = await h.SyncAsync(h.Hidden);
        await h.SetPublishedAtAsync(h.Hidden, 300);

        var siteStay = await h.AddStayAsync(orgId, h.Granted.PropertyId, "585009", "HMDIRECT01", BookingSource.Direct);
        var site = await h.AddSiteReviewAsync(siteStay, "Soggiorno piacevole.", 4, publishedMinutesAgo: 60);

        var quiet = await h.AddStayAsync(orgId, h.Granted.PropertyId, OtaReviewHarness.GrantedAccommodation, "HMQUIET001");
        h.ChannelHasNothing(quiet);
        var unpublished = await h.SyncAsync(quiet);

        h.ChannelHasReview(h.Other, "Un segreto dell'altra organizzazione", 5m);
        var other = await h.SyncAsync(h.Other);

        return new Seeded(h, granted, hidden, site, siteStay, unpublished, other);
    }

    private static MessageHubService Hub(OtaReviewHarness h, AppDbContext db) =>
        new(
            db,
            Mock.Of<IGuestMessageService>(),
            h.Mappings.Object,
            Mock.Of<IOtaAttachmentFetcher>(),
            NullLogger<MessageHubService>.Instance,
            h.Clock,
            h.Service(db));

    private static async Task<MessageHubPage> ListAsync(OtaReviewHarness h, Casazen.Core.Authorization.HostScope scope, MessageHubFilter filter, int page = 1, int pageSize = 20)
    {
        await using var db = h.NewDb();
        return await Hub(h, db).ListAsync(scope, filter, page, pageSize);
    }

    private static async Task<ReviewThreadView> DetailAsync(OtaReviewHarness h, Casazen.Core.Authorization.HostScope scope, MessageThreadKey key, bool includeAmounts = false)
    {
        await using var db = h.NewDb();
        return await Hub(h, db).GetReviewThreadAsync(scope, key, includeAmounts);
    }

    private static readonly MessageHubFilter ReviewsOnly = new(Kind: MessageHubThreadKind.Review);

    // ─── The list ───

    [Fact]
    public async Task List_KindReview_ListsTheReviewsOfBothSources_NewestFirst_EachAsAHubItem()
    {
        var s = await SeedAsync();

        var page = await ListAsync(s.H, s.H.World.OrgWide, ReviewsOnly);

        // The published ones of the org: the booking site an hour ago, the granted house two hours ago, the hidden house five hours ago. Not the unpublished row, not the other org.
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(
            [MessageThreadKey.ForReview(s.Site.Id), MessageThreadKey.ForOtaReview(s.Granted.Id), MessageThreadKey.ForOtaReview(s.Hidden.Id)],
            page.Items.Select(i => i.Key));

        var site = page.Items[0];
        Assert.Equal(MessageThreadSource.Review, site.Source);
        Assert.Equal(MessageHubChannel.Direct, site.Channel);
        Assert.Equal(MessageHubThreadKind.Review, site.Kind);
        Assert.Equal(MessageHubThreadState.Open, site.State);
        Assert.Equal(s.SiteStay.PropertyId, site.PropertyId);
        Assert.Equal(s.H.World.Granted.Name, site.PropertyName);
        Assert.False(site.Unassigned);
        Assert.Equal(s.SiteStay.BookingId, site.BookingId);
        Assert.False(string.IsNullOrWhiteSpace(site.BookingCode));
        Assert.Equal("Giulietta Verdi", site.GuestName);
        Assert.Equal(OtaReviewHarness.Now.UtcDateTime.AddMinutes(-60), site.LastMessageAt);
        Assert.Equal(MessageOrigin.Guest, site.LastMessageOrigin);
        Assert.Equal("Soggiorno piacevole.", site.Preview);
        Assert.Equal(0, site.UnreadCount);
        Assert.True(site.NeedsReply);
        Assert.Equal(4m, site.Rating);
        Assert.NotNull(site.CheckInDate);
        Assert.NotNull(site.CheckOutDate);

        var airbnb = page.Items[1];
        Assert.Equal(MessageThreadSource.OtaReview, airbnb.Source);
        Assert.Equal(MessageHubChannel.Airbnb, airbnb.Channel);
        Assert.Equal(5m, airbnb.Rating);
        Assert.StartsWith("Posto bellissimo", airbnb.Preview, StringComparison.Ordinal);
        Assert.EndsWith("…", airbnb.Preview, StringComparison.Ordinal);
        Assert.Equal(ReviewRules.PreviewLength + 1, airbnb.Preview?.Length);
        Assert.True(airbnb.NeedsReply);
        Assert.Equal(s.H.Granted.BookingId, airbnb.BookingId);
    }

    [Fact]
    public async Task List_IsPagedInTheDatabase_AndTheDefaultViewNeverHasAReview()
    {
        var s = await SeedAsync();

        var first = await ListAsync(s.H, s.H.World.OrgWide, ReviewsOnly, page: 1, pageSize: 2);
        var second = await ListAsync(s.H, s.H.World.OrgWide, ReviewsOnly, page: 2, pageSize: 2);

        Assert.Equal(3, first.TotalCount);
        Assert.Equal(2, first.Items.Count);
        Assert.Equal([MessageThreadKey.ForOtaReview(s.Hidden.Id)], second.Items.Select(i => i.Key));

        // The default view is the conversations, and the other filters of the list never turn up a review.
        foreach (var filter in new[]
                 {
                     new MessageHubFilter(),
                     new MessageHubFilter(NeedsReply: true),
                     new MessageHubFilter(Kind: MessageHubThreadKind.Reservation),
                     new MessageHubFilter(Kind: MessageHubThreadKind.Request),
                     new MessageHubFilter(Channel: MessageHubChannel.Airbnb),
                 })
        {
            var page = await ListAsync(s.H, s.H.World.OrgWide, filter);

            Assert.Empty(page.Items);
            Assert.Equal(0, page.TotalCount);
        }
    }

    [Fact]
    public async Task List_NeedsReply_IsPublishedAndNotAnswered_AReplyOfAChannelTakesTheReviewOutOfIt()
    {
        var s = await SeedAsync();
        await using (var db = s.H.NewDb())
        using (s.H.Scope.Use(s.Granted.OrgId))
            await s.H.Service(db).ReplyAsync(s.Granted.Id, "Grazie di cuore!", "reply-key-0001", User);

        var needs = await ListAsync(s.H, s.H.World.OrgWide, ReviewsOnly with { NeedsReply = true });
        var answered = await ListAsync(s.H, s.H.World.OrgWide, ReviewsOnly with { NeedsReply = false });

        Assert.Equal(
            [MessageThreadKey.ForReview(s.Site.Id), MessageThreadKey.ForOtaReview(s.Hidden.Id)],
            needs.Items.Select(i => i.Key));
        Assert.Equal([MessageThreadKey.ForOtaReview(s.Granted.Id)], answered.Items.Select(i => i.Key));
    }

    [Fact]
    public async Task List_TheOtherFiltersCombineWithTheKind_AndNothingSearchesTheWordsOfAReview()
    {
        var s = await SeedAsync();
        var org = s.H.World.OrgWide;

        var ofTheGrantedHouse = await ListAsync(s.H, org, ReviewsOnly with { PropertyId = s.H.Granted.PropertyId });
        var airbnb = await ListAsync(s.H, org, ReviewsOnly with { Channel = MessageHubChannel.Airbnb });
        var direct = await ListAsync(s.H, org, ReviewsOnly with { Channel = MessageHubChannel.Direct });
        var ofTheStay = await ListAsync(s.H, org, ReviewsOnly with { BookingId = s.SiteStay.BookingId });
        var byGuest = await ListAsync(s.H, org, ReviewsOnly with { Query = "verdi" });
        var byHouse = await ListAsync(s.H, org, ReviewsOnly with { Query = s.H.World.Granted.Name });
        var byWords = await ListAsync(s.H, org, ReviewsOnly with { Query = "bellissimo" });
        var unread = await ListAsync(s.H, org, ReviewsOnly with { Unread = true });
        var read = await ListAsync(s.H, org, ReviewsOnly with { Unread = false });

        Assert.Equal([MessageThreadKey.ForReview(s.Site.Id), MessageThreadKey.ForOtaReview(s.Granted.Id)], ofTheGrantedHouse.Items.Select(i => i.Key));
        Assert.Equal([MessageThreadKey.ForOtaReview(s.Granted.Id), MessageThreadKey.ForOtaReview(s.Hidden.Id)], airbnb.Items.Select(i => i.Key));
        Assert.Equal([MessageThreadKey.ForReview(s.Site.Id)], direct.Items.Select(i => i.Key));
        Assert.Equal([MessageThreadKey.ForReview(s.Site.Id)], ofTheStay.Items.Select(i => i.Key));
        Assert.Equal(3, byGuest.TotalCount);
        Assert.Equal(2, byHouse.TotalCount);
        Assert.Empty(byWords.Items);
        Assert.Empty(unread.Items);
        Assert.Equal(3, read.TotalCount);
    }

    // ─── Scope and tenant ───

    /// <summary>
    /// PROOF TEST: permission rule (AM-03). A collaborator limited to some properties lists and opens the reviews of the houses it was given and no other: the reviews of the house it was not
    /// given, and of another org, are not in its list and are a 404 when it asks for them (the key of one that is not there and of one that is out of reach answer the same).
    /// </summary>
    [Fact]
    public async Task ReviewHub_Collaborator_SeesOnlyGrantedProperties_Test()
    {
        var s = await SeedAsync();
        var restricted = s.H.World.Restricted;

        var page = await ListAsync(s.H, restricted, ReviewsOnly);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal([MessageThreadKey.ForReview(s.Site.Id), MessageThreadKey.ForOtaReview(s.Granted.Id)], page.Items.Select(i => i.Key));
        Assert.All(page.Items, i => Assert.Equal(s.H.Granted.PropertyId, i.PropertyId));

        // The detail follows the same reach.
        Assert.Equal(s.H.Granted.PropertyId, (await DetailAsync(s.H, restricted, MessageThreadKey.ForOtaReview(s.Granted.Id))).PropertyId);
        Assert.Equal(s.H.Granted.PropertyId, (await DetailAsync(s.H, restricted, MessageThreadKey.ForReview(s.Site.Id))).PropertyId);
        foreach (var hidden in new[] { MessageThreadKey.ForOtaReview(s.Hidden.Id), MessageThreadKey.ForOtaReview(s.Other.Id), MessageThreadKey.ForReview(Guid.NewGuid()) })
        {
            var error = await Assert.ThrowsAsync<NotFoundException>(() => DetailAsync(s.H, restricted, hidden));
            Assert.Equal(MessageHubService.ReviewNotFoundCode, error.Code);
        }

        // The owner of the org reaches the three of the org and none of the other org.
        var all = await ListAsync(s.H, s.H.World.OrgWide, ReviewsOnly);
        Assert.DoesNotContain(all.Items, i => i.Key == MessageThreadKey.ForOtaReview(s.Other.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => DetailAsync(s.H, s.H.World.OrgWide, MessageThreadKey.ForOtaReview(s.Other.Id)));
    }

    // ─── The detail ───

    [Fact]
    public async Task Detail_OfAReviewOfAChannel_ShowsTheStarsTheWordsTheStatesTheBookingAndWhatTheChannelAllows()
    {
        var s = await SeedAsync();
        var key = MessageThreadKey.ForOtaReview(s.Granted.Id);

        var detail = await DetailAsync(s.H, s.H.World.OrgWide, key, includeAmounts: true);

        Assert.Equal(key, detail.Key);
        Assert.Equal(MessageThreadSource.OtaReview, detail.Source);
        Assert.Equal(MessageHubChannel.Airbnb, detail.Channel);
        Assert.Equal(s.H.Granted.PropertyId, detail.PropertyId);
        Assert.Equal(s.H.World.Granted.Name, detail.PropertyName);
        Assert.Equal(s.H.Granted.BookingId, detail.BookingId);
        Assert.Equal("Giulietta Verdi", detail.GuestName);
        Assert.Equal(OtaReviewHarness.Now.UtcDateTime.AddMinutes(-120), detail.PublishedAt);
        Assert.Equal(5m, detail.Rating);
        Assert.Equal(["cleanliness", "communication"], detail.CategoryRatings.Select(r => r.Category));
        Assert.StartsWith("Posto bellissimo", detail.Text, StringComparison.Ordinal);
        Assert.Equal(OtaReviewActionState.None, detail.Reply.State);
        Assert.NotNull(detail.HostReview);
        Assert.Equal(HostReviewWindow.Open, detail.HostReview.Window);
        Assert.Equal(OtaReviewActionState.None, detail.HostReview.State);
        Assert.NotNull(detail.Booking);
        Assert.Equal(s.H.Granted.BookingId, detail.Booking.BookingId);
        Assert.NotNull(detail.Booking.Amounts);
        Assert.NotNull(detail.CustomerGuestId);
        Assert.True(detail.Capabilities.CanRead);
        Assert.True(detail.Capabilities.CanReply);
        Assert.True(detail.Capabilities.CanHostReview);
        Assert.Null(detail.Capabilities.Reason);
        Assert.Equal(OtaReviewHarness.Now.UtcDateTime, detail.LastSyncedAt);
        Assert.Null(detail.LastSyncError);

        // A caller who may not read payments gets the card with no money.
        var withoutMoney = await DetailAsync(s.H, s.H.World.OrgWide, key, includeAmounts: false);
        Assert.NotNull(withoutMoney.Booking);
        Assert.Null(withoutMoney.Booking.Amounts);

        // The connector off, or the listing no longer confirmed: nothing can be sent, and the reason says why.
        s.H.Flags.Set(FeatureFlags.OctorateConnector, false);
        var off = await DetailAsync(s.H, s.H.World.OrgWide, key);
        Assert.False(off.Capabilities.CanReply);
        Assert.False(off.Capabilities.CanHostReview);
        Assert.NotNull(off.Capabilities.Reason);
    }

    [Fact]
    public async Task Detail_OfAReviewOfTheBookingSite_IsAnsweredOnce_AndHasNoReviewOfTheHost()
    {
        var s = await SeedAsync();
        var answeredStay = await s.H.AddStayAsync(s.H.Granted.OrgId, s.H.Granted.PropertyId, "585009", "HMDIRECT02", BookingSource.Direct);
        var answered = await s.H.AddSiteReviewAsync(answeredStay, "Tutto ok.", 5, hostReply: "Grazie a voi!");

        var open = await DetailAsync(s.H, s.H.World.OrgWide, MessageThreadKey.ForReview(s.Site.Id));
        var done = await DetailAsync(s.H, s.H.World.OrgWide, MessageThreadKey.ForReview(answered.Id), includeAmounts: true);

        Assert.Equal(MessageThreadSource.Review, open.Source);
        Assert.Equal(MessageHubChannel.Direct, open.Channel);
        Assert.Equal(4m, open.Rating);
        Assert.Equal("Soggiorno piacevole.", open.Text);
        Assert.Empty(open.CategoryRatings);
        Assert.Null(open.HostReview);
        Assert.Equal(OtaReviewActionState.None, open.Reply.State);
        Assert.True(open.Capabilities.CanReply);
        Assert.False(open.Capabilities.CanHostReview);
        Assert.Null(open.Booking?.Amounts);
        Assert.Null(open.LastSyncedAt);

        Assert.Equal(OtaReviewActionState.Sent, done.Reply.State);
        Assert.Equal("Grazie a voi!", done.Reply.Text);
        Assert.False(done.Capabilities.CanReply);
        Assert.Equal("review_reply_already_sent", done.Capabilities.ReplyReason);
        Assert.NotNull(done.Booking?.Amounts);
    }

    [Fact]
    public async Task Detail_OfAReviewNobodyPublished_OfAnotherOrg_OrWithTheKeyOfAConversation_IsNotFound()
    {
        var s = await SeedAsync();
        var org = s.H.World.OrgWide;

        foreach (var key in new[]
                 {
                     MessageThreadKey.ForOtaReview(s.Unpublished.Id),
                     MessageThreadKey.ForOtaReview(s.Other.Id),
                     MessageThreadKey.ForOtaReview(Guid.NewGuid()),
                     MessageThreadKey.ForReview(Guid.NewGuid()),
                     MessageThreadKey.ForOta(s.Granted.Id),
                     MessageThreadKey.ForDirect(s.H.Granted.BookingId),
                 })
        {
            var error = await Assert.ThrowsAsync<NotFoundException>(() => DetailAsync(s.H, org, key));
            Assert.Equal(MessageHubService.ReviewNotFoundCode, error.Code);
        }
    }

    // ─── A review is not a conversation ───

    [Fact]
    public async Task TheKeyOfAReview_IsA404ForTheEndpointsOfAConversation_AndNothingIsWrittenToIt()
    {
        var s = await SeedAsync();
        var org = s.H.World.OrgWide;
        var keys = new[] { MessageThreadKey.ForOtaReview(s.Granted.Id), MessageThreadKey.ForReview(s.Site.Id) };

        foreach (var key in keys)
        {
            await using var db = s.H.NewDb();
            var hub = Hub(s.H, db);

            var read = await Assert.ThrowsAsync<NotFoundException>(() => hub.GetThreadAsync(org, key, includeAmounts: false));
            var marked = await Assert.ThrowsAsync<NotFoundException>(() => hub.MarkReadAsync(org, key, User));
            var lookup = await Assert.ThrowsAsync<NotFoundException>(() => MessageConversationLookup.FindAsync(db, org, key, CancellationToken.None));

            Assert.Equal(MessageHubService.ThreadNotFoundCode, read.Code);
            Assert.Equal(MessageHubService.ThreadNotFoundCode, marked.Code);
            Assert.Equal(MessageHubService.ThreadNotFoundCode, lookup.Code);
        }

        // The reply of a conversation neither: it would have written a message where a review is.
        var conversations = HostScopeScenario.NewInMemoryDb();
        var world = await MessageHubTestKit.SeedAsync(conversations);
        var rig = new MessageReplyRig(conversations);
        foreach (var key in keys)
        {
            await Assert.ThrowsAsync<NotFoundException>(
                () => rig.Service.ReplyAsync(world.Scope.OrgWide, key, "Ciao", "reply-key-0001", User, MessageOrigin.Person, null));
        }

        rig.NothingWasSent();
        s.H.Client.Verify(
            c => c.SendHostReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
