using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Reviews;

/// <summary>
/// The reviews of the channels (E05/F06/US01/T02, ADR-032, NF-69, OCT-12, OCT-13): the read of one booking, what the host sees of it, and the two sends (the reply and the host's own
/// review). What is proven here is what the service does with the answers of the channel manager: a review that is not published has no text; a channel with no integration is never
/// read and never gets a row; a reply goes to the channel manager once for a key, and an outcome nobody knows is never sent again by the call; a review of another org or out of the
/// reach of the caller does not exist; the words of a guest or of a host are in no log.
/// </summary>
public class OtaReviewServiceTests
{
    private const string User = "auth0|titolare";
    private const string KeyOne = "reply-key-0001";
    private const string KeyTwo = "reply-key-0002";
    private const string ReplyText = "Grazie di cuore, speriamo di rivedervi presto!";

    private static async Task<OtaReview> PublishAsync(OtaReviewHarness h, ReviewStay stay, string text = "Posto bellissimo, host gentilissimo.")
    {
        h.ChannelHasReview(stay, text);
        return await h.SyncAsync(stay);
    }

    private static async Task<OtaReviewActionResult> ReplyAsync(OtaReviewHarness h, ReviewStay stay, Guid reviewId, string? text, string? key)
    {
        await using var db = h.NewDb();
        using (h.Scope.Use(stay.OrgId))
            return await h.Service(db).ReplyAsync(reviewId, text, key, User);
    }

    private static async Task<OtaReviewActionResult> HostReviewAsync(OtaReviewHarness h, ReviewStay stay, Guid reviewId, OtaHostReviewCommand? command, string? key)
    {
        await using var db = h.NewDb();
        using (h.Scope.Use(stay.OrgId))
            return await h.Service(db).SendHostReviewAsync(reviewId, command, key, User);
    }

    private static OtaHostReviewCommand GoodReview() => new(
        [new ReviewCategoryRating("cleanliness", 5m), new ReviewCategoryRating("communication", 4m)],
        "Ospiti ordinati e puntuali.",
        "Grazie per aver lasciato la casa in ordine.");

