using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Casazen.Core.Services;

/// <summary>Where the rows of a conversation (or of a review) of the hub of messages are kept (E05/F02/US01/T01, E05/F06/US01/T03, ADR-032).</summary>
public enum MessageThreadSource
{
    /// <summary>A conversation with the guest of a direct booking: <c>GuestThread</c> and <c>GuestMessage</c>, one thread per booking.</summary>
    Direct = 0,

    /// <summary>A conversation of a channel (Airbnb, Booking.com, Vrbo, Expedia) copied from the channel manager: <c>OtaMessageThread</c>.</summary>
    Ota = 1,

    Storefront = 2,

    /// <summary>
    /// The review of a stay of a channel, as the channel manager reports it (E05/F06/US01/T03): <c>OtaReview</c>. It is not a conversation: the hub lists it under the filter
    /// «Reviews» and opens it with its own detail (<c>GET api/messages/reviews/{key}</c>), never with the endpoints of a conversation.
    /// </summary>
    OtaReview = 3,

    /// <summary>The review a guest of a direct booking wrote on the booking site (E05/F06/US01/T03): <c>Review</c>. Not a conversation, as <see cref="OtaReview"/>.</summary>
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
    /// <summary>Start of the key of a conversation of a channel.</summary>
    public const string OtaPrefix = "ota:";

    /// <summary>Start of the key of a conversation with the guest of a direct booking.</summary>
    public const string DirectPrefix = "direct:";

    /// <summary>The stable <c>code</c> of the 400 a malformed key answers with.</summary>
    public const string InvalidCode = "thread_key_invalid";

    public const string StorefrontPrefix = "site:";

    /// <summary>Start of the key of the review of a channel (E05/F06/US01/T03).</summary>
    public const string OtaReviewPrefix = "otareview:";

    /// <summary>Start of the key of the review of the booking site (E05/F06/US01/T03).</summary>
    public const string ReviewPrefix = "review:";

    /// <summary>The longest key there is: the longest prefix (<see cref="OtaReviewPrefix"/>) and a guid with dashes. Anything longer is not parsed.</summary>
    public const int MaxLength = 10 + 36;

    /// <summary>The key is the one of a review (of a channel or of the booking site), not of a conversation.</summary>
    public bool IsReview => Source is MessageThreadSource.OtaReview or MessageThreadSource.Review;

    /// <summary>The key of the conversation of a channel stored in the row <paramref name="threadId"/>.</summary>
    public static MessageThreadKey ForOta(Guid threadId) => new(MessageThreadSource.Ota, threadId);

    /// <summary>The key of the conversation with the guest of the direct booking <paramref name="bookingId"/>.</summary>
    public static MessageThreadKey ForDirect(Guid bookingId) => new(MessageThreadSource.Direct, bookingId);

    /// <summary>The key of the review of a channel stored in the row <paramref name="otaReviewId"/>.</summary>
    public static MessageThreadKey ForOtaReview(Guid otaReviewId) => new(MessageThreadSource.OtaReview, otaReviewId);

    /// <summary>The key of the review of the booking site stored in the row <paramref name="reviewId"/>.</summary>
    public static MessageThreadKey ForReview(Guid reviewId) => new(MessageThreadSource.Review, reviewId);

    /// <summary>
    /// Reads a key: the prefix in lower case and a guid with dashes (any case of the hexadecimal digits), nothing else, not the empty guid.
    /// False for anything that is not exactly that.
    /// </summary>
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

    /// <summary>Reads a key; <see cref="FormatException"/> when <see cref="TryParse"/> says it is not one (the API answers 400 <see cref="InvalidCode"/> before it gets here).</summary>
    public static MessageThreadKey Parse(string? value) =>
        TryParse(value, out var key) ? key : throw new FormatException("The text is not the key of a message thread.");

    /// <summary>The key as text: <c>ota:{id}</c>, <c>direct:{id}</c>, <c>otareview:{id}</c> or <c>review:{id}</c>, the guid with dashes in lower case.</summary>
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
