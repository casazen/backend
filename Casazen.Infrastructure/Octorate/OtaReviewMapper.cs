using System.Globalization;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Octorate;

/// <summary>What <see cref="OtaReviewMapper.Apply"/> found when it laid an answer of the channel manager over a row.</summary>
/// <param name="NewlyPublished">The guest's review became published with this read: the first time CasaZen sees it.</param>
/// <param name="ReplyCompleted">A reply whose outcome was not known was found on the channel: it is <see cref="OtaReviewActionState.Sent"/> now.</param>
/// <param name="ReplyFailed">A reply whose outcome nobody confirmed within the hour is an <see cref="OtaReviewActionState.Error"/> now: it did not reach the channel.</param>
/// <param name="HostReviewCompleted">The same for the host's own review, found on the channel.</param>
/// <param name="HostReviewFailed">The same for the host's own review, turned into an error.</param>
internal readonly record struct OtaReviewApplyResult(bool NewlyPublished, bool ReplyCompleted, bool ReplyFailed, bool HostReviewCompleted, bool HostReviewFailed);

/// <summary>
/// The one place that turns the answer of the reviews of Octorate into what CasaZen keeps, and back (E05/F06/US01/T02, OCT-12): <see cref="ReadReview"/> reads the wire (the
/// client calls it), <see cref="Apply"/> lays what was read over the <see cref="OtaReview"/> row. <b>Documented, not proven:</b> the sandbox has no reviews and no OTA connected,
/// so no answer with a review in it was ever seen; the member names below follow the plan (<c>REVIEW_PUBLISHED</c>, <c>privateReview</c>) and the habits of the other
/// operations of the API, are looked for without regard to case, and a member that is not there is <c>null</c>: never a made-up value. When the first real answer shows another
/// name, this file is the only one to change.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Two shapes are read.</b> An object with the guest's review and the host's review as members (<c>guestReview</c>, <c>hostReview</c>), or a list (bare, or in the <c>data</c>
/// of the wrapper) of reviews, each said to be the guest's or the host's by its <c>type</c> or <c>direction</c> (<c>GUEST_TO_HOST</c>, <c>HOST_TO_GUEST</c>). An empty list is no review,
/// not an error (the sandbox answers it for every booking). Anything else is not read: the client raises <see cref="OctorateServerException"/>, never an empty review.</item>
/// <item><b>Published or nothing.</b> The guest's review is read only when its status says <c>REVIEW_PUBLISHED</c> (or <c>PUBLISHED</c>): any other word, or none, is «not published», and a
/// review that is not published carries no text, no rating and no reply. A published review is never taken back by a later read that says less (a lagging answer must not take a
/// review away).</item>
/// <item><b>The channel is the truth for an unconfirmed action.</b> A reply or a host's review that was sent and whose outcome is not known (<see cref="OtaReviewActionState.ManualVerify"/>)
/// is completed when the channel shows it, and after an hour without it becomes an error the host can act on; an action that is <see cref="OtaReviewActionState.Sent"/> is never turned
/// back by a read that does not show it yet.</item>
/// <item><b>Personal data.</b> The texts are returned and stored, never logged; this class logs nothing.</item>
/// </list>
/// </remarks>
internal static class OtaReviewMapper
{
    /// <summary>The status of a review the channel published, as the channel manager spells it.</summary>
    internal const string PublishedStatus = "REVIEW_PUBLISHED";

    /// <summary>The most characters of a guest's review or of a reply that are kept: far more than any channel lets a person write.</summary>
    internal const int MaxKeptTextLength = 4000;

    private const int MaxCategories = 20;
    private const int MaxCategoryLength = 64;

