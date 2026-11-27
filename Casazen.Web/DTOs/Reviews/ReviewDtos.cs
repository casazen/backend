using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Web.DTOs.Reviews;

/// <summary>
/// A review as the public reads it (DB-12a): the mark, the text, the first name of the guest (or «Ospite»), the month of the stay, the
/// answer of the host. Nothing else of the guest or of the booking: no surname, email, country, date or id of a booking.
/// <see cref="Verified"/> is what the backend declares (a direct booking, not cancelled, stay over); the page never decides it.
/// </summary>
public sealed class PublicReviewDto
{
    public Guid Id { get; init; }
    public int Rating { get; init; }
    public string? Text { get; init; }
    public string AuthorName { get; init; } = string.Empty;

    public string StayMonth { get; init; } = string.Empty;

    public DateTime PublishedAt { get; init; }
    public string? HostReply { get; init; }
    public DateTime? HostRepliedAt { get; init; }
    public bool Verified { get; init; }

    public static PublicReviewDto From(PublicReview review) => new()
    {
        Id = review.Id,
        Rating = review.Rating,
        Text = review.Text,
        AuthorName = review.AuthorName,
        StayMonth = review.StayMonth,
        PublishedAt = review.PublishedAt,
        HostReply = review.HostReply,
        HostRepliedAt = review.HostRepliedAt,
        Verified = review.Verified,
    };
}

public class ReviewLinkRequest
{
    [Required(ErrorMessage = "ReviewLinkInvalid")]
    [MaxLength(1024, ErrorMessage = "ReviewLinkInvalid")]
    public string Token { get; set; } = string.Empty;
}

public sealed class ReviewLinkInfoDto
{
    public string PropertyName { get; init; } = string.Empty;
    public DateOnly CheckInDate { get; init; }
    public DateOnly CheckOutDate { get; init; }

    public PublicReviewDto? Review { get; init; }

    public static ReviewLinkInfoDto From(ReviewLinkInfo info) => new()
    {
        PropertyName = info.PropertyName,
        CheckInDate = RomeCalendar.DateInRome(info.CheckInDate),
        CheckOutDate = RomeCalendar.DateInRome(info.CheckOutDate),
        Review = info.Review is null ? null : PublicReviewDto.From(info.Review),
    };
}

public sealed class SubmitReviewRequest : ReviewLinkRequest
{
    [Range(ReviewRules.MinRating, ReviewRules.MaxRating, ErrorMessage = "ReviewRatingInvalid")]
    public int Rating { get; set; }

    [MaxLength(ReviewRules.TextMaxLength, ErrorMessage = "ReviewTextTooLong")]
    public string? Text { get; set; }

    public bool ShowName { get; set; } = true;
}

public sealed class HostReviewDto
{
    public Guid Id { get; init; }
    public Guid PropertyId { get; init; }
    public string PropertyName { get; init; } = string.Empty;
    public Guid BookingId { get; init; }
    public int Rating { get; init; }
    public string? Text { get; init; }
    public string AuthorName { get; init; } = string.Empty;
    public string StayMonth { get; init; } = string.Empty;
    public DateTime PublishedAt { get; init; }

    public ReviewStatus Status { get; init; }

    public string? HostReply { get; init; }
    public DateTime? HostRepliedAt { get; init; }

    public DateTime? ReportedAt { get; init; }

    public static HostReviewDto From(HostReview review) => new()
    {
        Id = review.Id,
        PropertyId = review.PropertyId,
        PropertyName = review.PropertyName,
        BookingId = review.BookingId,
        Rating = review.Rating,
        Text = review.Text,
        AuthorName = review.AuthorName,
        StayMonth = review.StayMonth,
        PublishedAt = review.PublishedAt,
        Status = review.Status,
        HostReply = review.HostReply,
        HostRepliedAt = review.HostRepliedAt,
        ReportedAt = review.ReportedAt,
    };
}

