using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Reviews;

/// <summary>One rating of a review: the category as the channel names it (<c>cleanliness</c>, <c>communication</c>, ...) and its value, 1 to 5.</summary>
public sealed class ReviewCategoryRatingDto
{
    public string Category { get; init; } = string.Empty;
    public decimal Value { get; init; }

    public static ReviewCategoryRatingDto From(ReviewCategoryRating rating) => new() { Category = rating.Category, Value = rating.Value };
}

/// <summary>The host's reply to a review of a channel: <c>State</c> is <c>None</c>, <c>Sent</c>, <c>Error</c> (with its <c>ErrorCode</c>) or <c>ManualVerify</c> (the outcome is checked on the channel, never sent again by the call).</summary>
public sealed class ReviewReplyDto
{
    public OtaReviewActionState State { get; init; }
    public string? Text { get; init; }
    public DateTime? SentAt { get; init; }
    public string? ErrorCode { get; init; }

    public static ReviewReplyDto From(ReviewReplyView reply) => new()
    {
        State = reply.State,
        Text = reply.Text,
        SentAt = reply.SentAt,
        ErrorCode = reply.ErrorCode,
    };
}

/// <summary>The host's own review of the guest: the <c>Window</c> (<c>Unknown</c>, <c>Open</c>, <c>Sent</c>, <c>Closed</c>), where the send stands, and what the host sent.</summary>
public sealed class OtaHostReviewDto
{
    public HostReviewWindow Window { get; init; }
    public OtaReviewActionState State { get; init; }
    public IReadOnlyList<ReviewCategoryRatingDto> Ratings { get; init; } = [];
    public string? Text { get; init; }
    public string? PrivateText { get; init; }
    public DateTime? SentAt { get; init; }
    public string? ErrorCode { get; init; }

    public static OtaHostReviewDto From(OtaHostReviewView review) => new()
    {
        Window = review.Window,
        State = review.State,
        Ratings = review.Ratings.Select(ReviewCategoryRatingDto.From).ToList(),
        Text = review.Text,
        PrivateText = review.PrivateText,
        SentAt = review.SentAt,
        ErrorCode = review.ErrorCode,
    };
}

/// <summary>
/// What a review allows its host now. <c>Reason</c> is the reason of the channel or of the property (<c>channel_not_integrated</c>, <c>mapping_not_confirmed</c>,
/// <c>octorate_not_connected</c>, <c>no_reviews_for_booking_source</c>) and is absent while the channel allows; <c>ReplyReason</c> and <c>HostReviewReason</c> say why one action is closed
/// while the other may be open (<c>ota_review_reply_already_sent</c>, <c>ota_review_window_closed</c>, <c>ota_review_action_unconfirmed</c>, <c>ota_review_not_published</c>).
/// </summary>
public sealed class OtaReviewCapabilitiesDto
{
    public bool CanRead { get; init; }
    public bool CanReply { get; init; }
    public bool CanHostReview { get; init; }
    public string? Reason { get; init; }
    public string? ReplyReason { get; init; }
    public string? HostReviewReason { get; init; }

    public static OtaReviewCapabilitiesDto From(OtaReviewCapabilitiesView capabilities) => new()
    {
        CanRead = capabilities.CanRead,
        CanReply = capabilities.CanReply,
        CanHostReview = capabilities.CanHostReview,
        Reason = capabilities.Reason,
        ReplyReason = capabilities.ReplyReason,
        HostReviewReason = capabilities.HostReviewReason,
    };
}

/// <summary>
/// A review of a channel (<c>GET api/reviews/ota/{id}</c>): the guest's review (published or not), the reply, the host's own review, the state of the read of the channel
/// (<c>LastSyncedAt</c>, and <c>LastSyncError</c>, a code: <c>review_not_available</c> when the channel manager does not give the reviews of the accommodation) and what it allows.
/// <c>ThreadKey</c> is the key <c>otareview:{id}</c> of the hub of messages. The words of the guest and of the host are personal data: shown as text only.
/// </summary>
public sealed class OtaReviewDto
{
    public Guid Id { get; init; }
    public string ThreadKey { get; init; } = string.Empty;
    public Guid PropertyId { get; init; }
    public string? PropertyName { get; init; }
    public Guid BookingId { get; init; }
    public OtaChannel Channel { get; init; }
    public bool GuestReviewPublished { get; init; }
    public DateTime? GuestReviewPublishedAt { get; init; }
    public decimal? GuestRating { get; init; }
    public IReadOnlyList<ReviewCategoryRatingDto> GuestCategoryRatings { get; init; } = [];
    public string? GuestText { get; init; }
    public ReviewReplyDto Reply { get; init; } = new();
    public OtaHostReviewDto HostReview { get; init; } = new();
    public DateTime? LastSyncedAt { get; init; }
    public string? LastSyncError { get; init; }
    public OtaReviewCapabilitiesDto Capabilities { get; init; } = new();