    /// <summary>The JSON the ratings are kept in: camel case, read without regard to case.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] GuestMembers = ["guestReview", "guest_review", "guest", "reviewFromGuest", "receivedReview"];
    private static readonly string[] HostMembers = ["hostReview", "host_review", "host", "reviewFromHost", "sentReview"];
    private static readonly string[] StatusMembers = ["status", "state", "reviewStatus"];
    private static readonly string[] KindMembers = ["type", "reviewType", "direction", "role", "author", "reviewer"];
    private static readonly string[] RatingMembers = ["overallRating", "overall_rating", "rating", "overall", "score", "stars"];
    private static readonly string[] CategoryMembers = ["categoryRatings", "category_ratings", "ratings", "categories"];
    private static readonly string[] CategoryNameMembers = ["category", "name", "type", "key"];
    private static readonly string[] CategoryValueMembers = ["rating", "value", "score", "stars"];
    private static readonly string[] TextMembers = ["publicReview", "public_review", "comment", "comments", "text", "content", "review", "body"];
    private static readonly string[] ReplyMembers = ["hostReply", "host_reply", "reply", "response", "hostResponse", "ownerResponse"];
    private static readonly string[] ReplyTextMembers = ["text", "content", "message", "reply", "response", "body"];
    private static readonly string[] TimeMembers = ["publishedAt", "publishTime", "publishedTime", "publicationTime", "submittedAt", "createdAt", "createTime"];
    private static readonly string[] ReplyTimeMembers = ["publishedAt", "publishTime", "createdAt", "createTime", "time", "date"];
    private static readonly string[] DeadlineMembers = ["expiresAt", "expireTime", "deadline", "windowEnd", "reviewDeadline", "closesAt"];

    // ─── The wire ───

    /// <summary>
    /// The reviews an answer holds, or <c>null</c> when the answer is none of the shapes the class remarks describe (a string, a number, an object with nothing in it that is a
    /// review): a caller never takes it for «no review». <see cref="OctorateReview.IsEmpty"/> is an answer that says there is none.
    /// </summary>
    internal static OctorateReview? ReadReview(JsonElement root)
    {
        switch (root.ValueKind)
        {
            case JsonValueKind.Null:
                return new OctorateReview(null, null);
            case JsonValueKind.Array:
                return ReadItems(root);
            case JsonValueKind.Object:
                break;
            default:
                return null;
        }

        var body = root;
        if (OctorateJson.Find(root, "data") is { } data)
        {
            switch (data.ValueKind)
            {
                case JsonValueKind.Array:
                    return ReadItems(data);
                case JsonValueKind.Object:
                    body = data;
                    break;
                case JsonValueKind.Null:
                    return new OctorateReview(null, null);
                default:
                    return null;
            }
        }

        var guest = OctorateJson.Find(body, GuestMembers) is { ValueKind: JsonValueKind.Object } g ? ReadGuest(g) : null;
        var host = OctorateJson.Find(body, HostMembers) is { ValueKind: JsonValueKind.Object } h ? ReadHost(h) : null;
        if (guest is not null || host is not null)
            return new OctorateReview(guest, host);

        // One review on its own, said to be the guest's or the host's by its type.
        if (KindOf(body) is { } kind && LooksLikeAReview(body))
            return kind ? new OctorateReview(null, ReadHost(body)) : new OctorateReview(ReadGuest(body), null);

        // An object with nothing of a review in it: an empty answer, as long as it does not look like one that has members CasaZen does not know.
        return body.EnumerateObject().All(property => property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Array or JsonValueKind.Object && IsEmptyValue(property.Value))
            ? new OctorateReview(null, null)
            : null;
    }

    /// <summary>The reviews of a list: each said to be the guest's or the host's by its type; an item that says neither is left out, and a list whose every item is left out is unreadable.</summary>
    private static OctorateReview? ReadItems(JsonElement items)
    {
        OctorateGuestReview? guest = null;
        OctorateHostReviewInfo? host = null;
        var any = false;
        var left = 0;
        foreach (var item in items.EnumerateArray())
        {
            any = true;
            if (item.ValueKind != JsonValueKind.Object || KindOf(item) is not { } isHost)
            {
                left++;
                continue;
            }

            if (isHost)
                host ??= ReadHost(item);
            else
                guest ??= ReadGuest(item);
        }

        return !any || guest is not null || host is not null ? new OctorateReview(guest, host) : left > 0 ? null : new OctorateReview(null, null);
    }

    /// <summary>The guest's review of <paramref name="element"/>: published or not, and only when it is published its rating, categories, text and the reply of the host.</summary>
    private static OctorateGuestReview ReadGuest(JsonElement element)
    {
        var status = OctorateJson.Text(element, StatusMembers);
        var published = IsPublished(element, status);
        if (!published)
            return new OctorateGuestReview { Published = false };

        var reply = ReplyOf(element);
        return new OctorateGuestReview
        {
            Published = true,
            PublishedAt = OctorateJson.Instant(element, TimeMembers),
            Rating = RatingOf(element),
            Ratings = RatingsOf(element),
            Text = Clip(FirstText(element, TextMembers), MaxKeptTextLength),
            HostReply = reply.Text,
            HostRepliedAt = reply.At,
        };
    }

