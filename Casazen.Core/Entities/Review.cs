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

    /// <summary>Tenant of the row, copied from the booking (TN-2). Never client-supplied.</summary>
    public Guid OrgId { get; set; }

    [ForeignKey(nameof(Property))]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    [ForeignKey(nameof(Booking))]
    public Guid BookingId { get; set; }
    public virtual Booking Booking { get; set; } = null!;

    /// <summary>The single mark of the stay, 1 to 5 stars.</summary>
    public int Rating { get; set; }

    /// <summary>What the guest wrote (up to <c>ReviewRules.TextMaxLength</c> characters, plain text); null when the guest wrote nothing.</summary>
    [MaxLength(1500)]
    public string? Text { get; set; }

    /// <summary>
    /// How the author is shown: the first name of the guest, or «Ospite» when the guest did not agree to the name or the guest was
    /// erased. The only personal datum of the review the public sees.
    /// </summary>
    [Required, MaxLength(100)]
    public string AuthorName { get; set; } = string.Empty;

    /// <summary>The Europe/Rome day the stay ended (midnight UTC, the date-only convention): the public sees its month.</summary>
    public DateTime StayEndedOn { get; set; }

    public ReviewStatus Status { get; set; } = ReviewStatus.Published;

    /// <summary>When the guest sent it (UTC): the review is public from this instant.</summary>
    public DateTime PublishedAt { get; set; }

    /// <summary>The answer of the host, once and as written (up to <c>ReviewRules.ReplyMaxLength</c> characters); null until the host answers.</summary>
    [MaxLength(800)]
    public string? HostReply { get; set; }

    public DateTime? HostRepliedAt { get; set; }

    /// <summary>When the staff took it down after a report; null otherwise.</summary>
    public DateTime? RemovedAt { get; set; }

    /// <summary>Why the staff took it down (the reason written for the decision). Never shown to the public.</summary>
    [MaxLength(500)]
    public string? RemovedReason { get; set; }
}

/// <summary>State of a <see cref="Review"/>. Stored as an integer.</summary>
public enum ReviewStatus
{
    /// <summary>Public.</summary>
    Published = 0,

    /// <summary>Reported by the host and still public: it stays visible until the staff decides.</summary>
    Reported = 1,

    /// <summary>Taken down by the staff after a report: not public, not counted in the average.</summary>
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

    /// <summary>The org that reports: the org of the review.</summary>
    public Guid OrgId { get; set; }

    [ForeignKey(nameof(Review))]
    public Guid ReviewId { get; set; }
    public virtual Review Review { get; set; } = null!;

    /// <summary>The reason, from a closed list.</summary>
    public ReviewReportReason Reason { get; set; }

    /// <summary>What the host added (up to <c>ReviewRules.ReportNoteMaxLength</c> characters); seen by the staff only.</summary>
    [MaxLength(500)]
    public string? Note { get; set; }

    /// <summary>The user who reported (<c>User.Id</c>), an opaque id with no foreign key: the report outlives the account.</summary>
    [MaxLength(255)]
    public string? ReportedByUserId { get; set; }

    public DateTime ReportedAt { get; set; }

    public ReviewReportStatus Status { get; set; } = ReviewReportStatus.Open;

    public DateTime? DecidedAt { get; set; }

    /// <summary>The staff user who decided (<c>User.Id</c>).</summary>
    [MaxLength(255)]
    public string? DecidedByUserId { get; set; }

    /// <summary>The reason written for the decision, required (up to 500 characters).</summary>
    [MaxLength(500)]
    public string? DecisionReason { get; set; }
}

/// <summary>Why a host reports a review. Stored as an integer.</summary>
public enum ReviewReportReason
{
    /// <summary>Insults, threats or hate.</summary>
    Offensive = 0,

    /// <summary>Advertising, links or text that is not a review.</summary>
    Spam = 1,

    /// <summary>It is about something other than the stay (another place, a person, a dispute).</summary>
    NotAboutTheStay = 2,

    /// <summary>It names a person or gives a phone number, an address or another personal datum.</summary>
    PersonalData = 3,

    /// <summary>The guest never stayed, or it is not the stay of this booking.</summary>
    NotAGuest = 4,

    Other = 5,
}

/// <summary>State of a <see cref="ReviewReport"/>. Stored as an integer.</summary>
public enum ReviewReportStatus
{
    /// <summary>Waiting for the staff.</summary>
    Open = 0,

    /// <summary>The staff decided the review stays public.</summary>
    Kept = 1,

    /// <summary>The staff took the review down.</summary>
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

    /// <summary>When the host asked (UTC).</summary>
    public DateTime SentAt { get; set; }

    /// <summary>The user who asked (<c>User.Id</c>), an opaque id with no foreign key.</summary>
    [MaxLength(255)]
    public string? SentByUserId { get; set; }
}
