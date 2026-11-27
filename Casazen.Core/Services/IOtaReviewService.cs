using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The part of the service of the reviews that only reads (E05/F06/US01/T03, NF-69, AI-06): one review of a channel as the host sees it. It is what code that must <b>never send</b> a reply is
/// given: the drafter of the replies writes a draft for a person to approve, and the reply leaves from the approve action alone, through <see cref="IOtaReviewService"/>. With the read in its
/// own interface «the way of the drafts cannot send» is a fact of the types (the drafter has no <c>ReplyAsync</c> to call), and the architecture test keeps it so by forbidding the
/// service that sends, and by pinning that this interface has no member that writes.
/// </summary>
public interface IOtaReviewReader
{
    /// <summary>One review of a channel, within the reach of <paramref name="scope"/>; <see cref="Exceptions.NotFoundException"/> <c>ota_review_not_found</c> (404) otherwise.</summary>
    Task<OtaReviewView> GetAsync(HostScope scope, Guid reviewId, CancellationToken ct = default);
}

/// <summary>
/// The reviews of the channels, read from the channel manager and answered through it (E05/F06/US01/T02, ADR-032, NF-69, OCT-12, OCT-13): Airbnb only. Octorate has no
/// list per property, so every booking is read on its own; nothing is invented for the channels that have no integration for reviews, and no review is ever simulated.
/// Behind <c>Features:OctorateConnector</c>.
/// </summary>
/// <remarks>
/// <para><b>Who may call.</b> The controller authorizes the row (<c>booking.read</c> / <c>booking.write</c> on the property of the booking, the reach of a collaborator
/// «Solo alcuni» in SQL, a row outside it is a 404); the service takes the org from <see cref="IOctorateOrgScope"/>, the one that every call to Octorate acts for, and
/// names it in every query. It checks no role.</para>
/// <para><b>Once.</b> A reply and a host's review are asked of the channel manager at most once for a key. The text and the key are saved <b>first</b>, as
/// <see cref="OtaReviewActionState.ManualVerify"/>; the call comes after, and its answer turns the row into <see cref="OtaReviewActionState.Sent"/> or
/// <see cref="OtaReviewActionState.Error"/>. A call whose outcome is not known (no answer, a 5xx, an answer that cannot be read) stays
/// <see cref="OtaReviewActionState.ManualVerify"/> and is <b>never sent again by the call</b>: the next read finds the reply on the channel and completes it, or after one
/// hour turns it into <c>Error</c> (<see cref="OtaReviewErrorCodes.ReplyNotDelivered"/>), and only then may the host try again, with a new key.</para>
/// <para><b>Personal data.</b> The words of the guest, of the reply and of the host's review are encrypted at rest and never logged.</para>
/// </remarks>
public interface IOtaReviewService : IOtaReviewReader
{
    /// <summary>
    /// Reads the reviews of one booking from the channel manager and stores what it says (the org is the one of <see cref="IOctorateOrgScope"/>). The row of the booking
    /// and channel is made when the channel manager answers; the first time the guest's review is published <see cref="Entities.Enums.DomainEventType.ReviewReceived"/> is
    /// recorded, once, in the same unit of work. A reply or a host's review whose outcome was not known is completed when the channel shows it, and turned into an error the
    /// host can act on after an hour when it does not. A failed read stores its code (never a text) and backs off; nothing is stored for a channel with no integration.
    /// </summary>
    Task<OtaReviewSyncResult> SyncBookingAsync(Guid bookingId, CancellationToken ct = default);

    Task<BookingReviewsView> ListForBookingAsync(HostScope scope, Guid bookingId, CancellationToken ct = default);

    /// <summary>
    /// Reads the booking now (<see cref="SyncBookingAsync"/>) at most once every <see cref="OtaReviewPollSchedule.RefreshInterval"/>: <see cref="Exceptions.DomainTooManyRequestsException"/>
    /// <c>ota_review_refresh_too_soon</c> (429) with <c>retryAfterSeconds</c> after that; <see cref="Exceptions.DomainRuleException"/> <c>channel_not_integrated</c> (422) for a
    /// booking whose channel has no integration for reviews. Answers the reviews of the booking as they are after the read. <b>Nothing is told as a success that was not one:</b> a booking that
    /// did not happen or has no reference on the channel manager is <see cref="Exceptions.DomainRuleException"/> <c>ota_review_booking_not_reviewable</c> (422), a property whose listing nobody
    /// confirmed is <see cref="Exceptions.DomainConflictException"/> <c>mapping_not_confirmed</c> (409), and a read the channel manager refused is stored on the row (its code and the time of the
    /// next read) and raised with its own code (409 <c>review_not_available</c>, 502 <c>octorate_unavailable</c>, ...).
    /// </summary>
    Task<BookingReviewsView> RefreshAsync(HostScope scope, Guid bookingId, CancellationToken ct = default);

    Task<Guid?> FindPropertyIdAsync(Guid reviewId, CancellationToken ct = default);