    /// <summary>The host's review of <paramref name="element"/>: where its window stands.</summary>
    private static OctorateHostReviewInfo ReadHost(JsonElement element)
    {
        var status = OctorateJson.Text(element, StatusMembers);
        var sentAt = OctorateJson.Instant(element, TimeMembers);
        var window = WindowOf(element, status);
        return new OctorateHostReviewInfo(window, window == HostReviewWindow.Sent ? sentAt : null);
    }

    /// <summary>
    /// <c>true</c> when the review is the host's, <c>false</c> when it is the guest's, <c>null</c> when its <c>type</c> says neither: <c>GUEST_TO_HOST</c> (what the guest wrote about
    /// the host) is the guest's, <c>HOST_TO_GUEST</c> the host's; a word with only one of the two names is that one's; a word with both and no direction is not read.
    /// </summary>
    private static bool? KindOf(JsonElement element)
    {
        var kind = OctorateJson.Text(element, KindMembers)?.ToUpperInvariant();
        if (string.IsNullOrEmpty(kind))
            return null;

        var words = kind.Split(['_', '-', ' ', '.', '/'], StringSplitOptions.RemoveEmptyEntries);
        var to = Array.IndexOf(words, "TO");
        if (to > 0 && to < words.Length - 1)
        {
            var from = words[..to].Contains("HOST") ? "HOST" : words[..to].Contains("GUEST") ? "GUEST" : null;
            if (from is not null)
                return from == "HOST";
        }

        var host = words.Contains("HOST");
        var guest = words.Contains("GUEST");
        return host == guest ? null : host;
    }

    private static bool LooksLikeAReview(JsonElement element) =>
        OctorateJson.Find(element, StatusMembers) is not null
        || OctorateJson.Find(element, RatingMembers) is not null
        || OctorateJson.Find(element, TextMembers) is not null;

