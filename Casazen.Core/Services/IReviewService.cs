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
    /// <summary>
    /// The public reviews of a published property of the org, newest first, a page at a time (<c>Published</c> and <c>Reported</c>
    /// ones; a removed one is never listed). <c>null</c> when the org has no such property on its site.
    /// </summary>
    Task<PublicReviewPage?> ListAsync(Guid orgId, string propertySlugOrId, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>The count and the average of the public reviews of a property; <c>null</c> when it has none (nothing is shown, not «0»).</summary>
    Task<PublicRatingSummary?> GetSummaryAsync(Guid orgId, Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// What the page of the invitation link shows: the name of the property, the dates of the stay, and the review itself when the
    /// guest already wrote it. <see cref="Exceptions.DomainRuleException"/> <c>review_link_invalid</c> (422) for a link that is not ours,
    /// is expired or is for another booking; <see cref="Exceptions.DomainConflictException"/> <c>review_not_available</c> (409) when the
    /// booking can no longer be reviewed and no review exists.
    /// </summary>
    Task<ReviewLinkInfo> GetLinkInfoAsync(Guid bookingId, string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// The guest's review, published at once. The same errors as <see cref="GetLinkInfoAsync"/>, plus
    /// <see cref="Exceptions.DomainConflictException"/> <c>review_already_submitted</c> (409): one review per booking, whoever is faster.
    /// </summary>
    Task<PublicReview> SubmitAsync(Guid bookingId, string token, SubmitReviewCommand command, CancellationToken cancellationToken = default);
}

/// <summary>A public review as the site shows it. <paramref name="Verified"/> is what the backend declares: always true here.</summary>
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

/// <summary>The count and the average (one decimal) of the public reviews of a property.</summary>
public sealed record PublicRatingSummary(int Count, decimal Average);

/// <summary>The page of the invitation link: <paramref name="Review"/> is set when the guest already reviewed (the page is read-only).</summary>
public sealed record ReviewLinkInfo(string PropertyName, DateTime CheckInDate, DateTime CheckOutDate, PublicReview? Review);

/// <summary>The form of the guest: a mark from 1 to 5, an optional text, and whether the first name may be shown.</summary>
public sealed record SubmitReviewCommand(int Rating, string? Text, bool ShowName);

/// <summary>
/// The reviews of the host's own properties and the invitation of the guests (DB-12a). The caller is already authorized on the booking
/// or on the property of the review (<c>booking.read</c> / <c>booking.write</c> and the host scope); the service reads the caller's
/// tenant only. The host answers once and reports; it has no way to hide or delete a review.
/// </summary>
public interface IHostReviewService
{
    /// <summary>The reviews of the properties the scope reaches, newest first; optionally those of one property (outside the scope: none).</summary>
    Task<HostReviewPage> ListAsync(HostScope scope, Guid? propertyId, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>The property of a review of the caller's org, for the authorization of the action; <c>null</c> when there is none.</summary>
    Task<Guid?> FindPropertyIdAsync(Guid reviewId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Answers a review, once, as written (<see cref="ReviewRules.ReplyMaxLength"/> characters at most).
    /// <see cref="Exceptions.DomainConflictException"/> <c>review_reply_already_sent</c> (409) when there is already an answer,
    /// <see cref="Exceptions.DomainRuleException"/> <c>review_reply_invalid</c> (422) for an empty or too long text.
    /// </summary>
    Task<HostReview> ReplyAsync(Guid reviewId, string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports a review to CasaZen with a reason and a note. The review stays public until the staff decides.
    /// <see cref="Exceptions.DomainConflictException"/> <c>review_already_reported</c> (409): one report per review.
    /// </summary>
    Task<HostReview> ReportAsync(Guid reviewId, ReviewReportReason reason, string? note, string? userId, CancellationToken cancellationToken = default);

    /// <summary>Whether the host may ask the guest of a booking for a review now, and what happened already.</summary>
    Task<ReviewInviteStatus> GetInviteStatusAsync(Guid bookingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the one email with the signed link to the guest of the booking, at the host's explicit request. Written to a third party,
    /// so it leaves from this call or from <see cref="RequestInviteAsync"/> only, once per booking: <see cref="Exceptions.DomainRuleException"/> <c>review_invite_not_allowed</c>
    /// (422) when the booking cannot be reviewed now or the guest has no address, <see cref="Exceptions.DomainConflictException"/>
    /// <c>review_invite_already_sent</c> / <c>review_already_submitted</c> (409).
    /// </summary>
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

/// <summary>How a request of <see cref="IHostReviewService.RequestInviteAsync"/> ended.</summary>
public enum ReviewInviteRequestStatus
{
    /// <summary>The email with the signed link was handed to the queue, and the booking is marked: it leaves once.</summary>
    Sent = 0,

    /// <summary>The booking is not to be invited (<see cref="ReviewInviteRequestResult.Reason"/> says why): nothing was written and nothing was sent.</summary>
    Skipped = 1,

    /// <summary>The org has no such booking: nothing was read of any other org.</summary>
    BookingNotFound = 2,

    /// <summary>The queue did not take the email (the provider is not configured, or it could not be queued): the booking is not marked and nothing left.</summary>
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
    /// <summary>The booking did not come from the booking site (a portal asks its own guests, the host's own entries are not asked): only direct bookings are reviewed.</summary>
    public const string NotDirect = "not_direct";

    /// <summary>The booking was cancelled, or never confirmed.</summary>
    public const string NotConfirmed = "not_confirmed";

    /// <summary>The host marked the stay as a no-show: the guest never came, so there is nothing to review.</summary>
    public const string NoShow = "no_show";

    /// <summary>The check-out day has not passed yet: the first day to review is the day after.</summary>
    public const string TooEarly = "too_early";

    /// <summary>The 14 days after the check-out day are gone: the link would be expired.</summary>
    public const string TooLate = "too_late";

    /// <summary>There is no address to write to: none was given, or the guest was erased or asked for the erasure.</summary>
    public const string NoEmail = "no_email";

    /// <summary>The guest wrote the review already.</summary>
    public const string AlreadyReviewed = "already_reviewed";

    /// <summary>The invitation of this booking was sent already (by the host or by a routine): it leaves once.</summary>
    public const string AlreadyInvited = "already_invited";
}

/// <summary>A review as its host sees it: the first name of the guest, as the public does, and where it stands.</summary>
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

/// <summary>
/// The invitation of one booking for the host's screen: <paramref name="Eligibility"/> says whether it can be reviewed now,
/// <paramref name="HasGuestEmail"/> whether there is an address to write to, <paramref name="InviteSentAt"/> when the host already asked,
/// <paramref name="Reviewed"/> whether the guest already reviewed.
/// </summary>
public sealed record ReviewInviteStatus(ReviewEligibility Eligibility, bool HasGuestEmail, DateTime? InviteSentAt, bool Reviewed);

/// <summary>The decision of the staff about a reported review.</summary>
public enum ReviewReportDecision
{
    /// <summary>The review stays public.</summary>
    Keep = 0,

    /// <summary>The review is taken down.</summary>
    Remove = 1,
}

/// <summary>The reports of the hosts and the decision of the staff (DB-12a): API only, the screen is the staff console's (AM-09).</summary>
public interface IAdminReviewService
{
    /// <summary>The reports of every org, oldest first (the queue), optionally only those in one state.</summary>
    Task<AdminReviewReportPage> ListReportsAsync(ReviewReportStatus? status, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decides an open report: the review stays (<see cref="ReviewStatus.Published"/> again) or is taken down. The reason is required.
    /// <see cref="Exceptions.NotFoundException"/> for an unknown report, <see cref="Exceptions.DomainConflictException"/>
    /// <c>review_report_already_decided</c> (409) for one that was decided.
    /// </summary>
    Task<AdminReviewReport> DecideAsync(Guid reportId, ReviewReportDecision decision, string reason, string? adminUserId, CancellationToken cancellationToken = default);
}

/// <summary>A report with the review it is about, for the staff. Personal data: the name is the first name shown to the public, nothing more.</summary>
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
