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

    /// <summary>
    /// The reviews of one booking within the reach of <paramref name="scope"/>, direct (the review of the booking site) and of the channel, each with what it allows, and what
    /// the channel of the booking allows even when there is no review yet (AC-NF69-1). <see cref="Exceptions.NotFoundException"/> <c>booking_not_found</c> (404) for a booking of
    /// another org or out of the reach.
    /// </summary>
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

    /// <summary>The property of a review of the caller's org, for the authorization of the action; <c>null</c> when there is none.</summary>
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

    /// <summary>
    /// The host's own review of the guest, once (Airbnb only, the window <see cref="HostReviewWindow.Open"/>: else 409 <c>ota_review_window_closed</c>), with the same safe send as
    /// <see cref="ReplyAsync"/>. At least one rating from 1 to 5 and a public text: 422 <c>ota_review_host_review_invalid</c>.
    /// </summary>
    Task<OtaReviewActionResult> SendHostReviewAsync(Guid reviewId, OtaHostReviewCommand? command, string? idempotencyKey, string? userId, CancellationToken ct = default);
}

/// <summary>The stable codes of the reviews of the channels: what the API answers with and what a row stores (never a text).</summary>
public static class OtaReviewErrorCodes
{
    /// <summary>The channel has no integration for reviews (the same code as <see cref="OtaReviewCapabilities.ChannelNotIntegrated"/>).</summary>
    public const string ChannelNotIntegrated = OtaReviewCapabilities.ChannelNotIntegrated;

    /// <summary>The review of a channel that is not Airbnb cannot be answered or written (422).</summary>
    public const string ChannelNotSupported = "ota_review_channel_not_supported";

    /// <summary>The guest's review is not published yet: nothing to answer (422).</summary>
    public const string NotPublished = "ota_review_not_published";

    /// <summary>The window of the host's review is not open (409).</summary>
    public const string WindowClosed = "ota_review_window_closed";

    /// <summary>The channel manager answered 409 to a reply or a host's review: not allowed, or already sent (409). The next read shows which.</summary>
    public const string ActionNotAllowed = "ota_review_action_not_allowed";

    /// <summary>The reply is on the channel already (409).</summary>
    public const string ReplyAlreadySent = "ota_review_reply_already_sent";

    /// <summary>The host's review is on the channel already (409).</summary>
    public const string HostReviewAlreadySent = "ota_review_host_review_already_sent";

    /// <summary>An earlier attempt whose outcome is not known has to be confirmed (or turn into an error) before another one is made (409).</summary>
    public const string ActionUnconfirmed = "ota_review_action_unconfirmed";

    /// <summary>The ratings or the text of the host's review are not usable (422).</summary>
    public const string HostReviewInvalid = "ota_review_host_review_invalid";

    /// <summary>The reply is empty or longer than <see cref="ReviewRules.ReplyMaxLength"/> (422): the code the reply to a review of the booking site has always had.</summary>
    public const string ReplyInvalid = "review_reply_invalid";

    /// <summary>The key of the send is missing or not 8 to 64 letters, digits, dashes or underscores (422).</summary>
    public const string IdempotencyKeyInvalid = "idempotency_key_invalid";

    /// <summary>The listing of the property has no confirmed mapping: nothing is written to the channel (409, OCT-04).</summary>
    public const string MappingNotConfirmed = "mapping_not_confirmed";

    /// <summary>The review is not there, is of another org or is out of the reach of the caller (404).</summary>
    public const string NotFound = "ota_review_not_found";

    /// <summary>The channel manager does not give the reviews of the accommodation (no permission, or the channel is not connected there): a state of the read, not a failure to retry soon.</summary>
    public const string ReviewNotAvailable = "review_not_available";

    /// <summary>A reply whose outcome nobody confirmed within the hour: it did not reach the channel; the host may try again with a new key.</summary>
    public const string ReplyNotDelivered = "reply_not_delivered";

    /// <summary>The same for the host's own review.</summary>
    public const string ReviewNotDelivered = "review_not_delivered";

    /// <summary>The booking was read less than ten minutes ago (429).</summary>
    public const string RefreshTooSoon = "ota_review_refresh_too_soon";

    /// <summary>The booking is not there, is of another org or is out of the reach of the caller (404).</summary>
    public const string BookingNotFound = "booking_not_found";

    /// <summary>The stay has no reviews to read: it did not happen (a request, a cancelled booking, a no-show) or it has no usable reference on the channel manager (422).</summary>
    public const string BookingNotReviewable = "ota_review_booking_not_reviewable";
}

/// <summary>How a read of one booking ended.</summary>
public enum OtaReviewSyncOutcome
{
    /// <summary>The channel manager answered and the row was updated (or made).</summary>
    Synced = 0,

    /// <summary><c>Features:OctorateConnector</c> is off: nothing was read and nothing was written.</summary>
    ConnectorOff = 1,