    private static void NothingWasSent(OtaReviewHarness h)
    {
        h.Client.Verify(
            c => c.SendHostReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        h.Client.Verify(
            c => c.SendHostReviewAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<OctorateHostReviewCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static void TheReplyIsSent(OtaReviewHarness h, Times times) =>
        h.Client.Verify(
            c => c.SendHostReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            times);

    // ─── The read ───

    [Fact]
    public async Task SyncBooking_APublishedReview_IsStoredOnceWithOneEventOfIdsOnly_AndNoWordOfItInTheLog()
    {
        var h = await OtaReviewHarness.CreateAsync();
        h.ChannelHasReview(h.Granted, "Posto bellissimo, host gentilissimo.", 4.5m);

        OtaReviewSyncResult first;
        OtaReviewSyncResult second;
        await using (var db = h.NewDb())
        using (h.Scope.Use(h.Granted.OrgId))
        {
            // One service for both reads, as a run of the job holds it: the row of the first read is tracked when the second comes.
            var service = h.Service(db);
            first = await service.SyncBookingAsync(h.Granted.BookingId);
            second = await service.SyncBookingAsync(h.Granted.BookingId);
        }

        Assert.Equal(OtaReviewSyncOutcome.Synced, first.Outcome);
        Assert.True(first.NewlyPublished);
        Assert.Equal(OtaReviewSyncOutcome.Synced, second.Outcome);
        Assert.False(second.NewlyPublished);

        var row = await h.ReviewOfAsync(h.Granted);
        Assert.True(row.GuestReviewPublished);
        Assert.Equal(OtaChannel.Airbnb, row.Channel);
        Assert.Equal(OtaReviewHarness.GrantedRefer, row.OctorateRefer);
        Assert.Equal(4.5m, row.GuestRating);
        Assert.Equal("Posto bellissimo, host gentilissimo.", row.GuestReviewText);
        Assert.Equal(2, row.PollCount);
        Assert.Equal(HostReviewWindow.Open, row.HostReviewWindow);
        Assert.Equal(OtaReviewHarness.Now.UtcDateTime.AddHours(6), row.NextPollAt);

        // One event for the review, the first time it is seen, with ids only: the routines of the host that wait for a review start from it.
        var events = await h.ReadAsync(db => db.OutboxEvents.Where(e => e.EventType == DomainEventType.ReviewReceived).ToListAsync());
        var published = Assert.Single(events);
        Assert.Equal(row.Id, published.EntityId);
        Assert.Equal(h.Granted.OrgId, published.OrgId);
        Assert.Equal(ReviewReceivedPayload.OtaDedupKeyOf(row.Id), published.DedupKey);
        Assert.DoesNotContain("bellissimo", published.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(h.Granted.BookingId.ToString(), published.PayloadJson, StringComparison.OrdinalIgnoreCase);

        // The log keeps ids and states: never the words of the guest or the reference of the booking on the channel manager.
        Assert.DoesNotContain("bellissimo", h.Log.AllOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(OtaReviewHarness.GrantedRefer, h.Log.AllOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Giulietta", h.Log.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncBooking_NoReviewYet_MakesTheRowThatWaits_WithNoEventAndNoText()
    {
        var h = await OtaReviewHarness.CreateAsync();
        h.ChannelHasNothing(h.Granted);

        var row = await h.SyncAsync(h.Granted);

        Assert.False(row.GuestReviewPublished);
        Assert.Null(row.GuestReviewText);
        Assert.Null(row.GuestRating);
        Assert.Equal(OtaReviewHarness.Now.UtcDateTime.AddHours(6), row.NextPollAt);
        Assert.Empty(await h.ReadAsync(db => db.OutboxEvents.Where(e => e.EventType == DomainEventType.ReviewReceived).ToListAsync()));
    }

    [Fact]
    public async Task SyncBooking_AReviewTheChannelHasNotPublished_HasNoTextNoRatingAndNoEvent()
    {
        var h = await OtaReviewHarness.CreateAsync();
        h.Client
            .Setup(c => c.GetReviewAsync(h.Granted.Accommodation, h.Granted.Refer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateReview(new OctorateGuestReview { Published = false, Text = "Segreto", Rating = 5m }, null));

        var row = await h.SyncAsync(h.Granted);

        Assert.False(row.GuestReviewPublished);
        Assert.Null(row.GuestReviewText);
        Assert.Null(row.GuestRating);
        Assert.Empty(await h.ReadAsync(db => db.OutboxEvents.Where(e => e.EventType == DomainEventType.ReviewReceived).ToListAsync()));
    }

    [Fact]
    public async Task SyncBooking_AChannelTheChannelManagerHasNoReviewsFor_IsNeverReadAndGetsNoRow()
    {
        var h = await OtaReviewHarness.CreateAsync();
        var booking = await h.AddStayAsync(h.Granted.OrgId, h.Granted.PropertyId, "585009", "HMBOOKING1", BookingSource.BookingCom);
        var direct = await h.AddStayAsync(h.Granted.OrgId, h.Granted.PropertyId, "585009", "HMDIRECT01", BookingSource.Direct);

        foreach (var stay in new[] { booking, direct })
        {
            await using var db = h.NewDb();
            OtaReviewSyncResult result;
            using (h.Scope.Use(stay.OrgId))
                result = await h.Service(db).SyncBookingAsync(stay.BookingId);

            Assert.Equal(OtaReviewSyncOutcome.ChannelNotIntegrated, result.Outcome);
        }

        h.Client.Verify(c => c.GetReviewAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(await h.ReadAsync(db => db.OtaReviews.ToListAsync()));
    }

    [Fact]
    public async Task SyncBooking_AStayThatDoesNotHappenOrHasNoListingOrNoReservation_IsNotReadable()
    {
        var h = await OtaReviewHarness.CreateAsync();
        var orgId = h.Granted.OrgId;
        var propertyId = h.Granted.PropertyId;
        var cancelled = await h.AddStayAsync(orgId, propertyId, OtaReviewHarness.GrantedAccommodation, "HMCANCELED", status: BookingStatus.Cancelled);
        var pending = await h.AddStayAsync(orgId, propertyId, OtaReviewHarness.GrantedAccommodation, "HMPENDING1", status: BookingStatus.Pending);
        var noReservation = await h.AddStayAsync(orgId, propertyId, OtaReviewHarness.GrantedAccommodation, string.Empty);
        var noShow = await h.AddStayAsync(orgId, propertyId, OtaReviewHarness.GrantedAccommodation, "HMNOSHOW01");
        await using (var db = h.NewDb())
        {
            (await db.Bookings.SingleAsync(b => b.Id == noShow.BookingId)).NoShowAt = OtaReviewHarness.Now.UtcDateTime.AddDays(-2);
            await db.SaveChangesAsync();
        }

        h.WithdrawListing(h.Hidden.OrgId, OtaReviewHarness.HiddenAccommodation);

        foreach (var stay in new[] { cancelled, pending, noReservation, noShow, h.Hidden })
        {
            await using var db = h.NewDb();
            OtaReviewSyncResult result;
            using (h.Scope.Use(stay.OrgId))
                result = await h.Service(db).SyncBookingAsync(stay.BookingId);

            Assert.Equal(OtaReviewSyncOutcome.NotReadable, result.Outcome);
        }

        h.Client.Verify(c => c.GetReviewAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(await h.ReadAsync(db => db.OtaReviews.ToListAsync()));
    }

    [Fact]
    public async Task SyncBooking_WithTheConnectorOff_ReadsNothingAndWritesNothing()
    {
        var h = await OtaReviewHarness.CreateAsync();
        h.Flags.Set(FeatureFlags.OctorateConnector, false);

        await using var db = h.NewDb();
        OtaReviewSyncResult result;
        using (h.Scope.Use(h.Granted.OrgId))
            result = await h.Service(db).SyncBookingAsync(h.Granted.BookingId);

        Assert.Equal(OtaReviewSyncOutcome.ConnectorOff, result.Outcome);
        h.Client.Verify(c => c.GetReviewAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(await db.OtaReviews.ToListAsync());
    }

    [Fact]
    public async Task SyncBooking_AReadTheChannelManagerRefuses_StoresOnlyTheCodeAndBacksOff()
    {
        var h = await OtaReviewHarness.CreateAsync();
        h.Client
            .Setup(c => c.GetReviewAsync(h.Granted.Accommodation, h.Granted.Refer, It.IsAny<CancellationToken>()))
            .ThrowsAsync(OctorateAuthException.ReviewNotAvailable(403));

        OtaReviewSyncResult result;
        await using (var db = h.NewDb())
        using (h.Scope.Use(h.Granted.OrgId))
            result = await h.Service(db).SyncBookingAsync(h.Granted.BookingId);

        Assert.Equal(OtaReviewSyncOutcome.Failed, result.Outcome);
        Assert.Equal(OtaReviewErrorCodes.ReviewNotAvailable, result.ErrorCode);
        var row = await h.ReviewOfAsync(h.Granted);
        Assert.Equal(OtaReviewErrorCodes.ReviewNotAvailable, row.LastSyncError);
        Assert.False(row.GuestReviewPublished);
        Assert.Null(row.GuestReviewText);
        Assert.Equal(OtaReviewHarness.Now.UtcDateTime.AddHours(24), row.NextPollAt);
        Assert.DoesNotContain(OtaReviewHarness.GrantedRefer, h.Log.AllOutput, StringComparison.Ordinal);

        // A failure that may pass is read again at the next run.
        h.Client
            .Setup(c => c.GetReviewAsync(h.Granted.Accommodation, h.Granted.Refer, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(503));
        var again = await h.SyncAsync(h.Granted);
        Assert.Equal("octorate_unavailable", again.LastSyncError);
        Assert.Equal(OtaReviewHarness.Now.UtcDateTime.AddHours(6), again.NextPollAt);
    }

    [Fact]
    public async Task SyncBooking_WhenTheCallsOfTheOrgAreUsedUp_StoresNothingAndTheLimitReachesTheCaller()
    {
        var h = await OtaReviewHarness.CreateAsync();
        h.Client
            .Setup(c => c.GetReviewAsync(h.Granted.Accommodation, h.Granted.Refer, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateRateLimitException(429));

        await using var db = h.NewDb();
        using (h.Scope.Use(h.Granted.OrgId))
            await Assert.ThrowsAsync<OctorateRateLimitException>(() => h.Service(db).SyncBookingAsync(h.Granted.BookingId));

        Assert.Empty(await db.OtaReviews.ToListAsync());
    }

    [Fact]
    public async Task SyncBooking_ANewReview_TellsTheHostsOnce_WithNoWordOfTheReviewOrNameOfTheGuest()
    {
        var h = await OtaReviewHarness.CreateAsync();
        var told = new List<(string Key, PushNotificationPayload Payload)>();
        var push = new Mock<IPushNotificationService>();
        push.Setup(p => p.Enqueue(It.IsAny<string>(), It.IsAny<PushAudience>(), It.IsAny<PushNotificationPayload>()))
            .Callback<string, PushAudience, PushNotificationPayload>((key, _, payload) => told.Add((key, payload)))
            .Returns(true);
        h.ChannelHasReview(h.Granted, "Il frigorifero era rotto e il proprietario scortese.", 2m);

        await using (var db = h.NewDb())
        using (h.Scope.Use(h.Granted.OrgId))
        {
            var service = h.Service(db, push.Object);
            await service.SyncBookingAsync(h.Granted.BookingId);
            await service.SyncBookingAsync(h.Granted.BookingId);
        }

        var row = await h.ReviewOfAsync(h.Granted);
        var one = Assert.Single(told);
        Assert.Equal(PushDeliveryKeys.OtaReviewReceived(row.Id), one.Key);
        Assert.Equal(PushTypes.OtaReviewReceived, one.Payload.Type);
        Assert.Equal(h.Granted.BookingId, one.Payload.BookingId);
        var shown = $"{one.Payload.Title} {one.Payload.Body}";
        Assert.Contains(h.World.Granted.Name, shown, StringComparison.Ordinal);
        Assert.DoesNotContain("frigorifero", shown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Giulietta", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncBooking_AReplyThatNeverReachedTheChannel_TellsTheHostsOnceItIsAnError()
    {
        var h = await OtaReviewHarness.CreateAsync();
        var review = await PublishAsync(h, h.Granted);
        h.Client
            .Setup(c => c.SendHostReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(503));
        await ReplyAsync(h, h.Granted, review.Id, ReplyText, KeyOne);
        var told = new List<(string Key, PushNotificationPayload Payload)>();
        var push = new Mock<IPushNotificationService>();
        push.Setup(p => p.Enqueue(It.IsAny<string>(), It.IsAny<PushAudience>(), It.IsAny<PushNotificationPayload>()))
            .Callback<string, PushAudience, PushNotificationPayload>((key, _, payload) => told.Add((key, payload)))
            .Returns(true);

        h.Clock.Advance(TimeSpan.FromMinutes(61));
        for (var run = 0; run < 2; run++)
        {
            await using var db = h.NewDb();
            using (h.Scope.Use(h.Granted.OrgId))
                await h.Service(db, push.Object).SyncBookingAsync(h.Granted.BookingId);
        }

        var failed = Assert.Single(told);
        Assert.Equal(PushTypes.OtaReviewActionFailed, failed.Payload.Type);
        Assert.StartsWith($"ota-review:{review.Id:N}:reply-failed:", failed.Key, StringComparison.Ordinal);
        Assert.DoesNotContain("rivedervi", $"{failed.Payload.Title} {failed.Payload.Body}", StringComparison.OrdinalIgnoreCase);
    }

    // ─── The reply ───

    /// <summary>
    /// PROOF TEST: data rule (OCT-13, a reply is public and goes out once). The same key asked twice, even with another text, sends once and answers what was stored; a second reply
    /// of the same review with a key of its own is refused (the channel holds one); the reply is in no log.
    /// </summary>
    [Fact]
    public async Task OtaReview_ReplyTwice_SendsOnce_Test()
    {
        var h = await OtaReviewHarness.CreateAsync();
        var review = await PublishAsync(h, h.Granted);

        var first = await ReplyAsync(h, h.Granted, review.Id, ReplyText, KeyOne);
        var again = await ReplyAsync(h, h.Granted, review.Id, ReplyText, KeyOne);
        var otherText = await ReplyAsync(h, h.Granted, review.Id, "Un testo diverso, la stessa chiave", KeyOne);

        Assert.Equal(OtaReviewActionState.Sent, first.State);
        Assert.False(first.Replayed);
        Assert.True(again.Replayed);
        Assert.True(otherText.Replayed);
        Assert.Equal(OtaReviewActionState.Sent, otherText.State);
        Assert.Equal(ReplyText, otherText.Review.Reply.Text);

        var second = await Assert.ThrowsAsync<DomainConflictException>(() => ReplyAsync(h, h.Granted, review.Id, "Un secondo tentativo", KeyTwo));
        Assert.Equal(OtaReviewErrorCodes.ReplyAlreadySent, second.Code);

        h.Client.Verify(
            c => c.SendHostReplyAsync(OtaReviewHarness.GrantedAccommodation, OtaReviewHarness.GrantedRefer, ReplyText, It.IsAny<CancellationToken>()),
            Times.Once);
        TheReplyIsSent(h, Times.Once());

        var row = await h.ReviewOfAsync(h.Granted);
        Assert.Equal(OtaReviewActionState.Sent, row.HostReplyState);
        Assert.Equal(KeyOne, row.HostReplyIdempotencyKey);
        Assert.Equal(ReplyText, row.HostReplyText);
        Assert.NotNull(row.HostReplySentAt);
        Assert.Null(row.HostReplyError);

        Assert.DoesNotContain("speriamo", h.Log.AllOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(OtaReviewHarness.GrantedRefer, h.Log.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reply_ACallWithNoAnswer_StaysToBeVerified_IsNeverSentAgain_AndIsCompletedWhenTheChannelShowsIt()
    {
        var h = await OtaReviewHarness.CreateAsync();
        var review = await PublishAsync(h, h.Granted);
        h.Client
            .Setup(c => c.SendHostReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(503));

        var unknown = await ReplyAsync(h, h.Granted, review.Id, ReplyText, KeyOne);

        // Saved before it left, and not known to have arrived: waiting for a check, never an error and never a success.
        Assert.Equal(OtaReviewActionState.ManualVerify, unknown.State);
        var waiting = await h.ReviewOfAsync(h.Granted);
        Assert.Equal(ReplyText, waiting.HostReplyText);
        Assert.Equal(KeyOne, waiting.HostReplyIdempotencyKey);
        Assert.NotNull(waiting.HostReplyRequestedAt);
        Assert.Null(waiting.HostReplySentAt);

        // Neither the same key nor another one sends it again while nobody knows (a reply is public).
        var replay = await ReplyAsync(h, h.Granted, review.Id, ReplyText, KeyOne);
        Assert.True(replay.Replayed);
        Assert.Equal(OtaReviewActionState.ManualVerify, replay.State);
        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => ReplyAsync(h, h.Granted, review.Id, ReplyText, KeyTwo));
        Assert.Equal(OtaReviewErrorCodes.ActionUnconfirmed, refused.Code);
        TheReplyIsSent(h, Times.Once());

        // The next read finds the reply on the channel and completes the action: still one send.
        h.ChannelHasReview(h.Granted, hostReply: ReplyText);
        var completed = await h.SyncAsync(h.Granted);
        Assert.Equal(OtaReviewActionState.Sent, completed.HostReplyState);
        Assert.NotNull(completed.HostReplySentAt);
        TheReplyIsSent(h, Times.Once());
    }

    [Fact]
    public async Task Reply_NotConfirmedWithinTheHour_BecomesAnErrorTheHostCanActOn_AndANewKeySendsAgain()
    {
        var h = await OtaReviewHarness.CreateAsync();
        var review = await PublishAsync(h, h.Granted);
        h.Client
            .Setup(c => c.SendHostReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(503));
        await ReplyAsync(h, h.Granted, review.Id, ReplyText, KeyOne);

        h.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(OtaReviewActionState.ManualVerify, (await h.SyncAsync(h.Granted)).HostReplyState);

        h.Clock.Advance(TimeSpan.FromMinutes(31));
        var failed = await h.SyncAsync(h.Granted);
        Assert.Equal(OtaReviewActionState.Error, failed.HostReplyState);
        Assert.Equal(OtaReviewErrorCodes.ReplyNotDelivered, failed.HostReplyError);

        // The host may try again, with a key of its own, when the channel manager answers.
        h.Client
            .Setup(c => c.SendHostReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var retried = await ReplyAsync(h, h.Granted, review.Id, "Grazie ancora per il soggiorno!", KeyTwo);

        Assert.Equal(OtaReviewActionState.Sent, retried.State);
        Assert.Null(retried.ErrorCode);
        TheReplyIsSent(h, Times.Exactly(2));
        var row = await h.ReviewOfAsync(h.Granted);
        Assert.Equal(KeyTwo, row.HostReplyIdempotencyKey);
        Assert.Equal("Grazie ancora per il soggiorno!", row.HostReplyText);
    }

    [Fact]
    public async Task Reply_WhenTheLimitIsReached_TakesTheAttemptBack_SoTheSameKeyCanBeSentWhenItIsOver()
    {
        var h = await OtaReviewHarness.CreateAsync();
        var review = await PublishAsync(h, h.Granted);
        h.Client
            .SetupSequence(c => c.SendHostReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateRateLimitException(429))
            .Returns(Task.CompletedTask);

        await Assert.ThrowsAsync<OctorateRateLimitException>(() => ReplyAsync(h, h.Granted, review.Id, ReplyText, KeyOne));

        // Nothing left CasaZen: no text, no key, no state, as if nothing was asked.
        var untouched = await h.ReviewOfAsync(h.Granted);
        Assert.Equal(OtaReviewActionState.None, untouched.HostReplyState);
        Assert.Null(untouched.HostReplyText);
        Assert.Null(untouched.HostReplyIdempotencyKey);

        var sent = await ReplyAsync(h, h.Granted, review.Id, ReplyText, KeyOne);

        Assert.Equal(OtaReviewActionState.Sent, sent.State);
        Assert.False(sent.Replayed);
        TheReplyIsSent(h, Times.Exactly(2));
    }

    [Fact]
    public async Task Reply_TheChannelManagerSaysItIsNotAllowedOrDoneAlready_IsA409AndAnErrorTheNextReadExplains()
    {
        var h = await OtaReviewHarness.CreateAsync();
        var review = await PublishAsync(h, h.Granted);
        h.Client
            .Setup(c => c.SendHostReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateReviewActionNotAllowedException(409));

        var error = await Assert.ThrowsAsync<DomainConflictException>(() => ReplyAsync(h, h.Granted, review.Id, ReplyText, KeyOne));

        Assert.Equal(OtaReviewErrorCodes.ActionNotAllowed, error.Code);
        var row = await h.ReviewOfAsync(h.Granted);
        Assert.Equal(OtaReviewActionState.Error, row.HostReplyState);
        Assert.Equal(OctorateReviewActionNotAllowedException.CodeValue, row.HostReplyError);
        Assert.Equal(OtaReviewHarness.Now.UtcDateTime, row.NextPollAt);
    }

    [Fact]
    public async Task Reply_ATypedRefusal_IsTheStateWithItsCode_NotAnException()
    {
        var h = await OtaReviewHarness.CreateAsync();
        var review = await PublishAsync(h, h.Granted);
        h.Client
            .Setup(c => c.SendHostReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(OctorateAuthException.ReviewNotAvailable(403));

        var result = await ReplyAsync(h, h.Granted, review.Id, ReplyText, KeyOne);

        Assert.Equal(OtaReviewActionState.Error, result.State);
        Assert.Equal(OtaReviewErrorCodes.ReviewNotAvailable, result.ErrorCode);
        Assert.Equal(OtaReviewErrorCodes.ReviewNotAvailable, result.Review.Reply.ErrorCode);
    }

    [Fact]
    public async Task Reply_BeforeTheGuestsReviewIsPublished_Is422_AndNothingIsSent()
    {
        var h = await OtaReviewHarness.CreateAsync();
        h.ChannelHasNothing(h.Granted);
        var row = await h.SyncAsync(h.Granted);

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => ReplyAsync(h, h.Granted, row.Id, ReplyText, KeyOne));

        Assert.Equal(OtaReviewErrorCodes.NotPublished, error.Code);
        NothingWasSent(h);
    }

    [Fact]
    public async Task Reply_WithATextOrAKeyThatIsNotUsable_Is422_AndNothingIsSent()
    {
        var h = await OtaReviewHarness.CreateAsync();
        var review = await PublishAsync(h, h.Granted);

        foreach (var text in new string?[] { null, string.Empty, "   \n  ", new string('a', ReviewRules.ReplyMaxLength + 1) })
        {
            var error = await Assert.ThrowsAsync<DomainRuleException>(() => ReplyAsync(h, h.Granted, review.Id, text, KeyOne));
            Assert.Equal(OtaReviewErrorCodes.ReplyInvalid, error.Code);
        }

        foreach (var key in new string?[] { null, string.Empty, "short", "has spaces in it", new string('k', 65) })
        {
            var error = await Assert.ThrowsAsync<DomainRuleException>(() => ReplyAsync(h, h.Granted, review.Id, ReplyText, key));
            Assert.Equal(OtaReviewErrorCodes.IdempotencyKeyInvalid, error.Code);
        }

        NothingWasSent(h);
        Assert.Equal(OtaReviewActionState.None, (await h.ReviewOfAsync(h.Granted)).HostReplyState);
    }

    [Fact]
    public async Task Reply_ForAListingNobodyConfirmed_Is409_AndForAConnectorThatIsOff_NothingLeaves()
    {
        var h = await OtaReviewHarness.CreateAsync();
        var review = await PublishAsync(h, h.Granted);

        h.WithdrawListing(h.Granted.OrgId, OtaReviewHarness.GrantedAccommodation);
        var unconfirmed = await Assert.ThrowsAsync<DomainConflictException>(() => ReplyAsync(h, h.Granted, review.Id, ReplyText, KeyOne));
        Assert.Equal(OtaReviewErrorCodes.MappingNotConfirmed, unconfirmed.Code);

        await h.AddStayAsync(h.Granted.OrgId, h.Granted.PropertyId, OtaReviewHarness.GrantedAccommodation, "HMANOTHER01", listing: true);
        h.Flags.Set(FeatureFlags.OctorateConnector, false);
        var off = await Assert.ThrowsAsync<OctorateAuthException>(() => ReplyAsync(h, h.Granted, review.Id, ReplyText, KeyOne));
        Assert.True(off.IsNotConnected);

        NothingWasSent(h);
        Assert.Equal(OtaReviewActionState.None, (await h.ReviewOfAsync(h.Granted)).HostReplyState);
    }

    [Fact]
    public async Task Reply_ToAReviewOfAChannelThatCannotBeAnswered_Is422_AndNothingIsSent()
    {
        var h = await OtaReviewHarness.CreateAsync();
        var booking = await h.AddStayAsync(h.Granted.OrgId, h.Granted.PropertyId, "585009", "HMBOOKING1", BookingSource.BookingCom);
        Guid reviewId;
        await using (var db = h.NewDb())
        {
            var row = new OtaReview
            {
                OrgId = booking.OrgId,
                PropertyId = booking.PropertyId,
                BookingId = booking.BookingId,
                Channel = OtaChannel.Booking,
                OctorateRefer = booking.Refer,
                GuestReviewPublished = true,
                GuestReviewText = "Un testo che non doveva esserci",
            };
            db.OtaReviews.Add(row);
            await db.SaveChangesAsync();
            reviewId = row.Id;
        }

        var reply = await Assert.ThrowsAsync<DomainRuleException>(() => ReplyAsync(h, booking, reviewId, ReplyText, KeyOne));
        var review = await Assert.ThrowsAsync<DomainRuleException>(() => HostReviewAsync(h, booking, reviewId, GoodReview(), KeyOne));

        Assert.Equal(OtaReviewErrorCodes.ChannelNotSupported, reply.Code);
        Assert.Equal(OtaReviewErrorCodes.ChannelNotSupported, review.Code);
        NothingWasSent(h);
    }

    // ─── The host's own review ───

    [Fact]
    public async Task HostReview_WhileTheWindowIsOpen_IsSentOnceWithItsRatings_AndTheReplyIsAnotherAction()
    {
        var h = await OtaReviewHarness.CreateAsync();
        var review = await PublishAsync(h, h.Granted);

        // The reply and the host's review are two actions: the same key may open both.
        var reply = await ReplyAsync(h, h.Granted, review.Id, ReplyText, KeyOne);
        var sent = await HostReviewAsync(h, h.Granted, review.Id, GoodReview(), KeyOne);
        var again = await HostReviewAsync(h, h.Granted, review.Id, GoodReview(), KeyOne);

        Assert.Equal(OtaReviewActionState.Sent, reply.State);
        Assert.Equal(OtaReviewActionState.Sent, sent.State);
        Assert.False(sent.Replayed);
        Assert.True(again.Replayed);
        Assert.Equal(HostReviewWindow.Sent, sent.Review.HostReview.Window);
        Assert.Equal(["cleanliness", "communication"], sent.Review.HostReview.Ratings.Select(r => r.Category));
        Assert.Equal("Ospiti ordinati e puntuali.", sent.Review.HostReview.Text);
        h.Client.Verify(
            c => c.SendHostReviewAsync(
                OtaReviewHarness.GrantedAccommodation,
                OtaReviewHarness.GrantedRefer,
                It.Is<OctorateHostReviewCommand>(r => r.Ratings.Count == 2 && r.PublicText == "Ospiti ordinati e puntuali." && r.PrivateText != null),
                It.IsAny<CancellationToken>()),
            Times.Once);
        TheReplyIsSent(h, Times.Once());

        // Once it is on the channel the window is over: another host's review of the same stay is refused as already sent.
        var second = await Assert.ThrowsAsync<DomainConflictException>(() => HostReviewAsync(h, h.Granted, review.Id, GoodReview(), KeyTwo));
        Assert.Equal(OtaReviewErrorCodes.HostReviewAlreadySent, second.Code);

        var row = await h.ReviewOfAsync(h.Granted);
        Assert.Equal(OtaReviewActionState.Sent, row.HostReviewState);
        Assert.Equal(HostReviewWindow.Sent, row.HostReviewWindow);
        Assert.DoesNotContain("puntuali", h.Log.AllOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HostReview_AWindowThatIsNotOpen_Is409_AndAnInvalidReviewIs422_NothingIsSent()
    {
        var h = await OtaReviewHarness.CreateAsync();
        h.ChannelHasReview(h.Granted, window: HostReviewWindow.Closed);
        var closed = await h.SyncAsync(h.Granted);

        var window = await Assert.ThrowsAsync<DomainConflictException>(() => HostReviewAsync(h, h.Granted, closed.Id, GoodReview(), KeyOne));
        Assert.Equal(OtaReviewErrorCodes.WindowClosed, window.Code);

        var open = await PublishAsync(h, h.Hidden);
        var invalid = await Assert.ThrowsAsync<DomainRuleException>(
            () => HostReviewAsync(h, h.Hidden, open.Id, new OtaHostReviewCommand([], "Testo", null), KeyOne));
        Assert.Equal(OtaReviewErrorCodes.HostReviewInvalid, invalid.Code);

        NothingWasSent(h);
    }

    // ─── What the host reads ───

    [Fact]
    public async Task ListForBooking_ShowsWhatTheChannelAllowsEvenBeforeAnyReview_AndThenTheReviewWithItsActions()
    {
        var h = await OtaReviewHarness.CreateAsync();
        var scope = h.World.OrgWide;
        var booking = await h.AddStayAsync(h.Granted.OrgId, h.Granted.PropertyId, "585009", "HMBOOKING1", BookingSource.BookingCom);

        await using (var rig = h.AsRequestOf(h.Granted.OrgId))
        {
            var before = await rig.Service.ListForBookingAsync(scope, h.Granted.BookingId);
            Assert.Empty(before.Items);
            Assert.True(before.ChannelCapabilities.CanRead);
            Assert.True(before.ChannelCapabilities.CanReply);
            Assert.True(before.ChannelCapabilities.CanHostReview);
            Assert.Null(before.LastSyncedAt);
            Assert.Null(before.CanRefreshAt);

            // A channel the channel manager has no reviews for says so, with its reason, and offers nothing.
            var noIntegration = await rig.Service.ListForBookingAsync(scope, booking.BookingId);
            Assert.Empty(noIntegration.Items);
            Assert.False(noIntegration.ChannelCapabilities.CanRead);
            Assert.False(noIntegration.ChannelCapabilities.CanReply);
            Assert.Equal(OtaReviewCapabilities.ChannelNotIntegrated, noIntegration.ChannelCapabilities.Reason);
        }

        var review = await PublishAsync(h, h.Granted, new string('x', ReviewRules.PreviewLength + 30));
        await using (var rig = h.AsRequestOf(h.Granted.OrgId))
        {
            var list = await rig.Service.ListForBookingAsync(scope, h.Granted.BookingId);

            var item = Assert.Single(list.Items);
            Assert.Equal(MessageThreadKey.ForOtaReview(review.Id), item.Key);
            Assert.Equal(MessageThreadSource.OtaReview, item.Source);
            Assert.Equal(OtaReviewActionState.None, item.ReplyState);
            Assert.True(item.Capabilities.CanReply);
            Assert.Equal(ReviewRules.PreviewLength + 1, item.Preview?.Length);
            Assert.Equal(OtaReviewHarness.Now.UtcDateTime, list.LastSyncedAt);
            Assert.Equal(OtaReviewHarness.Now.UtcDateTime.Add(OtaReviewPollSchedule.RefreshInterval), list.CanRefreshAt);
        }
    }

    [Fact]
    public async Task Refresh_ReadsTheBookingNow_AtMostOnceEveryTenMinutes()
    {
        var h = await OtaReviewHarness.CreateAsync();
        await PublishAsync(h, h.Granted);

        await using var rig = h.AsRequestOf(h.Granted.OrgId);
        var tooSoon = await Assert.ThrowsAsync<DomainTooManyRequestsException>(() => rig.Service.RefreshAsync(h.World.OrgWide, h.Granted.BookingId));
        Assert.Equal(OtaReviewErrorCodes.RefreshTooSoon, tooSoon.Code);
        Assert.Equal(600, tooSoon.Extensions["retryAfterSeconds"]);

        h.Clock.Advance(TimeSpan.FromMinutes(11));
        var list = await rig.Service.RefreshAsync(h.World.OrgWide, h.Granted.BookingId);

        Assert.Single(list.Items);
        h.Client.Verify(c => c.GetReviewAsync(h.Granted.Accommodation, h.Granted.Refer, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Refresh_ForAChannelWithNoIntegration_Is422_AndWithTheConnectorOffIsNotConnected()
    {
        var h = await OtaReviewHarness.CreateAsync();
        var booking = await h.AddStayAsync(h.Granted.OrgId, h.Granted.PropertyId, "585009", "HMBOOKING1", BookingSource.BookingCom);
        await using var rig = h.AsRequestOf(h.Granted.OrgId);

        var noIntegration = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Service.RefreshAsync(h.World.OrgWide, booking.BookingId));
        Assert.Equal(OtaReviewErrorCodes.ChannelNotIntegrated, noIntegration.Code);

        h.Flags.Set(FeatureFlags.OctorateConnector, false);
        var off = await Assert.ThrowsAsync<OctorateAuthException>(() => rig.Service.RefreshAsync(h.World.OrgWide, h.Granted.BookingId));
        Assert.True(off.IsNotConnected);
        h.Client.Verify(c => c.GetReviewAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Tenant and reach ───

    /// <summary>
    /// PROOF TEST: tenant rule (AC-NF69-2). A review, a booking and a reply of org A do not exist for org B: not listed, not read, not refreshed, not answered, not found for the check
    /// of its property, and nothing of it ever reaches the channel manager. The same holds with the scope of org A in the hands of a request of org B (the tenant filter is the second wall).
    /// </summary>
    [Fact]
    public async Task OtaReview_OtherOrg_NotVisible_Test()
    {
        var h = await OtaReviewHarness.CreateAsync();
        var review = await PublishAsync(h, h.Granted);
        h.Client.Invocations.Clear();
        var orgB = h.Other.OrgId;

        await using var rig = h.AsRequestOf(orgB);
        var scopeB = new HostScope(orgB);
        var service = rig.Service;

        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(scopeB, review.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => service.ListForBookingAsync(scopeB, h.Granted.BookingId));
        await Assert.ThrowsAsync<NotFoundException>(() => service.RefreshAsync(scopeB, h.Granted.BookingId));
        await Assert.ThrowsAsync<NotFoundException>(() => service.ReplyAsync(review.Id, ReplyText, KeyOne, User));
        await Assert.ThrowsAsync<NotFoundException>(() => service.SendHostReviewAsync(review.Id, GoodReview(), KeyOne, User));
        Assert.Null(await service.FindPropertyIdAsync(review.Id));
        Assert.Equal(OtaReviewSyncOutcome.BookingNotFound, (await service.SyncBookingAsync(h.Granted.BookingId)).Outcome);

        // The scope of org A in a request of org B: the tenant filter of the context hides the rows anyway.
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(h.World.OrgWide, review.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => service.ListForBookingAsync(h.World.OrgWide, h.Granted.BookingId));

        NothingWasSent(h);
        h.Client.Verify(c => c.GetReviewAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var untouched = await h.ReviewOfAsync(h.Granted);
        Assert.Equal(OtaReviewActionState.None, untouched.HostReplyState);
        Assert.Null(untouched.HostReplyText);

        // The org that owns it reads it: the setup is sound.
        await using var owner = h.AsRequestOf(h.Granted.OrgId);
        Assert.Equal(review.Id, (await owner.Service.GetAsync(h.World.OrgWide, review.Id)).Id);
        Assert.Equal(review.PropertyId, await owner.Service.FindPropertyIdAsync(review.Id));
    }

    [Fact]
    public async Task OtaReview_CollaboratorSeesOnlyTheGrantedProperties_Test()
    {
        var h = await OtaReviewHarness.CreateAsync();
        var granted = await PublishAsync(h, h.Granted);
        var hidden = await PublishAsync(h, h.Hidden);
        var scope = h.World.Restricted;

        await using var rig = h.AsRequestOf(h.Granted.OrgId);
        var service = rig.Service;

        Assert.Equal(granted.Id, (await service.GetAsync(scope, granted.Id)).Id);
        Assert.Single((await service.ListForBookingAsync(scope, h.Granted.BookingId)).Items);
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(scope, hidden.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => service.ListForBookingAsync(scope, h.Hidden.BookingId));
        await Assert.ThrowsAsync<NotFoundException>(() => service.RefreshAsync(scope, h.Hidden.BookingId));

        // The owner of the org reads both.
        Assert.Equal(hidden.Id, (await service.GetAsync(h.World.OrgWide, hidden.Id)).Id);
    }

    // ─── Privacy ───

    [Fact]
    public async Task GuestErasure_EmptiesTheWordsOfTheGuest_KeepsTheHostsOwn_AndALaterReadBringsNothingBack()
    {
        var h = await OtaReviewHarness.CreateAsync();
        var erased = await PublishAsync(h, h.Granted, "Siamo stati benissimo, io sono Giulietta Verdi.");
        var kept = await PublishAsync(h, h.Hidden, "Casa pulita.");
        await ReplyAsync(h, h.Granted, erased.Id, ReplyText, KeyOne);
        var guestId = await h.ReadAsync(db => db.Bookings.Where(b => b.Id == h.Granted.BookingId).Select(b => b.GuestId).SingleAsync());

        await using (var db = h.NewDb())
        {
            var guest = await db.Guests.SingleAsync(g => g.Id == guestId);
            var eraser = new GuestDataEraser(db, Mock.Of<IFileStorage>(), NullLogger<GuestDataEraser>.Instance);

            var outcome = await eraser.AnonymizeAsync(guest, OtaReviewHarness.Now.UtcDateTime, CancellationToken.None);
            await db.SaveChangesAsync();

            Assert.True(outcome.Changed);
        }

        var afterErasure = await h.ReviewOfAsync(h.Granted);
        Assert.Null(afterErasure.GuestReviewText);
        Assert.True(afterErasure.GuestReviewPublished);
        Assert.Equal(5m, afterErasure.GuestRating);
        Assert.Equal(ReplyText, afterErasure.HostReplyText);
        Assert.Equal("Casa pulita.", (await h.ReviewOfAsync(h.Hidden)).GuestReviewText);
        Assert.Equal(kept.Id, (await h.ReviewOfAsync(h.Hidden)).Id);

        // The channel manager still holds the words of the guest: the next read does not store them again.
        h.ChannelHasReview(h.Granted, "Siamo stati benissimo, io sono Giulietta Verdi.");
        h.Clock.Advance(TimeSpan.FromHours(7));
        var later = await h.SyncAsync(h.Granted);
        Assert.Null(later.GuestReviewText);
        Assert.True(later.GuestReviewPublished);
    }
}
