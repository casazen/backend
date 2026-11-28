using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Reviews;

public sealed class ReviewCategoryRatingDto
{
    public string Category { get; init; } = string.Empty;
    public decimal Value { get; init; }

    public static ReviewCategoryRatingDto From(ReviewCategoryRating rating) => new() { Category = rating.Category, Value = rating.Value };
}

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

public sealed class ReplyOtaReviewRequest
{
    public string? Text { get; init; }
}

public sealed class HostReviewRatingRequest
{
    public string? Category { get; init; }
    public decimal Value { get; init; }
}

public sealed class SendHostReviewRequest
{
    public IReadOnlyList<HostReviewRatingRequest>? Ratings { get; init; }
    public string? Text { get; init; }
    public string? PrivateText { get; init; }

    public OtaHostReviewCommand ToCommand() => new(
        (Ratings ?? []).Select(r => new ReviewCategoryRating(r.Category ?? string.Empty, r.Value)).ToList(),
        Text,
        PrivateText);
}