    /// <summary>The booking is not there for this org: nothing was read.</summary>
    BookingNotFound = 2,

    /// <summary>The channel of the booking has no integration for reviews: nothing was read, and no row was made.</summary>
    ChannelNotIntegrated = 3,

    /// <summary>The property of the booking has no confirmed listing (nobody decided it, OCT-04): nothing can be read for it.</summary>
    NotReadable = 4,

    /// <summary>The channel manager did not give the reviews (<see cref="OtaReviewSyncResult.ErrorCode"/> says why); the row stores the code and waits for the next read.</summary>
    Failed = 5,

    /// <summary>The stay has no reviews to read: it did not happen (a request, a cancelled booking, a no-show) or it has no reservation on the channel manager. Nothing was read, and no row was made.</summary>
    NotReviewable = 6,
}

/// <summary>What <see cref="IOtaReviewService.SyncBookingAsync"/> did for one booking: how it ended, the code of a failure, and whether the guest's review became published now.</summary>
public sealed record OtaReviewSyncResult(OtaReviewSyncOutcome Outcome, string? ErrorCode = null, bool NewlyPublished = false);

/// <summary>One rating category of a review, with the value it was given.</summary>
public sealed record ReviewCategoryRating(string Category, decimal Value);

/// <summary>The host's reply to a review of a channel: where it stands, its text (the host's own), when the channel took it and the code of the last failure.</summary>
public sealed record ReviewReplyView(OtaReviewActionState State, string? Text, DateTime? SentAt, string? ErrorCode);

/// <summary>The host's own review of a guest: the window, where the send stands, what was sent (the host's own words) and the code of the last failure.</summary>
public sealed record OtaHostReviewView(
    HostReviewWindow Window,
    OtaReviewActionState State,
    IReadOnlyList<ReviewCategoryRating> Ratings,
    string? Text,
    string? PrivateText,
    DateTime? SentAt,
    string? ErrorCode);

/// <summary>
/// What a review allows its host now: the three capabilities of the channel (<see cref="OtaReviewCapabilities"/>) narrowed by what the review is. <paramref name="Reason"/> is
/// the reason of the channel or of the property (<c>channel_not_integrated</c>, <c>mapping_not_confirmed</c>), <c>null</c> while the channel allows; <paramref name="ReplyReason"/>
/// and <paramref name="HostReviewReason"/> say why one action is closed while the other may be open (already answered, window closed, the last attempt waits for a check).
/// </summary>
public sealed record OtaReviewCapabilitiesView(
    bool CanRead,
    bool CanReply,
    bool CanHostReview,
    string? Reason,
    string? ReplyReason = null,
    string? HostReviewReason = null);

/// <summary>
/// One review of a channel as the host reads it (the personal words are the host's to read): the guest's review, the reply and the host's own review, with the state of the
/// read. The key <c>otareview:{id}</c> is the one of the hub of messages.
/// </summary>
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

/// <summary>
/// One review in the list of a booking: where it is kept (<see cref="MessageThreadSource.Review"/> for the review of the booking site, <see cref="MessageThreadSource.OtaReview"/>
/// for the one of a channel), the stars, the first words, and what the host may do with it.
/// </summary>
public sealed record BookingReviewItem(
    MessageThreadKey Key,
    MessageThreadSource Source,
    MessageHubChannel Channel,
    decimal? Rating,
    string? Preview,
    DateTime? PublishedAt,
    OtaReviewActionState ReplyState,
    OtaReviewCapabilitiesView Capabilities);

/// <summary>
/// The reviews of one booking: what the channel of the booking allows even when there is no review yet (<paramref name="ChannelCapabilities"/>), the reviews there are, and the
/// state of the read. <paramref name="CanRefreshAt"/> is the instant the booking may be read again from the channel manager (<c>null</c>: now).
/// </summary>
public sealed record BookingReviewsView(
    Guid BookingId,
    MessageHubChannel Channel,
    OtaReviewCapabilitiesView ChannelCapabilities,
    IReadOnlyList<BookingReviewItem> Items,
    DateTime? LastSyncedAt,
    string? LastSyncError,
    DateTime? CanRefreshAt);

/// <summary>The host's own review as the request carries it: the ratings, the public text and the private text.</summary>
public sealed record OtaHostReviewCommand(IReadOnlyList<ReviewCategoryRating> Ratings, string? Text, string? PrivateText);

/// <summary>
/// How a reply or a host's review ended, for the answer of the API: the review as it is now, the state of the action, whether the key had been seen before (nothing was sent
/// again) and the code of a refusal (<see cref="OtaReviewActionState.Error"/>).
/// </summary>
public sealed record OtaReviewActionResult(OtaReviewView Review, OtaReviewActionState State, bool Replayed, string? ErrorCode);
