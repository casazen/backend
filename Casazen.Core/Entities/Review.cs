using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// The review of a stay made through the booking site (DB-12a): one per booking (<see cref="BookingId"/> is unique), written by the
/// guest from the signed link the host sent after the stay, published at once. The host can answer it once and report it, never hide
/// or delete it; only the staff takes one down, after a report (<see cref="ReviewReport"/>).
/// </summary>
/// <remarks>
/// <para><b>Verified</b> means one thing only: the booking is a direct one (the booking site), it was not cancelled and its stay is
/// over (<c>ReviewRules.Evaluate</c>). It does not prove that the guest arrived.</para>
/// <para><b>Personal data.</b> The public sees the first name of the guest (or «Ospite») and the month of the stay, never a surname,
/// an email, a country or a date. The name is copied here, so erasing the guest rewrites it (<c>GuestDataEraser</c>) and the review
/// stays, anonymous. Plain text only; the text is shown as text and never as markup or links.</para>
/// </remarks>
[Table("Reviews")]
[Index(nameof(BookingId), IsUnique = true, Name = "UIX_Reviews_BookingId")]
[Index(nameof(PropertyId), nameof(PublishedAt), Name = "IX_Reviews_PropertyId_PublishedAt")]
[Index(nameof(OrgId), Name = "IX_Reviews_OrgId")]
public class Review : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [ForeignKey(nameof(Property))]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    [ForeignKey(nameof(Booking))]
    public Guid BookingId { get; set; }
    public virtual Booking Booking { get; set; } = null!;

    public int Rating { get; set; }

    [MaxLength(1500)]
    public string? Text { get; set; }

    [Required, MaxLength(100)]
    public string AuthorName { get; set; } = string.Empty;

    public DateTime StayEndedOn { get; set; }

    public ReviewStatus Status { get; set; } = ReviewStatus.Published;

    public DateTime PublishedAt { get; set; }

    [MaxLength(800)]
    public string? HostReply { get; set; }

    public DateTime? HostRepliedAt { get; set; }

    /// <summary>When the staff took it down after a report; null otherwise.</summary>
    public DateTime? RemovedAt { get; set; }

    [MaxLength(500)]
    public string? RemovedReason { get; set; }
}

public enum ReviewStatus
{
    Published = 0,

    Reported = 1,

    Removed = 2,
}

/// <summary>
/// A report of the host about a review (DB-12a): the way a host asks CasaZen to look at a review, since it cannot hide or delete one.
/// One per review, ever (<see cref="ReviewId"/> is unique): a review the staff kept is not reported again. The review stays public
/// until the staff decides; the decision is made through the API (<c>POST api/admin/reviews/reports/{id}/decide</c>), the screen of
/// the staff console is another task.
/// </summary>
[Table("ReviewReports")]
[Index(nameof(ReviewId), IsUnique = true, Name = "UIX_ReviewReports_ReviewId")]
[Index(nameof(Status), nameof(ReportedAt), Name = "IX_ReviewReports_Status_ReportedAt")]
[Index(nameof(OrgId), Name = "IX_ReviewReports_OrgId")]
public class ReviewReport : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [ForeignKey(nameof(Review))]
    public Guid ReviewId { get; set; }
    public virtual Review Review { get; set; } = null!;

    public ReviewReportReason Reason { get; set; }

    [MaxLength(500)]
    public string? Note { get; set; }

    [MaxLength(255)]
    public string? ReportedByUserId { get; set; }

    public DateTime ReportedAt { get; set; }

    public ReviewReportStatus Status { get; set; } = ReviewReportStatus.Open;

    public DateTime? DecidedAt { get; set; }

    [MaxLength(255)]
    public string? DecidedByUserId { get; set; }

    [MaxLength(500)]
    public string? DecisionReason { get; set; }
}

public enum ReviewReportReason
{
    Offensive = 0,

    Spam = 1,

    NotAboutTheStay = 2,

    PersonalData = 3,

    NotAGuest = 4,

    Other = 5,
}

public enum ReviewReportStatus
{
    Open = 0,

    Kept = 1,

    Removed = 2,
}

/// <summary>
/// The mark that the host asked a guest to write a review (DB-12a): the email with the signed link leaves once per booking, only when
/// the host asks, and this row is the proof it was done (<see cref="BookingId"/> is unique, so the second ask loses the race on the index
/// and answers 409). It holds no address and no text. Written before the email is queued, like the other markers of a one-time email.
/// </summary>
[Table("ReviewInvites")]
[Index(nameof(BookingId), IsUnique = true, Name = "UIX_ReviewInvites_BookingId")]
[Index(nameof(OrgId), Name = "IX_ReviewInvites_OrgId")]
public class ReviewInvite : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [ForeignKey(nameof(Booking))]
    public Guid BookingId { get; set; }
    public virtual Booking Booking { get; set; } = null!;

    public DateTime SentAt { get; set; }

    [MaxLength(255)]
    public string? SentByUserId { get; set; }
}
