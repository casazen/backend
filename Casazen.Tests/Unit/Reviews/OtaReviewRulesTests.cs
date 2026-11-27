using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Reviews;

/// <summary>
/// The pure rules of the reviews of the channels (E05/F06/US01/T02): what each channel allows (Airbnb only, S6), when a booking is read again, how the request of a host's review is
/// checked before anything leaves, and what the requests to the channel manager carry.
/// </summary>
public class OtaReviewRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CheckOut = new(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);

    // ─── What each channel allows ───

    [Fact]
    public void Capabilities_Airbnb_ReadsRepliesAndWritesTheHostReview()
    {
        var airbnb = OtaReviewCapabilities.For(OtaChannel.Airbnb);

        Assert.True(airbnb.CanRead);
        Assert.True(airbnb.CanReply);
        Assert.True(airbnb.CanHostReview);
        Assert.Null(airbnb.Reason);
    }

    [Theory]
    [InlineData(OtaChannel.Booking)]
    [InlineData(OtaChannel.Vrbo)]
    [InlineData(OtaChannel.Expedia)]
    public void Capabilities_AChannelTheChannelManagerHasNoReviewsFor_IsClosedWithItsReason(OtaChannel channel)
    {
        var capability = OtaReviewCapabilities.For(channel);

        Assert.False(capability.CanRead);
        Assert.False(capability.CanReply);
        Assert.False(capability.CanHostReview);
        Assert.Equal(OtaReviewCapabilities.ChannelNotIntegrated, capability.Reason);
    }

    [Fact]
    public void Capabilities_EveryChannelOfTheTable_HasARow_AndOnlyAirbnbIsOpen()
    {
        // The list of the channels is the one of the enum: a channel added there is closed here until somebody decides it, never open by default.
        Assert.Equal(Enum.GetValues<OtaChannel>().OrderBy(c => (int)c), OtaReviewCapabilities.Channels.OrderBy(c => (int)c));
        Assert.Equal([OtaChannel.Airbnb], OtaReviewCapabilities.Channels.Where(c => OtaReviewCapabilities.For(c).CanRead));
    }

    [Fact]
    public void Capabilities_OfASource_FollowsWhereTheBookingCameFrom()
    {
        Assert.Equal(OtaReviewCapabilities.Direct, OtaReviewCapabilities.OfSource(BookingSource.Direct));
        Assert.True(OtaReviewCapabilities.OfSource(BookingSource.Direct).CanReply);
        Assert.False(OtaReviewCapabilities.OfSource(BookingSource.Direct).CanHostReview);
        Assert.True(OtaReviewCapabilities.OfSource(BookingSource.Airbnb).CanHostReview);

        foreach (var source in new[] { BookingSource.BookingCom, BookingSource.Vrbo, BookingSource.Expedia, BookingSource.TripAdvisor, BookingSource.Agoda })
            Assert.Equal(OtaReviewCapabilities.ChannelNotIntegrated, OtaReviewCapabilities.OfSource(source).Reason);

        foreach (var source in new[] { BookingSource.Manual, BookingSource.Local, BookingSource.OtherChannel })
        {
            var capability = OtaReviewCapabilities.OfSource(source);
            Assert.False(capability.CanRead);
            Assert.Equal(OtaReviewCapabilities.NoReviewsForSource, capability.Reason);
        }
    }

    // ─── When a booking is read again ───

    [Fact]
    public void PollSchedule_TheFirstFortnight_IsEverySixHours_ThenOnceADayUntilDaySixty()
    {
        Assert.Equal(Now.AddHours(6), OtaReviewPollSchedule.NextPollAt(Now, CheckOut, false, HostReviewWindow.Unknown, OtaReviewActionState.None, OtaReviewActionState.None));

        var later = CheckOut.AddDays(OtaReviewPollSchedule.FastDays).AddHours(1);
        Assert.Equal(later.AddHours(24), OtaReviewPollSchedule.NextPollAt(later, CheckOut, false, HostReviewWindow.Open, OtaReviewActionState.None, OtaReviewActionState.None));

        var over = CheckOut.AddDays(OtaReviewPollSchedule.WindowDays);
        Assert.Null(OtaReviewPollSchedule.NextPollAt(over, CheckOut, false, HostReviewWindow.Open, OtaReviewActionState.None, OtaReviewActionState.None));
    }

    [Fact]
    public void PollSchedule_NothingLeftToWaitFor_LeavesTheSchedule_ButAnUnconfirmedActionNever()
    {
        // The guest's review is out and the host's own is sent or closed: done.
        Assert.Null(OtaReviewPollSchedule.NextPollAt(Now, CheckOut, true, HostReviewWindow.Sent, OtaReviewActionState.Sent, OtaReviewActionState.Sent));
        Assert.Null(OtaReviewPollSchedule.NextPollAt(Now, CheckOut, true, HostReviewWindow.Closed, OtaReviewActionState.None, OtaReviewActionState.None));

        // The guest's review is not out yet: the booking is waited for, even when the host's window is closed.
        Assert.NotNull(OtaReviewPollSchedule.NextPollAt(Now, CheckOut, false, HostReviewWindow.Closed, OtaReviewActionState.None, OtaReviewActionState.None));

        // An action nobody confirmed is read at the next run, whatever else is true (even after the window).
        var after = CheckOut.AddDays(OtaReviewPollSchedule.WindowDays + 5);
        Assert.Equal(after, OtaReviewPollSchedule.NextPollAt(after, CheckOut, true, HostReviewWindow.Sent, OtaReviewActionState.ManualVerify, OtaReviewActionState.Sent));
        Assert.Equal(after, OtaReviewPollSchedule.NextPollAt(after, CheckOut, true, HostReviewWindow.Sent, OtaReviewActionState.Sent, OtaReviewActionState.ManualVerify));
    }

    [Fact]
    public void PollSchedule_AFailureThatMayPass_IsReadAgainSoon_TheOthersTomorrow_AndNothingAfterTheWindow()
    {
        Assert.Equal(Now.AddHours(6), OtaReviewPollSchedule.NextPollAfterFailure(Now, CheckOut, "octorate_unavailable"));
        Assert.Equal(Now.AddHours(6), OtaReviewPollSchedule.NextPollAfterFailure(Now, CheckOut, "octorate_rate_limited"));
        Assert.Equal(Now.AddHours(24), OtaReviewPollSchedule.NextPollAfterFailure(Now, CheckOut, OtaReviewErrorCodes.ReviewNotAvailable));
        Assert.Null(OtaReviewPollSchedule.NextPollAfterFailure(CheckOut.AddDays(OtaReviewPollSchedule.WindowDays), CheckOut, "octorate_unavailable"));
        Assert.Throws<ArgumentException>(() => OtaReviewPollSchedule.NextPollAfterFailure(Now, CheckOut, " "));
    }

    [Fact]
    public void PollSchedule_AFailedReadOfAnUnconfirmedAction_IsReadAgainWhateverTheWindowSays()
    {
        var after = CheckOut.AddDays(OtaReviewPollSchedule.WindowDays + 5);

        // The window is over and nothing waits: the booking leaves the schedule. An action nobody confirmed stays in it, with the pace of the failure.
        Assert.Null(OtaReviewPollSchedule.NextPollAfterFailure(after, CheckOut, "octorate_unavailable"));
        Assert.Equal(after.AddHours(6), OtaReviewPollSchedule.NextPollAfterFailure(after, CheckOut, "octorate_unavailable", unconfirmedAction: true));
        Assert.Equal(after.AddHours(24), OtaReviewPollSchedule.NextPollAfterFailure(after, CheckOut, OtaReviewErrorCodes.ReviewNotAvailable, unconfirmedAction: true));

        // Inside the window the flag changes nothing.
        Assert.Equal(Now.AddHours(6), OtaReviewPollSchedule.NextPollAfterFailure(Now, CheckOut, "octorate_unavailable", unconfirmedAction: true));
    }

    [Fact]
    public void PollSchedule_TheWindowOfAStay_IsTheTimeBetweenItsCheckOutAndSixtyDaysAfter()
    {
        Assert.False(OtaReviewPollSchedule.IsInWindow(CheckOut, CheckOut.AddHours(-1)));
        Assert.True(OtaReviewPollSchedule.IsInWindow(CheckOut, CheckOut));
        Assert.True(OtaReviewPollSchedule.IsInWindow(CheckOut, CheckOut.AddDays(59)));
        Assert.False(OtaReviewPollSchedule.IsInWindow(CheckOut, CheckOut.AddDays(60)));
        Assert.Equal(TimeSpan.FromMinutes(10), OtaReviewPollSchedule.RefreshInterval);
        Assert.Equal(TimeSpan.FromHours(1), OtaReviewPollSchedule.UnconfirmedActionWindow);
    }

    // ─── The host's review, checked before anything leaves ───

    private static OtaHostReviewCommand Command(string? text = "Ospiti ordinati e puntuali.", params (string Category, decimal Value)[] ratings) =>
        new(ratings.Select(r => new ReviewCategoryRating(r.Category, r.Value)).ToList(), text, null);

    [Fact]
    public void HostReview_AValidReview_IsNormalizedAndStoredAsTheHostsOwnWords()
    {
        var (review, stored) = OtaReviewService.HostReviewOf(Command("  Ospiti ordinati.  ", ("Cleanliness", 5m), ("communication", 4m)));

        Assert.Equal([new OctorateReviewRating("cleanliness", 5m), new OctorateReviewRating("communication", 4m)], review.Ratings);
        Assert.Equal("Ospiti ordinati.", review.PublicText);
        Assert.Null(review.PrivateText);
        using var document = JsonDocument.Parse(stored);
        Assert.Equal("Ospiti ordinati.", document.RootElement.GetProperty("text").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("ratings").GetArrayLength());
    }

    [Fact]
    public void HostReview_NoRatingTheScaleOrTheTextAllowDoesNotStandInForOne_Is422()
    {
        var tooLong = new string('a', ReviewRules.TextMaxLength + 1);
        var invalid = new List<OtaHostReviewCommand?>
        {
            null,
            new OtaHostReviewCommand(null!, "Testo", null),
            new OtaHostReviewCommand([], "Testo", null),
            Command("Testo", ("cleanliness", 0m)),
            Command("Testo", ("cleanliness", 6m)),
            Command("Testo", ("cleanliness", 4.5m)),
            Command("Testo", ("", 4m)),
            Command("Testo", ("clean liness", 4m)),
            Command("Testo", ("cleanliness", 4m), ("Cleanliness", 5m)),
            Command(null, ("cleanliness", 4m)),
            Command("   ", ("cleanliness", 4m)),
            Command(tooLong, ("cleanliness", 4m)),
            new OtaHostReviewCommand([new ReviewCategoryRating("cleanliness", 4m)], "Testo", tooLong),
        };

        foreach (var command in invalid)
        {
            var error = Assert.Throws<DomainRuleException>(() => OtaReviewService.HostReviewOf(command));
            Assert.Equal(OtaReviewErrorCodes.HostReviewInvalid, error.Code);
        }
    }

    [Fact]
    public void HostReview_MoreRatingsThanTheChannelHas_Is422_AnEmptyListIsNeverAll()
    {
        var many = Enumerable.Range(0, OtaReviewService.MaxHostReviewRatings + 1).Select(i => ($"category-{i}", 4m)).ToArray();

        var error = Assert.Throws<DomainRuleException>(() => OtaReviewService.HostReviewOf(Command("Testo", many)));

        Assert.Equal(OtaReviewErrorCodes.HostReviewInvalid, error.Code);
    }

    // ─── What the requests carry ───

    [Fact]
    public void Requests_TheAddressOfTheReviewsOfABooking_EscapesWhatItIsMadeOf()
    {
        Assert.Equal("/rest/v2/review/585001/HMGRANTED1", OctorateReviewRequests.Path("585001", "HMGRANTED1"));
        Assert.Equal("/rest/v2/review/585001/A%2FB%3FC", OctorateReviewRequests.Path("585001", "A/B?C"));
    }

    [Fact]
    public void Requests_TheReplyCarriesTheTextAndNothingElse()
    {
        using var document = JsonDocument.Parse(OctorateReviewRequests.HostReplyBody("Grazie di cuore!"));

        var member = Assert.Single(document.RootElement.EnumerateObject());
        Assert.Equal("text", member.Name);
        Assert.Equal("Grazie di cuore!", member.Value.GetString());
    }

    [Fact]
    public void Requests_TheHostReviewCarriesWholeRatings_AndLeavesOutWhatTheHostDidNotWrite()
    {
        var review = new OctorateHostReviewCommand([new OctorateReviewRating("cleanliness", 5m), new OctorateReviewRating("respect_house_rules", 4m)], "Ottimi ospiti", null);

        using var document = JsonDocument.Parse(OctorateReviewRequests.HostReviewBody(review));

        var root = document.RootElement;
        Assert.Equal("Ottimi ospiti", root.GetProperty("publicReview").GetString());
        Assert.False(root.TryGetProperty("privateReview", out _));
        var ratings = root.GetProperty("categoryRatings").EnumerateArray().ToList();
        Assert.Equal(2, ratings.Count);
        Assert.Equal(("cleanliness", 5), (ratings[0].GetProperty("category").GetString(), ratings[0].GetProperty("rating").GetInt32()));

        var withPrivate = review with { PrivateText = "Solo per l'ospite" };
        using var second = JsonDocument.Parse(OctorateReviewRequests.HostReviewBody(withPrivate));
        Assert.Equal("Solo per l'ospite", second.RootElement.GetProperty("privateReview").GetString());
    }

    [Fact]
    public void Requests_AHostReviewWithNoRatingOrOutsideTheScale_IsNeverBuilt()
    {
        Assert.Throws<ArgumentException>(() => OctorateReviewRequests.HostReviewBody(new OctorateHostReviewCommand([], "Testo", null)));
        Assert.Throws<ArgumentException>(
            () => OctorateReviewRequests.HostReviewBody(new OctorateHostReviewCommand([new OctorateReviewRating("cleanliness", 6m)], "Testo", null)));
    }

    // ─── Small rules ───

    [Fact]
    public void Options_AWrongCap_NeverStopsTheJobOrLetsItReadWithoutALimit()
    {
        Assert.Equal(200, new OctorateReviewsOptions().EffectiveMaxBookingsPerRun);
        Assert.Equal(1, new OctorateReviewsOptions { MaxBookingsPerRun = 1 }.EffectiveMaxBookingsPerRun);
        Assert.Equal(OctorateReviewsOptions.MaxBookingsPerRunLimit, new OctorateReviewsOptions { MaxBookingsPerRun = OctorateReviewsOptions.MaxBookingsPerRunLimit }.EffectiveMaxBookingsPerRun);
        Assert.Equal(200, new OctorateReviewsOptions { MaxBookingsPerRun = 0 }.EffectiveMaxBookingsPerRun);
        Assert.Equal(200, new OctorateReviewsOptions { MaxBookingsPerRun = -5 }.EffectiveMaxBookingsPerRun);
        Assert.Equal(200, new OctorateReviewsOptions { MaxBookingsPerRun = 100_000 }.EffectiveMaxBookingsPerRun);
        Assert.Equal("Octorate:Reviews", OctorateReviewsOptions.SectionName);
    }

    [Fact]
    public void Preview_IsTheFirstWordsOnOneLine_AndNullForAReviewWithNoText()
    {
        Assert.Null(ReviewRules.Preview(null));
        Assert.Null(ReviewRules.Preview("   \n  "));
        Assert.Equal("Bello e pulito", ReviewRules.Preview("Bello\ne  pulito"));

        var preview = ReviewRules.Preview(new string('a', ReviewRules.PreviewLength + 50));
        Assert.NotNull(preview);
        Assert.EndsWith("…", preview, StringComparison.Ordinal);
        Assert.Equal(ReviewRules.PreviewLength + 1, preview.Length);
    }

    [Fact]
    public void TheEventOfAReviewOfAChannel_HasAKeyOfItsOwn()
    {
        var id = Guid.NewGuid();

        Assert.NotEqual(ReviewReceivedPayload.DedupKeyOf(id), ReviewReceivedPayload.OtaDedupKeyOf(id));
        Assert.Equal($"ota-review:{id:N}", ReviewReceivedPayload.OtaDedupKeyOf(id));
    }
}