public sealed class ReplyReviewRequest
{
    [Required(ErrorMessage = "ReviewReplyInvalid")]
    [MaxLength(ReviewRules.ReplyMaxLength, ErrorMessage = "ReviewReplyInvalid")]
    public string Text { get; set; } = string.Empty;
}

public sealed class ReportReviewRequest
{
    [Required(ErrorMessage = "ReviewReasonRequired")]
    public ReviewReportReason? Reason { get; set; }

    [MaxLength(ReviewRules.ReportNoteMaxLength, ErrorMessage = "ReviewTextTooLong")]
    public string? Note { get; set; }
}

public sealed class ReviewInviteStatusDto
{
    public bool CanInvite { get; init; }
    public string? Reason { get; init; }

    public DateTime? InviteSentAt { get; init; }

    public bool Reviewed { get; init; }

    public static ReviewInviteStatusDto From(ReviewInviteStatus status)
    {
        var reason = status.Eligibility switch
        {
            ReviewEligibility.NotDirectBooking => "not_direct_booking",
            ReviewEligibility.BookingNotConfirmed => "booking_not_confirmed",
            ReviewEligibility.StayNotEnded => "stay_not_ended",
            ReviewEligibility.WindowClosed => "window_closed",
            ReviewEligibility.ReviewsDisabled => "reviews_disabled",
            ReviewEligibility.NoShow => "no_show",
            _ => status.HasGuestEmail ? null : "no_guest_address",
        };

        return new ReviewInviteStatusDto
        {
            CanInvite = reason is null && status.InviteSentAt is null && !status.Reviewed,
            Reason = reason,
            InviteSentAt = status.InviteSentAt,
            Reviewed = status.Reviewed,
        };
    }
}

public sealed class AdminReviewReportDto
{
    public Guid Id { get; init; }
    public Guid ReviewId { get; init; }
    public Guid OrgId { get; init; }
    public string OrgName { get; init; } = string.Empty;
    public string PropertyName { get; init; } = string.Empty;
    public ReviewReportReason Reason { get; init; }
    public string? Note { get; init; }
    public DateTime ReportedAt { get; init; }
    public ReviewReportStatus Status { get; init; }
    public DateTime? DecidedAt { get; init; }
    public string? DecisionReason { get; init; }
    public AdminReportedReviewDto Review { get; init; } = new();

    public static AdminReviewReportDto From(AdminReviewReport report) => new()
    {
        Id = report.Id,
        ReviewId = report.ReviewId,
        OrgId = report.OrgId,
        OrgName = report.OrgName,
        PropertyName = report.PropertyName,
        Reason = report.Reason,
        Note = report.Note,
        ReportedAt = report.ReportedAt,
        Status = report.Status,
        DecidedAt = report.DecidedAt,
        DecisionReason = report.DecisionReason,
        Review = new AdminReportedReviewDto
        {
            Rating = report.Rating,
            Text = report.ReviewText,
            AuthorName = report.AuthorName,
            StayMonth = report.StayMonth,
            PublishedAt = report.PublishedAt,
            HostReply = report.HostReply,
            Status = report.ReviewStatus,
        },
    };
}

public sealed class AdminReportedReviewDto
{
    public int Rating { get; init; }
    public string? Text { get; init; }
    public string AuthorName { get; init; } = string.Empty;
    public string StayMonth { get; init; } = string.Empty;
    public DateTime PublishedAt { get; init; }
    public string? HostReply { get; init; }
    public ReviewStatus Status { get; init; }
}

public sealed class DecideReviewReportRequest
{
    [Required(ErrorMessage = "ReviewDecisionInvalid")]
    public ReviewReportDecision? Decision { get; set; }

    [Required(ErrorMessage = "ReviewDecisionReasonInvalid")]
    [MaxLength(ReviewRules.DecisionReasonMaxLength, ErrorMessage = "ReviewDecisionReasonInvalid")]
    public string Reason { get; set; } = string.Empty;
}