    private static bool IsEmptyValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => true,
        JsonValueKind.Array => value.GetArrayLength() == 0,
        JsonValueKind.Object => !value.EnumerateObject().Any(),
        _ => false,
    };

    /// <summary>The channel published the review: its status says <c>REVIEW_PUBLISHED</c> or <c>PUBLISHED</c> (not <c>UNPUBLISHED</c>, not <c>NOT_PUBLISHED</c>), or it has no status and says <c>published</c> is true.</summary>
    private static bool IsPublished(JsonElement element, string? status)
    {
        if (status is not null)
        {
            var word = Letters(status);
            return word is "REVIEWPUBLISHED" or "PUBLISHED";
        }

        return OctorateJson.Bool(element, "published", "isPublished") == true;
    }

    /// <summary>The overall rating, 1 to 5, to one decimal; <c>null</c> when it is missing or outside the scale (a scale CasaZen does not know is not guessed at).</summary>
    private static decimal? RatingOf(JsonElement element) =>
        ValidRating(OctorateJson.Decimal(element, RatingMembers));

    private static decimal? ValidRating(decimal? value) =>
        value is >= 1m and <= 5m ? Math.Round(value.Value, 1, MidpointRounding.AwayFromZero) : null;

    private static IReadOnlyList<OctorateReviewRating> RatingsOf(JsonElement element)
    {
        if (OctorateJson.Find(element, CategoryMembers) is not { ValueKind: JsonValueKind.Array } items)
            return [];

        var ratings = new List<OctorateReviewRating>();
        foreach (var item in items.EnumerateArray())
        {
            if (ratings.Count >= MaxCategories || item.ValueKind != JsonValueKind.Object)
                continue;

            var category = OctorateJson.Text(item, CategoryNameMembers);
            var value = ValidRating(OctorateJson.Decimal(item, CategoryValueMembers));
            if (category is null || value is null)
                continue;

            category = Clip(category.ToLowerInvariant(), MaxCategoryLength)!;
            if (ratings.All(r => r.Category != category))
                ratings.Add(new OctorateReviewRating(category, value.Value));
        }

        return ratings;
    }

    /// <summary>The reply of the host the review carries: a text, or an object with a text and the time it was published.</summary>
    private static (string? Text, DateTime? At) ReplyOf(JsonElement element)
    {
        if (OctorateJson.Find(element, ReplyMembers) is not { } reply)
            return (null, null);

        if (reply.ValueKind == JsonValueKind.Object)
            return (Clip(FirstText(reply, ReplyTextMembers), MaxKeptTextLength), OctorateJson.Instant(reply, ReplyTimeMembers));

        return (Clip(OctorateJson.TextOf(reply), MaxKeptTextLength), null);
    }

    /// <summary>Where the host's own review stands, from the status the channel gives (<c>PUBLISHED</c>, <c>EXPIRED</c>, <c>PENDING</c>, ...), a flag, or a deadline that is over; <see cref="HostReviewWindow.Unknown"/> when none says.</summary>
    private static HostReviewWindow WindowOf(JsonElement element, string? status)
    {
        if (status is not null)
        {
            var word = Letters(status);
            if (word.Contains("NOT", StringComparison.Ordinal) && (word.Contains("PUBLISHED", StringComparison.Ordinal) || word.Contains("SENT", StringComparison.Ordinal) || word.Contains("SUBMITTED", StringComparison.Ordinal)))
                return HostReviewWindow.Unknown;

            if (word.Contains("UNPUBLISHED", StringComparison.Ordinal))
                return HostReviewWindow.Unknown;

            if (word.Contains("PUBLISHED", StringComparison.Ordinal) || word.Contains("SENT", StringComparison.Ordinal) || word.Contains("SUBMITTED", StringComparison.Ordinal) || word.Contains("COMPLETED", StringComparison.Ordinal))
                return HostReviewWindow.Sent;

            if (word.Contains("EXPIRED", StringComparison.Ordinal) || word.Contains("CLOSED", StringComparison.Ordinal) || word.Contains("NOTALLOWED", StringComparison.Ordinal) || word.Contains("UNAVAILABLE", StringComparison.Ordinal))
                return HostReviewWindow.Closed;

            if (word.Contains("OPEN", StringComparison.Ordinal) || word.Contains("PENDING", StringComparison.Ordinal) || word.Contains("AVAILABLE", StringComparison.Ordinal) || word.Contains("ALLOWED", StringComparison.Ordinal) || word.Contains("WAITING", StringComparison.Ordinal))
                return HostReviewWindow.Open;
        }

        if (OctorateJson.Bool(element, "canReview", "reviewable", "windowOpen", "isOpen") is { } open)
            return open ? HostReviewWindow.Open : HostReviewWindow.Closed;

        return HostReviewWindow.Unknown;
    }

    // ─── What CasaZen keeps ───

    /// <summary>
    /// Lays <paramref name="answer"/> over <paramref name="row"/> (see the remarks of the class for the rules), sets the time of the next read, and says what changed that the service
    /// has to act on. Changes the row in place and saves nothing. <paramref name="guestErased"/>: the guest of the booking was erased or anonymized, so what the guest wrote is not
    /// stored again (the host's own words are).
    /// </summary>
    internal static OtaReviewApplyResult Apply(OtaReview row, OctorateReview answer, DateTime checkOutUtc, bool guestErased, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(answer);

        var newlyPublished = false;
        var guest = answer.Guest;
        if (guest is { Published: true })
        {
            newlyPublished = !row.GuestReviewPublished;
            row.GuestReviewPublished = true;
            row.GuestReviewPublishedAt ??= guest.PublishedAt ?? nowUtc;
            row.GuestRating = guest.Rating;
            row.GuestCategoryRatingsJson = RatingsJson(guest.Ratings);
            row.GuestReviewText = guestErased ? null : guest.Text;
        }

        var (replyCompleted, replyFailed) = ApplyReply(row, guest is { Published: true } ? guest : null, nowUtc);
        var (hostCompleted, hostFailed) = ApplyHostReview(row, answer.Host, nowUtc);

        row.LastSyncedAt = nowUtc;
        row.LastSyncError = null;
        row.PollCount++;
        row.NextPollAt = OtaReviewPollSchedule.NextPollAt(
            nowUtc, checkOutUtc, row.GuestReviewPublished, row.HostReviewWindow, row.HostReplyState, row.HostReviewState);
        row.UpdatedAt = nowUtc;
        return new OtaReviewApplyResult(newlyPublished, replyCompleted, replyFailed, hostCompleted, hostFailed);
    }

    /// <summary>
    /// The reply of the host against what the channel holds. A reply on the channel is the host's, whoever sent it (a person on the channel itself too): the row is
    /// <see cref="OtaReviewActionState.Sent"/>. A reply CasaZen asked for whose outcome is not known and that the channel does not show is an error after
    /// <see cref="OtaReviewPollSchedule.UnconfirmedActionWindow"/>; one the row calls sent is not turned back by a read that does not show it yet.
    /// </summary>
    private static (bool Completed, bool Failed) ApplyReply(OtaReview row, OctorateGuestReview? guest, DateTime nowUtc)
    {
        if (!string.IsNullOrWhiteSpace(guest?.HostReply))
        {
            if (row.HostReplyState == OtaReviewActionState.Sent)
                return (false, false);

            var completed = row.HostReplyState == OtaReviewActionState.ManualVerify;
            row.HostReplyText ??= guest.HostReply;
            row.HostReplyState = OtaReviewActionState.Sent;
            row.HostReplySentAt ??= guest.HostRepliedAt ?? nowUtc;
            row.HostReplyError = null;
            return (completed, false);
        }

        if (row.HostReplyState == OtaReviewActionState.ManualVerify
            && row.HostReplyRequestedAt is { } asked
            && nowUtc - asked >= OtaReviewPollSchedule.UnconfirmedActionWindow)
        {
            row.HostReplyState = OtaReviewActionState.Error;
            row.HostReplyError = OtaReviewErrorCodes.ReplyNotDelivered;
            return (false, true);
        }

        return (false, false);
    }

    /// <summary>The host's own review against what the channel says: the window it reports, and the same completion or error of an action nobody confirmed.</summary>
    private static (bool Completed, bool Failed) ApplyHostReview(OtaReview row, OctorateHostReviewInfo? host, DateTime nowUtc)
    {
        var completed = false;
        var failed = false;

        if (host is not null)
        {
            switch (host.Window)
            {
                case HostReviewWindow.Sent:
                    completed = row.HostReviewState == OtaReviewActionState.ManualVerify;
                    row.HostReviewWindow = HostReviewWindow.Sent;
                    row.HostReviewState = OtaReviewActionState.Sent;
                    row.HostReviewSentAt ??= host.SentAt ?? nowUtc;
                    row.HostReviewError = null;
                    break;
                case HostReviewWindow.Closed when row.HostReviewWindow != HostReviewWindow.Sent:
                    row.HostReviewWindow = HostReviewWindow.Closed;
                    break;
                case HostReviewWindow.Open when row.HostReviewWindow is HostReviewWindow.Unknown or HostReviewWindow.Open:
                    row.HostReviewWindow = HostReviewWindow.Open;
                    break;
            }
        }

        if (!completed
            && row.HostReviewState == OtaReviewActionState.ManualVerify
            && row.HostReviewRequestedAt is { } asked
            && nowUtc - asked >= OtaReviewPollSchedule.UnconfirmedActionWindow)
        {
            row.HostReviewState = OtaReviewActionState.Error;
            row.HostReviewError = OtaReviewErrorCodes.ReviewNotDelivered;
            failed = true;
        }

        return (completed, failed);
    }

    /// <summary>The ratings as the row keeps them (a JSON list of <c>{ "category", "value" }</c>); <c>null</c> for none.</summary>
    internal static string? RatingsJson(IReadOnlyCollection<OctorateReviewRating> ratings) =>
        ratings.Count == 0
            ? null
            : JsonSerializer.Serialize(ratings.Select(r => new RatingRow(r.Category, r.Value)), JsonOptions);

    /// <summary>The ratings of a row, read back; empty for none or for a value that is not the list <see cref="RatingsJson"/> writes.</summary>
    internal static IReadOnlyList<ReviewCategoryRating> ReadRatingsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            var rows = JsonSerializer.Deserialize<List<RatingRow>>(json, JsonOptions);
            return rows is null
                ? []
                : [.. rows.Where(r => !string.IsNullOrWhiteSpace(r.Category)).Select(r => new ReviewCategoryRating(r.Category, r.Value))];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record RatingRow(string Category, decimal Value);

    // ─── Small helpers ───

    /// <summary>The first of <paramref name="names"/> that holds a text, trimmed.</summary>
    private static string? FirstText(JsonElement element, string[] names)
    {
        foreach (var name in names)
        {
            if (OctorateJson.Find(element, name) is { ValueKind: JsonValueKind.String } value && OctorateJson.TextOf(value) is { } text)
                return text;
        }

        return null;
    }

    private static string? Clip(string? text, int max) => text is null || text.Length <= max ? text : text[..max];

    /// <summary>The letters of a status, upper case: <c>review_published</c>, <c>REVIEW-PUBLISHED</c> and <c>ReviewPublished</c> are all <c>REVIEWPUBLISHED</c>.</summary>
    private static string Letters(string status) =>
        string.Concat(status.Where(char.IsLetter)).ToUpper(CultureInfo.InvariantCulture);
}
