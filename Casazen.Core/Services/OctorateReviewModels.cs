using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// One rating of a review: the category as the channel names it (<c>cleanliness</c>, <c>communication</c>, ...) and the value it gives (1 to 5).
/// </summary>
public sealed record OctorateReviewRating(string Category, decimal Value);

/// <summary>
/// The review a guest wrote, with the reply of the host to it, as the channel manager reports them for one booking (<c>GET /rest/v2/review/{accommodation}/{refer}</c>,
/// E05/F06/US01/T02, OCT-12). The channel manager shows the guest's review only once the channel published it (<c>REVIEW_PUBLISHED</c>): a review that is not published
/// is <see cref="Published"/> false and carries no text. The text and the reply are personal data: never logged.
/// </summary>
public sealed record OctorateGuestReview
{
    /// <summary>The channel published the review (<c>REVIEW_PUBLISHED</c>). Anything the channel manager says that is not that is «not published»: it is never guessed.</summary>
    public bool Published { get; init; }

    /// <summary>When the channel published it (UTC), when the answer says.</summary>
    public DateTime? PublishedAt { get; init; }

    /// <summary>The overall rating, 1 to 5, when the answer gives one.</summary>
    public decimal? Rating { get; init; }

    /// <summary>The rating categories as the channel reports them; empty when it reports none.</summary>
    public IReadOnlyList<OctorateReviewRating> Ratings { get; init; } = [];

    /// <summary>What the guest wrote. Personal data; <c>null</c> when the guest wrote nothing.</summary>
    public string? Text { get; init; }

    /// <summary>The reply of the host the channel holds for this review; <c>null</c> when it holds none. Personal data.</summary>
    public string? HostReply { get; init; }

    /// <summary>When the channel published the reply of the host (UTC), when the answer says.</summary>
    public DateTime? HostRepliedAt { get; init; }
}

/// <summary>
/// Where the host's own review of the guest stands on the channel (E05/F06/US01/T02): the window the host may write it in, as the channel manager says it.
/// </summary>
/// <param name="Window"><see cref="HostReviewWindow.Open"/>, <see cref="HostReviewWindow.Sent"/>, <see cref="HostReviewWindow.Closed"/>; <see cref="HostReviewWindow.Unknown"/> when the answer does not say.</param>
/// <param name="SentAt">When the host's review was published (UTC), when the answer says.</param>
public sealed record OctorateHostReviewInfo(HostReviewWindow Window, DateTime? SentAt);

/// <summary>
/// What the channel manager reports about the reviews of one booking: the guest's review (with the host's reply) and the host's own review. Either is <c>null</c> when the
/// answer holds nothing of it (the sandbox answers an empty list for every booking: no review, no host review). A wire shape CasaZen cannot read is never turned into one
/// of these: the client raises <c>OctorateServerException</c>.
/// </summary>
public sealed record OctorateReview(OctorateGuestReview? Guest, OctorateHostReviewInfo? Host)
{
    /// <summary>The answer holds neither review.</summary>
    public bool IsEmpty => Guest is null && Host is null;
}

/// <summary>
/// The host's own review of a guest to send (<c>POST /rest/v2/review/{accommodation}/{refer}/sendHostReview</c>, Airbnb only, while the window is open): the rating
/// categories, the public text and the private text (<c>privateReview</c>). The texts are personal data: never logged.
/// </summary>
/// <param name="Ratings">At least one rating, each from 1 to 5.</param>
/// <param name="PublicText">What the guest and the public read; <c>null</c> when the host writes none.</param>
/// <param name="PrivateText">What only the guest reads (<c>privateReview</c>); <c>null</c> when the host writes none.</param>
public sealed record OctorateHostReviewCommand(IReadOnlyList<OctorateReviewRating> Ratings, string? PublicText, string? PrivateText);