    /// <summary>
    /// The host's reply to the guest's review, once (Airbnb only: 422 <c>ota_review_channel_not_supported</c>; the review must be published: 422 <c>ota_review_not_published</c>;
    /// 1 to <see cref="ReviewRules.ReplyMaxLength"/> characters: 422 <c>review_reply_invalid</c>; a key of 8 to 64 characters: 422 <c>idempotency_key_invalid</c>). The same key
    /// returns the stored result and sends nothing. A listing nobody confirmed is 409 <c>mapping_not_confirmed</c>; a reply that is on the channel already is 409
    /// <c>ota_review_reply_already_sent</c>; an earlier attempt that was not confirmed is 409 <c>ota_review_action_unconfirmed</c>; a 409 of the channel manager is 409
    /// <c>ota_review_action_not_allowed</c> and the next read finds the truth. A refusal with a code is the state <see cref="OtaReviewActionState.Error"/> with it; a call whose
    /// outcome is not known is <see cref="OtaReviewActionState.ManualVerify"/> and is never sent again by this call.
    /// </summary>
    Task<OtaReviewActionResult> ReplyAsync(Guid reviewId, string? text, string? idempotencyKey, string? userId, CancellationToken ct = default);

    Task<OtaReviewActionResult> SendHostReviewAsync(Guid reviewId, OtaHostReviewCommand? command, string? idempotencyKey, string? userId, CancellationToken ct = default);
}

public static class OtaReviewErrorCodes
{
    public const string ChannelNotIntegrated = OtaReviewCapabilities.ChannelNotIntegrated;

    public const string ChannelNotSupported = "ota_review_channel_not_supported";

    public const string NotPublished = "ota_review_not_published";

    public const string WindowClosed = "ota_review_window_closed";

    public const string ActionNotAllowed = "ota_review_action_not_allowed";

    public const string ReplyAlreadySent = "ota_review_reply_already_sent";

    public const string HostReviewAlreadySent = "ota_review_host_review_already_sent";

    public const string ActionUnconfirmed = "ota_review_action_unconfirmed";

    public const string HostReviewInvalid = "ota_review_host_review_invalid";

    public const string ReplyInvalid = "review_reply_invalid";

    public const string IdempotencyKeyInvalid = "idempotency_key_invalid";

    public const string MappingNotConfirmed = "mapping_not_confirmed";

    public const string NotFound = "ota_review_not_found";

    /// <summary>The channel manager does not give the reviews of the accommodation (no permission, or the channel is not connected there): a state of the read, not a failure to retry soon.</summary>
    public const string ReviewNotAvailable = "review_not_available";

    public const string ReplyNotDelivered = "reply_not_delivered";

    public const string ReviewNotDelivered = "review_not_delivered";

    public const string RefreshTooSoon = "ota_review_refresh_too_soon";

    public const string BookingNotFound = "booking_not_found";

    public const string BookingNotReviewable = "ota_review_booking_not_reviewable";
}

public enum OtaReviewSyncOutcome
{
    Synced = 0,

    ConnectorOff = 1,

    BookingNotFound = 2,

    ChannelNotIntegrated = 3,

    NotReadable = 4,

    Failed = 5,

    NotReviewable = 6,
}

public sealed record OtaReviewSyncResult(OtaReviewSyncOutcome Outcome, string? ErrorCode = null, bool NewlyPublished = false);

public sealed record ReviewCategoryRating(string Category, decimal Value);

public sealed record ReviewReplyView(OtaReviewActionState State, string? Text, DateTime? SentAt, string? ErrorCode);

public sealed record OtaHostReviewView(
    HostReviewWindow Window,
    OtaReviewActionState State,
    IReadOnlyList<ReviewCategoryRating> Ratings,
    string? Text,
    string? PrivateText,
    DateTime? SentAt,
    string? ErrorCode);

public sealed record OtaReviewCapabilitiesView(
    bool CanRead,
    bool CanReply,
    bool CanHostReview,
    string? Reason,
    string? ReplyReason = null,
    string? HostReviewReason = null);

public sealed record OtaReviewView(
    Guid Id,
    MessageThreadKey Key,
    Guid PropertyId,
    string? PropertyName,
    Guid BookingId,
    OtaChannel Channel,
    bool GuestReviewPublished,
    DateTime? GuestReviewPublishedAt,
    decimal? GuestRating,
    IReadOnlyList<ReviewCategoryRating> GuestCategoryRatings,
    string? GuestText,
    ReviewReplyView Reply,
    OtaHostReviewView HostReview,
    DateTime? LastSyncedAt,
    string? LastSyncError,
    OtaReviewCapabilitiesView Capabilities);

public sealed record BookingReviewItem(
    MessageThreadKey Key,
    MessageThreadSource Source,
    MessageHubChannel Channel,
    decimal? Rating,
    string? Preview,
    DateTime? PublishedAt,
    OtaReviewActionState ReplyState,
    OtaReviewCapabilitiesView Capabilities);

public sealed record BookingReviewsView(
    Guid BookingId,
    MessageHubChannel Channel,
    OtaReviewCapabilitiesView ChannelCapabilities,
    IReadOnlyList<BookingReviewItem> Items,
    DateTime? LastSyncedAt,
    string? LastSyncError,
    DateTime? CanRefreshAt);

public sealed record OtaHostReviewCommand(IReadOnlyList<ReviewCategoryRating> Ratings, string? Text, string? PrivateText);

public sealed record OtaReviewActionResult(OtaReviewView Review, OtaReviewActionState State, bool Replayed, string? ErrorCode);
