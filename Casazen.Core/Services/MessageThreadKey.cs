using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Casazen.Core.Services;

public enum MessageThreadSource
{
    Direct = 0,

    Ota = 1,

    Storefront = 2,

    OtaReview = 3,

    Review = 4,
}

/// <summary>
/// The opaque key of a conversation in every endpoint and every DTO of the hub of messages: <c>ota:{thread id}</c> for a conversation of a
/// channel and <c>direct:{booking id}</c> for the one with the guest of a direct booking (which has one thread per booking, so the booking
/// id names it before the first message creates its row). Since E05/F06/US01/T03 the key of a <b>review</b> has the same form:
/// <c>otareview:{id}</c> for the review of a channel and <c>review:{id}</c> for the one of the booking site. The client never builds one: it
/// hands back what the server gave it. A key that is not of this form is a 400 <see cref="InvalidCode"/> at the edge of the API.
/// </summary>
/// <remarks>
/// The same text travels in the events of the outbox (<c>GuestMessageReceivedPayload.ThreadKey</c>) and in the route of the push. The endpoints of a
/// conversation (read, reply, mark as read, templates, drafts) answer 404 <c>thread_not_found</c> to the key of a review, and the endpoints of a review
/// to the key of a conversation (<see cref="IsReview"/>): a key is never read as the other kind.
/// </remarks>
/// <param name="Source">Where the conversation or the review is kept.</param>
/// <param name="Id">The id of the <c>OtaMessageThread</c>, of the <c>OtaReview</c> or of the <c>Review</c>, or of the booking for a direct conversation.</param>
public readonly record struct MessageThreadKey(MessageThreadSource Source, Guid Id)
{
    public const string OtaPrefix = "ota:";

    public const string DirectPrefix = "direct:";

    public const string InvalidCode = "thread_key_invalid";

    public const string StorefrontPrefix = "site:";

    public const string OtaReviewPrefix = "otareview:";

    public const string ReviewPrefix = "review:";

    public const int MaxLength = 10 + 36;

    public bool IsReview => Source is MessageThreadSource.OtaReview or MessageThreadSource.Review;

    public static MessageThreadKey ForOta(Guid threadId) => new(MessageThreadSource.Ota, threadId);

    public static MessageThreadKey ForDirect(Guid bookingId) => new(MessageThreadSource.Direct, bookingId);

    public static MessageThreadKey ForOtaReview(Guid otaReviewId) => new(MessageThreadSource.OtaReview, otaReviewId);

    public static MessageThreadKey ForReview(Guid reviewId) => new(MessageThreadSource.Review, reviewId);

    public static bool TryParse([NotNullWhen(true)] string? value, out MessageThreadKey key)
    {
        key = default;
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
            return false;

        MessageThreadSource source;
        string rest;
        if (value.StartsWith(OtaPrefix, StringComparison.Ordinal))
        {
            source = MessageThreadSource.Ota;
            rest = value[OtaPrefix.Length..];
        }
        else if (value.StartsWith(OtaReviewPrefix, StringComparison.Ordinal))
        {
            source = MessageThreadSource.OtaReview;
            rest = value[OtaReviewPrefix.Length..];
        }
        else if (value.StartsWith(ReviewPrefix, StringComparison.Ordinal))
        {
            source = MessageThreadSource.Review;
            rest = value[ReviewPrefix.Length..];
        }
        else if (value.StartsWith(StorefrontPrefix, StringComparison.Ordinal))
        {
            source = MessageThreadSource.Storefront;
            rest = value[StorefrontPrefix.Length..];
        }
        else if (value.StartsWith(DirectPrefix, StringComparison.Ordinal))
        {
            source = MessageThreadSource.Direct;
            rest = value[DirectPrefix.Length..];
        }
        else
        {
            return false;
        }

        if (rest.Length != 36 || !Guid.TryParseExact(rest, "D", out var id) || id == Guid.Empty)
            return false;

        key = new MessageThreadKey(source, id);
        return true;
    }

    public static MessageThreadKey Parse(string? value) =>
        TryParse(value, out var key) ? key : throw new FormatException("The text is not the key of a message thread.");

    public override string ToString() =>
        (Source switch
        {
            MessageThreadSource.Ota => OtaPrefix,
            MessageThreadSource.Storefront => StorefrontPrefix,
            MessageThreadSource.OtaReview => OtaReviewPrefix,
            MessageThreadSource.Review => ReviewPrefix,
            _ => DirectPrefix,
        }) + Id.ToString("D", CultureInfo.InvariantCulture);
}