    public static OtaReviewDto From(OtaReviewView review) => new()
    {
        Id = review.Id,
        ThreadKey = review.Key.ToString(),
        PropertyId = review.PropertyId,
        PropertyName = review.PropertyName,
        BookingId = review.BookingId,
        Channel = review.Channel,
        GuestReviewPublished = review.GuestReviewPublished,
        GuestReviewPublishedAt = review.GuestReviewPublishedAt,
        GuestRating = review.GuestRating,
        GuestCategoryRatings = review.GuestCategoryRatings.Select(ReviewCategoryRatingDto.From).ToList(),
        GuestText = review.GuestText,
        Reply = ReviewReplyDto.From(review.Reply),
        HostReview = OtaHostReviewDto.From(review.HostReview),
        LastSyncedAt = review.LastSyncedAt,
        LastSyncError = review.LastSyncError,
        Capabilities = OtaReviewCapabilitiesDto.From(review.Capabilities),
    };
}

/// <summary>
/// How a reply or a host's review ended (<c>POST api/reviews/ota/{id}/reply</c> and <c>/host-review</c>): the review as it is now, the <c>State</c> of the action, <c>Replayed</c> when the key
/// had been seen (nothing was sent again) and the <c>ErrorCode</c> of a refusal. 200, or 202 when the outcome is not known (<c>ManualVerify</c>): it is checked on the channel and never
/// sent again by the call.
/// </summary>
public sealed class OtaReviewActionDto
{
    public OtaReviewDto Review { get; init; } = new();
    public OtaReviewActionState State { get; init; }
    public bool Replayed { get; init; }
    public string? ErrorCode { get; init; }

    public static OtaReviewActionDto From(OtaReviewActionResult result) => new()
    {
        Review = OtaReviewDto.From(result.Review),
        State = result.State,
        Replayed = result.Replayed,
        ErrorCode = result.ErrorCode,
    };
}

/// <summary>One review in the list of a booking: where it is kept (<c>Source</c> is <c>Review</c> for the booking site, <c>OtaReview</c> for a channel), the stars, the first words and what the host may do.</summary>
public sealed class BookingReviewItemDto
{
    public string ThreadKey { get; init; } = string.Empty;
    public MessageThreadSource Source { get; init; }
    public MessageHubChannel Channel { get; init; }
    public decimal? Rating { get; init; }
    public string? Preview { get; init; }
    public DateTime? PublishedAt { get; init; }
    public OtaReviewActionState ReplyState { get; init; }
    public OtaReviewCapabilitiesDto Capabilities { get; init; } = new();

    public static BookingReviewItemDto From(BookingReviewItem item) => new()
    {
        ThreadKey = item.Key.ToString(),
        Source = item.Source,
        Channel = item.Channel,
        Rating = item.Rating,
        Preview = item.Preview,
        PublishedAt = item.PublishedAt,
        ReplyState = item.ReplyState,
        Capabilities = OtaReviewCapabilitiesDto.From(item.Capabilities),
    };
}

/// <summary>
/// The reviews of a booking (<c>GET api/bookings/{id}/reviews</c>): what the channel of the booking allows even when there is no review yet (<c>ChannelCapabilities</c>: Booking.com, Vrbo and
/// Expedia answer <c>channel_not_integrated</c>, and no review of theirs is ever simulated), the reviews there are, and the state of the read. <c>CanRefreshAt</c> is when the booking may be read
/// again from the channel manager (absent: now).
/// </summary>
public sealed class BookingReviewsDto
{
    public Guid BookingId { get; init; }
    public MessageHubChannel Channel { get; init; }
    public OtaReviewCapabilitiesDto ChannelCapabilities { get; init; } = new();
    public IReadOnlyList<BookingReviewItemDto> Items { get; init; } = [];
    public DateTime? LastSyncedAt { get; init; }
    public string? LastSyncError { get; init; }
    public DateTime? CanRefreshAt { get; init; }

    public static BookingReviewsDto From(BookingReviewsView view) => new()
    {
        BookingId = view.BookingId,
        Channel = view.Channel,
        ChannelCapabilities = OtaReviewCapabilitiesDto.From(view.ChannelCapabilities),
        Items = view.Items.Select(BookingReviewItemDto.From).ToList(),
        LastSyncedAt = view.LastSyncedAt,
        LastSyncError = view.LastSyncError,
        CanRefreshAt = view.CanRefreshAt,
    };
}

/// <summary>The body of <c>POST api/reviews/ota/{id}/reply</c>: the text of the reply (1 to 800 characters).</summary>
public sealed class ReplyOtaReviewRequest
{
    public string? Text { get; init; }
}

/// <summary>One rating of the host's review: a category and a whole value from 1 to 5.</summary>
public sealed class HostReviewRatingRequest
{
    public string? Category { get; init; }
    public decimal Value { get; init; }
}

/// <summary>The body of <c>POST api/reviews/ota/{id}/host-review</c>: at least one rating, the public text, and the private text (shown to the guest only) when there is one.</summary>
public sealed class SendHostReviewRequest
{
    public IReadOnlyList<HostReviewRatingRequest>? Ratings { get; init; }
    public string? Text { get; init; }
    public string? PrivateText { get; init; }

    /// <summary>The command the service takes; a rating without a category is kept with an empty one, which the service refuses.</summary>
    public OtaHostReviewCommand ToCommand() => new(
        (Ratings ?? []).Select(r => new ReviewCategoryRating(r.Category ?? string.Empty, r.Value)).ToList(),
        Text,
        PrivateText);
}
