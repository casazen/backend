using Casazen.Core.GuestPortal;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// What the public reads and what the guest sends (DB-12a): the reviews of a property of a booking site, its rating summary, and the
/// page behind the signed link of the invitation. Anonymous: the tenant filter is off and every query names the org. Nothing here ever
/// returns a surname, an email, a country, a date of the stay or the id of a booking.
/// </summary>
public interface IPublicReviewService
{
    Task<ReviewLinkInfo> GetPortalReviewAsync(PortalContext portal, CancellationToken ct = default);

    Task<PublicReview> SubmitPortalReviewAsync(PortalContext portal, SubmitReviewCommand command, CancellationToken ct = default);

    Task<PublicReviewPage?> ListAsync(Guid orgId, string propertySlugOrId, int page, int pageSize, CancellationToken cancellationToken = default);

    Task<PublicRatingSummary?> GetSummaryAsync(Guid orgId, Guid propertyId, CancellationToken cancellationToken = default);

    Task<ReviewLinkInfo> GetLinkInfoAsync(Guid bookingId, string token, CancellationToken cancellationToken = default);

    Task<PublicReview> SubmitAsync(Guid bookingId, string token, SubmitReviewCommand command, CancellationToken cancellationToken = default);
}

public sealed record PublicReview(
    Guid Id,
    int Rating,
    string? Text,
    string AuthorName,
    string StayMonth,
    DateTime PublishedAt,
    string? HostReply,
    DateTime? HostRepliedAt,
    bool Verified = true);

public sealed record PublicReviewPage(IReadOnlyList<PublicReview> Items, int TotalCount, int Page, int PageSize);

public sealed record PublicRatingSummary(int Count, decimal Average);

public sealed record ReviewLinkInfo(string PropertyName, DateTime CheckInDate, DateTime CheckOutDate, PublicReview? Review);

public sealed record SubmitReviewCommand(int Rating, string? Text, bool ShowName);

/// <summary>
/// The reviews of the host's own properties and the invitation of the guests (DB-12a). The caller is already authorized on the booking
/// or on the property of the review (<c>booking.read</c> / <c>booking.write</c> and the host scope); the service reads the caller's
/// tenant only. The host answers once and reports; it has no way to hide or delete a review.
/// </summary>
public interface IHostReviewService
{
    Task<HostReviewPage> ListAsync(HostScope scope, Guid? propertyId, int page, int pageSize, CancellationToken cancellationToken = default);

    Task<Guid?> FindPropertyIdAsync(Guid reviewId, CancellationToken cancellationToken = default);

    Task<HostReview> ReplyAsync(Guid reviewId, string text, CancellationToken cancellationToken = default);

    Task<HostReview> ReportAsync(Guid reviewId, ReviewReportReason reason, string? note, string? userId, CancellationToken cancellationToken = default);

    Task<ReviewInviteStatus> GetInviteStatusAsync(Guid bookingId, CancellationToken cancellationToken = default);

    Task SendInviteAsync(Guid bookingId, string? userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The invitation asked for by a routine of the org, the action <c>RequestReview</c> (E26/F01/US01/T01): the same email, the same link and the same rules
    /// as <see cref="SendInviteAsync"/>, once per booking whoever asks (the host's button, a routine, two routines, an event delivered twice), for a booking of
    /// <paramref name="orgId"/> only: nothing here reads through the tenant of a caller, a routine has none. What the invitation cannot be sent for is an answer
    /// (<see cref="ReviewInviteRequestStatus.Skipped"/> with its <see cref="ReviewInviteSkipReasons">reason</see>), never an error that makes the caller try again.
    /// Stricter than the host's button where nobody decides: the stay of a no-show, and a guest who was erased, asked for the erasure or has no address, get no
    /// email. The one failure left is the email that the queue did not take (<see cref="ReviewInviteRequestStatus.NotQueued"/>): the mark is taken back, so the
    /// host may still ask by hand.
    /// </summary>
    Task<ReviewInviteRequestResult> RequestInviteAsync(Guid orgId, Guid bookingId, CancellationToken cancellationToken = default);
}

public enum ReviewInviteRequestStatus
{
    Sent = 0,

    Skipped = 1,

    BookingNotFound = 2,

    NotQueued = 3,
}

/// <summary>What <see cref="IHostReviewService.RequestInviteAsync"/> answers: how it ended, and the reason when the booking was skipped.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Reason">One of <see cref="ReviewInviteSkipReasons"/> when <paramref name="Status"/> is <see cref="ReviewInviteRequestStatus.Skipped"/>; <c>null</c> otherwise.</param>
public sealed record ReviewInviteRequestResult(ReviewInviteRequestStatus Status, string? Reason = null)
{
    public static ReviewInviteRequestResult Sent { get; } = new(ReviewInviteRequestStatus.Sent);

    public static ReviewInviteRequestResult BookingNotFound { get; } = new(ReviewInviteRequestStatus.BookingNotFound);

    public static ReviewInviteRequestResult NotQueued { get; } = new(ReviewInviteRequestStatus.NotQueued);

    public static ReviewInviteRequestResult Skipped(string reason) => new(ReviewInviteRequestStatus.Skipped, reason);
}

/// <summary>
/// The reasons a request for a review is skipped (<see cref="ReviewInviteRequestResult.Reason"/>): stable codes that the routine log shows as the code of a
/// <c>skipped</c> action, translated by the interface. Never a name, an address or any other personal data.
/// </summary>
public static class ReviewInviteSkipReasons
{
    public const string NotDirect = "not_direct";

    public const string ReviewsDisabled = "reviews_disabled";

    public const string NotConfirmed = "not_confirmed";

    public const string NoShow = "no_show";

    public const string TooEarly = "too_early";

    public const string TooLate = "too_late";

    public const string NoEmail = "no_email";

    public const string AlreadyReviewed = "already_reviewed";

    public const string AlreadyInvited = "already_invited";
}

public sealed record HostReview(
    Guid Id,
    Guid PropertyId,
    string PropertyName,
    Guid BookingId,
    int Rating,
    string? Text,
    string AuthorName,
    string StayMonth,
    DateTime PublishedAt,
    ReviewStatus Status,
    string? HostReply,
    DateTime? HostRepliedAt,
    DateTime? ReportedAt);

public sealed record HostReviewPage(IReadOnlyList<HostReview> Items, int TotalCount, int Page, int PageSize);

public sealed record ReviewInviteStatus(ReviewEligibility Eligibility, bool HasGuestEmail, DateTime? InviteSentAt, bool Reviewed);

public enum ReviewReportDecision
{
    Keep = 0,

    Remove = 1,
}

public interface IAdminReviewService
{
    Task<AdminReviewReportPage> ListReportsAsync(ReviewReportStatus? status, int page, int pageSize, CancellationToken cancellationToken = default);

    Task<AdminReviewReport> DecideAsync(Guid reportId, ReviewReportDecision decision, string reason, string? adminUserId, CancellationToken cancellationToken = default);
}

public sealed record AdminReviewReport(
    Guid Id,
    Guid ReviewId,
    Guid OrgId,
    string OrgName,
    string PropertyName,
    ReviewReportReason Reason,
    string? Note,
    DateTime ReportedAt,
    ReviewReportStatus Status,
    DateTime? DecidedAt,
    string? DecisionReason,
    int Rating,
    string? ReviewText,
    string AuthorName,
    string StayMonth,
    DateTime PublishedAt,
    string? HostReply,
    ReviewStatus ReviewStatus);

public sealed record AdminReviewReportPage(IReadOnlyList<AdminReviewReport> Items, int TotalCount, int Page, int PageSize);
