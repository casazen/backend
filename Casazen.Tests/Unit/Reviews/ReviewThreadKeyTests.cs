using Casazen.Core.Entities;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Reviews;

/// <summary>
/// E05/F06/US01/T03: the key of a review in the hub of messages, <c>otareview:{id}</c> for the review of a channel and <c>review:{id}</c> for the one of the booking site, next to the two keys of a
/// conversation. A key is never read as the other kind (<see cref="MessageThreadKey.IsReview"/>), the longest key (<see cref="MessageThreadKey.MaxLength"/>) fits the column of the approval queue,
/// and a malformed key is still refused.
/// </summary>
public class ReviewThreadKeyTests
{
    private static readonly Guid Id = Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6");

    [Fact]
    public void ForOtaReview_And_ForReview_AreThePrefixAndTheGuidWithDashes()
    {
        Assert.Equal("otareview:3fa85f64-5717-4562-b3fc-2c963f66afa6", MessageThreadKey.ForOtaReview(Id).ToString());
        Assert.Equal("review:3fa85f64-5717-4562-b3fc-2c963f66afa6", MessageThreadKey.ForReview(Id).ToString());
    }

    [Theory]
    [InlineData("otareview:3fa85f64-5717-4562-b3fc-2c963f66afa6", MessageThreadSource.OtaReview)]
    [InlineData("review:3fa85f64-5717-4562-b3fc-2c963f66afa6", MessageThreadSource.Review)]
    [InlineData("otareview:3FA85F64-5717-4562-B3FC-2C963F66AFA6", MessageThreadSource.OtaReview)]
    [InlineData("ota:3fa85f64-5717-4562-b3fc-2c963f66afa6", MessageThreadSource.Ota)]
    [InlineData("direct:3fa85f64-5717-4562-b3fc-2c963f66afa6", MessageThreadSource.Direct)]
    public void TryParse_ReadsTheKeyOfAReviewAndTheOnesOfAConversation(string text, MessageThreadSource source)
    {
        Assert.True(MessageThreadKey.TryParse(text, out var key));
        Assert.Equal(source, key.Source);
        Assert.Equal(Id, key.Id);
        Assert.Equal(key, MessageThreadKey.Parse(text));
        Assert.Equal(text.ToLowerInvariant(), key.ToString());
    }

    [Fact]
    public void OnlyTheTwoKeysOfAReview_AreReviews()
    {
        Assert.True(MessageThreadKey.ForOtaReview(Id).IsReview);
        Assert.True(MessageThreadKey.ForReview(Id).IsReview);
        Assert.False(MessageThreadKey.ForOta(Id).IsReview);
        Assert.False(MessageThreadKey.ForDirect(Id).IsReview);
        Assert.False(new MessageThreadKey(MessageThreadSource.Storefront, Id).IsReview);
        Assert.NotEqual(MessageThreadKey.ForOtaReview(Id), MessageThreadKey.ForOta(Id));
        Assert.NotEqual(MessageThreadKey.ForReview(Id), MessageThreadKey.ForDirect(Id));
    }

    [Theory]
    [InlineData("otareview:")]
    [InlineData("review:")]
    [InlineData("otareview:not-a-guid")]
    [InlineData("OTAREVIEW:3fa85f64-5717-4562-b3fc-2c963f66afa6")]
    [InlineData("reviews:3fa85f64-5717-4562-b3fc-2c963f66afa6")]
    [InlineData("otareviews:3fa85f64-5717-4562-b3fc-2c963f66afa6")]
    [InlineData("review:00000000-0000-0000-0000-000000000000")]
    [InlineData("review:3fa85f6457174562b3fc2c963f66afa6")]
    [InlineData("review:{3fa85f64-5717-4562-b3fc-2c963f66afa6}")]
    [InlineData("review:3fa85f64-5717-4562-b3fc-2c963f66afa6 ")]
    [InlineData("review:otareview:3fa85f64-5717-4562-b3fc-2c963f66afa6")]
    [InlineData("otareview:review:3fa85f64-5717-4562-b3fc-2c963f66afa6")]
    public void TryParse_RefusesAMalformedKeyOfAReview(string text)
    {
        Assert.False(MessageThreadKey.TryParse(text, out var key));
        Assert.Equal(default, key);
        Assert.Throws<FormatException>(() => MessageThreadKey.Parse(text));
    }

    [Fact]
    public void TheLongestKey_IsTheOneOfAChannelReview_AndFitsTheColumnOfTheApprovalQueue()
    {
        var longest = MessageThreadKey.ForOtaReview(Id).ToString();

        Assert.Equal(MessageThreadKey.MaxLength, longest.Length);
        Assert.True(MessageThreadKey.TryParse(longest, out _));
        Assert.False(MessageThreadKey.TryParse(longest + "x", out _));
        Assert.False(MessageThreadKey.TryParse("otareview:" + new string('a', 5000), out _));
        Assert.True(MessageThreadKey.MaxLength <= ApprovalQueueItem.ThreadKeyMaxLength);
        Assert.True(MessageThreadKey.ForReview(Id).ToString().Length <= MessageThreadKey.MaxLength);
    }
}
