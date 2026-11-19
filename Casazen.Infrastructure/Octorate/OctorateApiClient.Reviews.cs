using System.Text;
using System.Text.Json;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// The reviews of the channels (E05/F06/US01/T02, OCT-12, OCT-13): the read of the reviews of one booking, the host's reply and the host's own review. A partial of the client
/// so that the operations added by the tasks of other epics never collide with these. Airbnb only on the channel manager's side (S6); CasaZen decides that before any call
/// (<c>OtaReviewCapabilities</c>), so no call is ever made for another channel.
/// </summary>
public sealed partial class OctorateApiClient
{
    public async Task<OctorateReview> GetReviewAsync(string accommodationId, string refer, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationId);
        var reference = CheckedId(refer, nameof(refer));

        var answer = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, Address(OctorateReviewRequests.Path(accommodation, reference))),
            "review-read",
            ct,
            mapError: ReviewErrorOf);

        // «No content» is a booking with no review. Any other answer is read by the one mapper; one it cannot read is not a booking with no review.
        if (answer.StatusCode == 204)
            return new OctorateReview(null, null);

        // The words of the guest are in the answer: it is read and returned, never logged (not even when it cannot be parsed).
        using var document = ParseJson(answer);
        return OtaReviewMapper.ReadReview(document.RootElement) ?? throw new OctorateServerException(answer.StatusCode);
    }

    public async Task SendHostReplyAsync(string accommodationId, string refer, string text, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationId);
        var reference = CheckedId(refer, nameof(refer));
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var json = OctorateReviewRequests.HostReplyBody(text);

        // One attempt, never repeated here (OCT-13): the operation has no idempotency key. SendAsync retries only what Octorate refused before doing anything (a 429, a token it no
        // longer accepts); a timeout or a 5xx is the typed error, and the caller decides after a read of the reviews. The words are in the request, never in anything logged.
        await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, Address(OctorateReviewRequests.Path(accommodation, reference) + "/sendHostReply"))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            },
            "review-host-reply",
            ct,
            mapError: ReviewWriteErrorOf);
    }

    public async Task SendHostReviewAsync(string accommodationId, string refer, OctorateHostReviewCommand review, CancellationToken ct)
    {
        var accommodation = CheckedAccommodationId(accommodationId);
        var reference = CheckedId(refer, nameof(refer));
        ArgumentNullException.ThrowIfNull(review);
        var json = OctorateReviewRequests.HostReviewBody(review);

        await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, Address(OctorateReviewRequests.Path(accommodation, reference) + "/sendHostReview"))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            },
            "review-host-review",
            ct,
            mapError: ReviewWriteErrorOf);
    }

    /// <summary>
    /// The refusal that is the reviews' own: a 403 whose answer is not an error of Octorate's API (no <c>type</c>: the page of the application server, which is what a call without
    /// the module or the permission gets, like the chat) and not the wrong environment. A 403 that is a typed error of the API (a refused token, the wrong environment, a quota)
    /// keeps its common meaning; <c>null</c> for every other answer. Decided by the status and the shape of the answer, never by its text.
    /// </summary>
    internal static OctorateException? ReviewErrorOf(int status, string? errorType, string body) =>
        status == 403 && string.IsNullOrEmpty(errorType) && !IsWrongEnvironment(errorType, body)
            ? OctorateAuthException.ReviewNotAvailable(status)
            : null;

    /// <summary>The refusals of a write: a 409 is «not allowed, or already done» (<c>review_action_not_allowed</c>); the others are those of <see cref="ReviewErrorOf"/>, then the common ones.</summary>
    internal static OctorateException? ReviewWriteErrorOf(int status, string? errorType, string body) =>
        status == 409 ? new OctorateReviewActionNotAllowedException(status) : ReviewErrorOf(status, errorType, body);
}

/// <summary>The paths and bodies of the review requests CasaZen makes (E05/F06/US01/T02), built apart from the client so that they can be read and tested.</summary>
internal static class OctorateReviewRequests
{
    /// <summary>The address of the reviews of one booking: <c>/rest/v2/review/{accommodation}/{refer}</c>.</summary>
    internal static string Path(string accommodationId, string refer) =>
        $"/rest/v2/review/{Uri.EscapeDataString(accommodationId)}/{Uri.EscapeDataString(refer)}";

    /// <summary>The body of the host's reply: its text. <b>Documented, not proven</b> (the member name is the plan's, to be confirmed on the first real Airbnb property).</summary>
    internal static string HostReplyBody(string text) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["text"] = text });

    /// <summary>
    /// The body of the host's own review: the rating categories, the public text and <c>privateReview</c>. A member the host leaves empty is not sent. <b>Documented, not
    /// proven</b>; at least one rating is required here, so «no rating» is never sent as if it were one.
    /// </summary>
    internal static string HostReviewBody(OctorateHostReviewCommand review)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (review.Ratings.Count == 0 || review.Ratings.Any(r => string.IsNullOrWhiteSpace(r.Category) || r.Value is < 1m or > 5m))
            throw new ArgumentException("A host's review has at least one rating, each from 1 to 5.", nameof(review));

        var body = new Dictionary<string, object?>
        {
            ["categoryRatings"] = review.Ratings
                .Select(r => new Dictionary<string, object?>
                {
                    ["category"] = r.Category,
                    ["rating"] = decimal.ToInt32(decimal.Round(r.Value, 0, MidpointRounding.AwayFromZero)),
                })
                .ToList(),
        };
        if (!string.IsNullOrWhiteSpace(review.PublicText))
            body["publicReview"] = review.PublicText;

        if (!string.IsNullOrWhiteSpace(review.PrivateText))
            body["privateReview"] = review.PrivateText;

        return JsonSerializer.Serialize(body);
    }
}
