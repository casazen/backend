using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Xunit;

namespace Casazen.Tests.Unit.Reviews;

/// <summary>
/// The one place that turns the answer of the reviews of the channel manager into what CasaZen keeps (E05/F06/US01/T02, OCT-12): the two shapes it reads, what a review that is not
/// published carries (nothing), what a read that says less never takes back, and how an action nobody confirmed ends. The member names are documented, not proven (the sandbox has no
/// reviews): these tests pin the tolerant reader so that the day the real answer shows another name, this is the file that changes.
/// </summary>
public class OtaReviewMapperTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CheckOut = new(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);

    private static OctorateReview? Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        return OtaReviewMapper.ReadReview(document.RootElement);
    }

    private static OtaReview NewRow() => new()
    {
        OrgId = Guid.NewGuid(),
        PropertyId = Guid.NewGuid(),
        BookingId = Guid.NewGuid(),
        Channel = OtaChannel.Airbnb,
        OctorateRefer = "HM1",
    };

    // ─── The wire ───

    [Fact]
    public void ReadReview_AListOfAGuestAndAHostReview_ReadsBothWithTheirWords()
    {
        var review = Read("""
            [
              { "type": "GUEST_TO_HOST", "status": "REVIEW_PUBLISHED", "overallRating": 4.5, "publicReview": "  Posto bello e pulito.  ",
                "publishedAt": "2026-11-18T10:00:00Z", "hostReply": { "text": "Grazie!", "publishedAt": "2026-11-19T09:30:00Z" },
                "categoryRatings": [ { "category": "Cleanliness", "rating": 5 }, { "category": "Communication", "rating": 4 }, { "category": "cleanliness", "rating": 1 } ] },
              { "type": "HOST_TO_GUEST", "status": "PENDING" }
            ]
            """);

        Assert.NotNull(review);
        var guest = review.Guest;
        Assert.NotNull(guest);
        Assert.True(guest.Published);
        Assert.Equal(4.5m, guest.Rating);
        Assert.Equal("Posto bello e pulito.", guest.Text);
        Assert.Equal(new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc), guest.PublishedAt);
        Assert.Equal("Grazie!", guest.HostReply);
        Assert.Equal(new DateTime(2026, 11, 19, 9, 30, 0, DateTimeKind.Utc), guest.HostRepliedAt);

        // The categories are named in lower case, once each (the first value wins).
        Assert.Equal([new OctorateReviewRating("cleanliness", 5m), new OctorateReviewRating("communication", 4m)], guest.Ratings);

        Assert.NotNull(review.Host);
        Assert.Equal(HostReviewWindow.Open, review.Host.Window);
        Assert.Null(review.Host.SentAt);
    }

    [Fact]
    public void ReadReview_AnObjectWithTheTwoReviewsAsMembers_ReadsBoth()
    {
        var review = Read("""
            { "guestReview": { "status": "PUBLISHED", "rating": 5, "comment": "Perfetto" },
              "hostReview": { "status": "PUBLISHED", "publishedAt": "2026-11-18T12:00:00Z" } }
            """);

        Assert.NotNull(review);
        Assert.Equal("Perfetto", review.Guest?.Text);
        Assert.Equal(5m, review.Guest?.Rating);
        Assert.Equal(HostReviewWindow.Sent, review.Host?.Window);
        Assert.Equal(new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc), review.Host?.SentAt);
    }

    [Fact]
    public void ReadReview_TheDataOfTheWrapper_IsTheAnswer()
    {
        var review = Read("""{ "data": [ { "direction": "GUEST_TO_HOST", "status": "REVIEW_PUBLISHED", "rating": 3, "text": "Ok" } ] }""");

        Assert.NotNull(review);
        Assert.Equal("Ok", review.Guest?.Text);
        Assert.Null(review.Host);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{ "data": [] }""")]
    [InlineData("""{ "data": null }""")]
    [InlineData("null")]
    [InlineData("{}")]
    public void ReadReview_AnAnswerThatSaysThereIsNone_IsEmptyAndNotAnError(string json)
    {
        var review = Read(json);

        Assert.NotNull(review);
        Assert.True(review.IsEmpty);
    }

    [Theory]
    [InlineData("\"nothing\"")]
    [InlineData("42")]
    [InlineData("""{ "unexpected": "shape", "members": 1 }""")]
    [InlineData("""[ { "unexpected": "shape" } ]""")]
    [InlineData("""[ "a", "b" ]""")]
    public void ReadReview_AnAnswerNobodyCanRead_IsNullAndNeverAnEmptyReview(string json)
    {
        Assert.Null(Read(json));
    }

    [Theory]
    [InlineData("UNPUBLISHED")]
    [InlineData("NOT_PUBLISHED")]
    [InlineData("PENDING")]
    [InlineData("REVIEW_EXPIRED")]
    public void ReadReview_AReviewThatIsNotPublished_CarriesNoTextNoRatingAndNoReply(string status)
    {
        var review = Read($$"""
            { "guestReview": { "status": "{{status}}", "rating": 5, "publicReview": "Segreto", "hostReply": "Anche questo" } }
            """);

        var guest = review?.Guest;
        Assert.NotNull(guest);
        Assert.False(guest.Published);
        Assert.Null(guest.Text);
        Assert.Null(guest.Rating);
        Assert.Null(guest.HostReply);
        Assert.Empty(guest.Ratings);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.5")]
    [InlineData("5.5")]
    [InlineData("10")]
    public void ReadReview_ARatingOutsideTheScaleOfOneToFive_IsNotGuessedAt(string rating)
    {
        var review = Read($$"""{ "guestReview": { "status": "REVIEW_PUBLISHED", "rating": {{rating}}, "comment": "x" } }""");

        var guest = review?.Guest;
        Assert.NotNull(guest);
        Assert.True(guest.Published);
        Assert.Null(guest.Rating);
    }

    [Fact]
    public void ReadReview_TheWordsAreClippedToWhatIsKept()
    {
        var words = new string('a', OtaReviewMapper.MaxKeptTextLength + 500);
        var review = Read($$"""{ "guestReview": { "status": "REVIEW_PUBLISHED", "rating": 4, "comment": "{{words}}" } }""");

        Assert.Equal(OtaReviewMapper.MaxKeptTextLength, review?.Guest?.Text?.Length);
    }

    [Theory]
    [InlineData("PUBLISHED", HostReviewWindow.Sent)]
    [InlineData("SUBMITTED", HostReviewWindow.Sent)]
    [InlineData("NOT_PUBLISHED", HostReviewWindow.Unknown)]
    [InlineData("EXPIRED", HostReviewWindow.Closed)]
    [InlineData("CLOSED", HostReviewWindow.Closed)]
    [InlineData("OPEN", HostReviewWindow.Open)]
    [InlineData("WAITING", HostReviewWindow.Open)]
    [InlineData("SOMETHING_ELSE", HostReviewWindow.Unknown)]
    public void ReadReview_TheWindowOfTheHostReview_IsWhatTheStatusSays(string status, HostReviewWindow expected)
    {
        var review = Read($$"""{ "hostReview": { "status": "{{status}}" } }""");

        Assert.Equal(expected, review?.Host?.Window);
    }

    // ─── What CasaZen keeps ───

    [Fact]
    public void Apply_ANewPublishedReview_SetsTheRowAndIsNewOnlyTheFirstTime()
    {
        var row = NewRow();
        var answer = new OctorateReview(
            new OctorateGuestReview
            {
                Published = true,
                PublishedAt = Now.AddDays(-1),
                Rating = 4m,
                Ratings = [new OctorateReviewRating("cleanliness", 5m)],
                Text = "Bello",
            },
            new OctorateHostReviewInfo(HostReviewWindow.Open, null));

        var first = OtaReviewMapper.Apply(row, answer, CheckOut, guestErased: false, Now);
        var second = OtaReviewMapper.Apply(row, answer, CheckOut, guestErased: false, Now.AddHours(6));

        Assert.True(first.NewlyPublished);
        Assert.False(second.NewlyPublished);
        Assert.True(row.GuestReviewPublished);
        Assert.Equal(Now.AddDays(-1), row.GuestReviewPublishedAt);
        Assert.Equal(4m, row.GuestRating);
        Assert.Equal("Bello", row.GuestReviewText);
        Assert.Equal(HostReviewWindow.Open, row.HostReviewWindow);
        Assert.Equal(2, row.PollCount);
        Assert.Equal(Now.AddHours(6), row.LastSyncedAt);
        Assert.Null(row.LastSyncError);

        var ratings = OtaReviewMapper.ReadRatingsJson(row.GuestCategoryRatingsJson);
        Assert.Equal([new ReviewCategoryRating("cleanliness", 5m)], ratings);
    }

    [Fact]
    public void Apply_AReadThatSaysLess_NeverTakesAPublishedReviewBack()
    {
        var row = NewRow();
        OtaReviewMapper.Apply(
            row,
            new OctorateReview(new OctorateGuestReview { Published = true, Rating = 5m, Text = "Bello" }, null),
            CheckOut,
            guestErased: false,
            Now);

        var later = OtaReviewMapper.Apply(row, new OctorateReview(null, null), CheckOut, guestErased: false, Now.AddHours(6));
        var unpublished = OtaReviewMapper.Apply(
            row,
            new OctorateReview(new OctorateGuestReview { Published = false }, null),
            CheckOut,
            guestErased: false,
            Now.AddHours(12));

        Assert.False(later.NewlyPublished);
        Assert.False(unpublished.NewlyPublished);
        Assert.True(row.GuestReviewPublished);
        Assert.Equal(5m, row.GuestRating);
        Assert.Equal("Bello", row.GuestReviewText);
    }

    [Fact]
    public void Apply_ForAnErasedGuest_StoresNoWordsOfTheGuest()
    {
        var row = NewRow();

        var applied = OtaReviewMapper.Apply(
            row,
            new OctorateReview(new OctorateGuestReview { Published = true, Rating = 4m, Text = "Mi chiamo Giulietta Verdi" }, null),
            CheckOut,
            guestErased: true,
            Now);

        // The mark and the state are kept (the reputation of the house is not an identity), the words are not.
        Assert.True(applied.NewlyPublished);
        Assert.True(row.GuestReviewPublished);
        Assert.Equal(4m, row.GuestRating);
        Assert.Null(row.GuestReviewText);
    }

    [Fact]
    public void Apply_AReplyThatTheChannelShows_CompletesTheOneNobodyConfirmed()
    {
        var row = NewRow();
        row.GuestReviewPublished = true;
        row.HostReplyState = OtaReviewActionState.ManualVerify;
        row.HostReplyText = "Grazie!";
        row.HostReplyRequestedAt = Now.AddMinutes(-5);

        var applied = OtaReviewMapper.Apply(
            row,
            new OctorateReview(new OctorateGuestReview { Published = true, Text = "Bello", HostReply = "Grazie!", HostRepliedAt = Now.AddMinutes(-4) }, null),
            CheckOut,
            guestErased: false,
            Now);

        Assert.True(applied.ReplyCompleted);
        Assert.False(applied.ReplyFailed);
        Assert.Equal(OtaReviewActionState.Sent, row.HostReplyState);
        Assert.Equal(Now.AddMinutes(-4), row.HostReplySentAt);
        Assert.Equal("Grazie!", row.HostReplyText);
        Assert.Null(row.HostReplyError);
    }

    [Fact]
    public void Apply_AReplyOfAPersonOnTheChannelItself_IsTheHostsReplyToo()
    {
        var row = NewRow();

        var applied = OtaReviewMapper.Apply(
            row,
            new OctorateReview(new OctorateGuestReview { Published = true, Text = "Bello", HostReply = "Risposta scritta sul canale" }, null),
            CheckOut,
            guestErased: false,
            Now);

        // Not a completion (CasaZen asked for nothing): the channel already holds the reply, so CasaZen sends none.
        Assert.False(applied.ReplyCompleted);
        Assert.Equal(OtaReviewActionState.Sent, row.HostReplyState);
        Assert.Equal("Risposta scritta sul canale", row.HostReplyText);
        Assert.Equal(Now, row.HostReplySentAt);
    }

    [Fact]
    public void Apply_AReplyNobodyConfirmed_IsWaitedForAnHourAndThenItIsAnError()
    {
        var row = NewRow();
        row.GuestReviewPublished = true;
        row.HostReplyState = OtaReviewActionState.ManualVerify;
        row.HostReplyRequestedAt = Now;
        var silent = new OctorateReview(new OctorateGuestReview { Published = true, Text = "Bello" }, null);

        var early = OtaReviewMapper.Apply(row, silent, CheckOut, guestErased: false, Now.AddMinutes(59));
        Assert.False(early.ReplyFailed);
        Assert.Equal(OtaReviewActionState.ManualVerify, row.HostReplyState);
        Assert.Equal(Now.AddMinutes(59), row.NextPollAt);

        var late = OtaReviewMapper.Apply(row, silent, CheckOut, guestErased: false, Now.AddMinutes(61));
        Assert.True(late.ReplyFailed);
        Assert.Equal(OtaReviewActionState.Error, row.HostReplyState);
        Assert.Equal(OtaReviewErrorCodes.ReplyNotDelivered, row.HostReplyError);
    }

    [Fact]
    public void Apply_AReplyTheRowCallsSent_IsNotTurnedBackByAReadThatDoesNotShowItYet()
    {
        var row = NewRow();
        row.GuestReviewPublished = true;
        row.HostReplyState = OtaReviewActionState.Sent;
        row.HostReplyText = "Grazie!";
        row.HostReplySentAt = Now.AddMinutes(-30);

        var applied = OtaReviewMapper.Apply(
            row,
            new OctorateReview(new OctorateGuestReview { Published = true, Text = "Bello" }, null),
            CheckOut,
            guestErased: false,
            Now);

        Assert.False(applied.ReplyFailed);
        Assert.Equal(OtaReviewActionState.Sent, row.HostReplyState);
        Assert.Equal("Grazie!", row.HostReplyText);
    }

    [Fact]
    public void Apply_TheHostReview_FollowsItsWindow_AndAnUnconfirmedSendEndsLikeAReply()
    {
        var row = NewRow();
        row.GuestReviewPublished = true;
        row.HostReviewWindow = HostReviewWindow.Open;
        row.HostReviewState = OtaReviewActionState.ManualVerify;
        row.HostReviewRequestedAt = Now.AddMinutes(-10);

        // The channel shows the review as published: the send is complete and the window is over.
        var done = OtaReviewMapper.Apply(
            row,
            new OctorateReview(null, new OctorateHostReviewInfo(HostReviewWindow.Sent, Now.AddMinutes(-9))),
            CheckOut,
            guestErased: false,
            Now);
        Assert.True(done.HostReviewCompleted);
        Assert.Equal(HostReviewWindow.Sent, row.HostReviewWindow);
        Assert.Equal(OtaReviewActionState.Sent, row.HostReviewState);
        Assert.Equal(Now.AddMinutes(-9), row.HostReviewSentAt);

        // A second row: nothing on the channel after an hour is an error the host can act on.
        var other = NewRow();
        other.HostReviewWindow = HostReviewWindow.Open;
        other.HostReviewState = OtaReviewActionState.ManualVerify;
        other.HostReviewRequestedAt = Now.AddHours(-2);
        var failed = OtaReviewMapper.Apply(other, new OctorateReview(null, new OctorateHostReviewInfo(HostReviewWindow.Open, null)), CheckOut, guestErased: false, Now);
        Assert.True(failed.HostReviewFailed);
        Assert.Equal(OtaReviewActionState.Error, other.HostReviewState);
        Assert.Equal(OtaReviewErrorCodes.ReviewNotDelivered, other.HostReviewError);
    }

    [Fact]
    public void Apply_AWindowThatIsClosedOrSent_IsNeverReopenedByALaterRead()
    {
        var row = NewRow();
        row.HostReviewWindow = HostReviewWindow.Sent;

        OtaReviewMapper.Apply(row, new OctorateReview(null, new OctorateHostReviewInfo(HostReviewWindow.Open, null)), CheckOut, guestErased: false, Now);
        Assert.Equal(HostReviewWindow.Sent, row.HostReviewWindow);

        var closed = NewRow();
        closed.HostReviewWindow = HostReviewWindow.Closed;
        OtaReviewMapper.Apply(closed, new OctorateReview(null, new OctorateHostReviewInfo(HostReviewWindow.Open, null)), CheckOut, guestErased: false, Now);
        Assert.Equal(HostReviewWindow.Closed, closed.HostReviewWindow);
    }

    [Fact]
    public void Apply_WithNothingLeftToWaitFor_TheBookingLeavesTheSchedule()
    {
        var row = NewRow();

        OtaReviewMapper.Apply(
            row,
            new OctorateReview(
                new OctorateGuestReview { Published = true, Text = "Bello", Rating = 5m },
                new OctorateHostReviewInfo(HostReviewWindow.Sent, Now.AddDays(-1))),
            CheckOut,
            guestErased: false,
            Now);

        Assert.Null(row.NextPollAt);
    }

    [Fact]
    public void RatingsJson_RoundTripsTheCategories_AndAJsonThatIsNotTheListReadsAsNone()
    {
        var json = OtaReviewMapper.RatingsJson([new OctorateReviewRating("cleanliness", 5m), new OctorateReviewRating("value", 3m)]);

        Assert.Equal(
            [new ReviewCategoryRating("cleanliness", 5m), new ReviewCategoryRating("value", 3m)],
            OtaReviewMapper.ReadRatingsJson(json));
        Assert.Null(OtaReviewMapper.RatingsJson([]));
        Assert.Empty(OtaReviewMapper.ReadRatingsJson(null));
        Assert.Empty(OtaReviewMapper.ReadRatingsJson("not json"));
        Assert.Empty(OtaReviewMapper.ReadRatingsJson("""{ "category": "x" }"""));
    }
}
