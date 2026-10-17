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

    /// <summary>The month of the stay, <c>yyyy-MM</c>.</summary>
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

/// <summary>The body of the calls that open the review link: the signed token of the email (it stays out of the address and the logs).</summary>
public class ReviewLinkRequest
{
    [Required(ErrorMessage = "ReviewLinkInvalid")]
    [MaxLength(1024, ErrorMessage = "ReviewLinkInvalid")]
    public string Token { get; set; } = string.Empty;
}

/// <summary>What the page of the link shows: the property, the dates, and the review when it was already written.</summary>
public sealed class ReviewLinkInfoDto
{
    public string PropertyName { get; init; } = string.Empty;
    public DateOnly CheckInDate { get; init; }
    public DateOnly CheckOutDate { get; init; }

    /// <summary>Set when the guest already reviewed this stay: the page is then read-only.</summary>
    public PublicReviewDto? Review { get; init; }

    public static ReviewLinkInfoDto From(ReviewLinkInfo info) => new()
    {
        PropertyName = info.PropertyName,
        CheckInDate = RomeCalendar.DateInRome(info.CheckInDate),
        CheckOutDate = RomeCalendar.DateInRome(info.CheckOutDate),
        Review = info.Review is null ? null : PublicReviewDto.From(info.Review),
    };
}

/// <summary>The review of the guest: a mark from 1 to 5, a text if the guest wants, and whether the first name may be shown.</summary>
public sealed class SubmitReviewRequest : ReviewLinkRequest
{
    [Range(ReviewRules.MinRating, ReviewRules.MaxRating, ErrorMessage = "ReviewRatingInvalid")]
    public int Rating { get; set; }

    [MaxLength(ReviewRules.TextMaxLength, ErrorMessage = "ReviewTextTooLong")]
    public string? Text { get; set; }

    /// <summary>False shows «Ospite» in place of the first name.</summary>
    public bool ShowName { get; set; } = true;
}

/// <summary>A review as its host sees it.</summary>
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

    /// <summary><c>Published</c>, <c>Reported</c> (reported by the host, still public until the staff decides) or <c>Removed</c>.</summary>
    public ReviewStatus Status { get; init; }

    public string? HostReply { get; init; }
    public DateTime? HostRepliedAt { get; init; }

    /// <summary>When the host reported it; <c>null</c> if it did not.</summary>
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

/// <summary>The answer of the host: once, as written.</summary>
public sealed class ReplyReviewRequest
{
    [Required(ErrorMessage = "ReviewReplyInvalid")]
    [MaxLength(ReviewRules.ReplyMaxLength, ErrorMessage = "ReviewReplyInvalid")]
    public string Text { get; set; } = string.Empty;
}

/// <summary>The report of the host to CasaZen: a reason from the list and a note for the staff.</summary>
public sealed class ReportReviewRequest
{
    [Required(ErrorMessage = "ReviewReasonRequired")]
    public ReviewReportReason? Reason { get; set; }

    [MaxLength(ReviewRules.ReportNoteMaxLength, ErrorMessage = "ReviewTextTooLong")]
    public string? Note { get; set; }
}

/// <summary>
/// Whether the host can ask the guest of a booking for a review, and what already happened. <see cref="Reason"/> says why not when
/// <see cref="CanInvite"/> is false and the host has nothing yet to do: <c>not_direct_booking</c>, <c>booking_not_confirmed</c>,
/// <c>stay_not_ended</c>, <c>window_closed</c> or <c>no_guest_address</c>.
/// </summary>
public sealed class ReviewInviteStatusDto
{
    public bool CanInvite { get; init; }
    public string? Reason { get; init; }

    /// <summary>When the host asked; <c>null</c> if it did not.</summary>
    public DateTime? InviteSentAt { get; init; }

    /// <summary>The guest wrote the review.</summary>
    public bool Reviewed { get; init; }

    public static ReviewInviteStatusDto From(ReviewInviteStatus status)
    {
        var reason = status.Eligibility switch
        {
            ReviewEligibility.NotDirectBooking => "not_direct_booking",
            ReviewEligibility.BookingNotConfirmed => "booking_not_confirmed",
            ReviewEligibility.StayNotEnded => "stay_not_ended",
            ReviewEligibility.WindowClosed => "window_closed",
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
