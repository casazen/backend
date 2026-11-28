using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// What the channel manager says about the reviews of one booking of a channel (E05/F06/US01/T01, ADR-032, NF-69, OCT-12): the review the guest wrote, the host's
/// reply to it and the host's own review of the guest, with the state of each and the state of the read itself (when the booking is read next). One row for each
/// organization, booking and channel (<see cref="OrgBookingChannelIndexName"/>). The reviews of the booking site stay in <see cref="Review"/> and are not touched.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Only what the channel manager reports.</b> The Octorate API has no list per property: a booking is read one at a time, and only the channels it integrates
/// for reviews (Airbnb) ever get a row. A row is never invented for another channel (OCT-12, S6).</item>
/// <item><b>Personal data, encrypted.</b> <see cref="GuestReviewText"/>, <see cref="HostReplyText"/> and <see cref="HostReviewJson"/> are free text a guest or a host
/// wrote and can say anything: they are encrypted at rest (<c>EncryptedColumns</c>, purpose <c>Casazen.Review.Content</c>) and the database only holds the Data
/// Protection payload; code reads and writes the clear text through EF. They are never logged and never put in an event. Erasing a guest empties
/// <see cref="GuestReviewText"/> (the host's own texts stay).</item>
/// <item><b>Once.</b> A reply or a review of the host is asked of the channel manager at most once for a key (<see cref="HostReplyIdempotencyKey"/>,
/// <see cref="HostReviewIdempotencyKey"/>), and <see cref="Version"/> (<c>xmin</c>) refuses a second writer of the same row, so no public reply goes out twice
/// (AC-NF69-2, OCT-13).</item>
/// </list>
/// </remarks>
[Table("OtaReviews")]
public class OtaReview : ITenantOwned
{
    public const string OrgBookingChannelIndexName = "UIX_OtaReviews_OrgId_BookingId_Channel";

    public const string PropertyPublishedIndexName = "IX_OtaReviews_OrgId_PropertyId_GuestReviewPublishedAt";

    public const string NextPollIndexName = "IX_OtaReviews_NextPollAt";

    public const int OctorateReferMaxLength = 64;

    /// <summary>Longest <see cref="HostReplyIdempotencyKey"/> and <see cref="HostReviewIdempotencyKey"/>: the same as the key of a message (<see cref="OtaMessage.IdempotencyKeyMaxLength"/>).</summary>
    public const int IdempotencyKeyMaxLength = 64;

    public const int ErrorCodeMaxLength = 64;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    public virtual Property Property { get; set; } = null!;

    public Guid BookingId { get; set; }

    public virtual Booking Booking { get; set; } = null!;

    public OtaChannel Channel { get; set; }

    [Required]
    [MaxLength(OctorateReferMaxLength)]
    public string OctorateRefer { get; set; } = string.Empty;

    public bool GuestReviewPublished { get; set; }

    /// <summary>When the channel published the guest's review (UTC), when it says; <c>null</c> otherwise.</summary>
    public DateTime? GuestReviewPublishedAt { get; set; }

    [Precision(3, 1)]
    public decimal? GuestRating { get; set; }

    [Column(TypeName = "jsonb")]
    public string? GuestCategoryRatingsJson { get; set; }

    public string? GuestReviewText { get; set; }

    public string? HostReplyText { get; set; }

    [MaxLength(IdempotencyKeyMaxLength)]
    public string? HostReplyIdempotencyKey { get; set; }

    public OtaReviewActionState HostReplyState { get; set; }

    public DateTime? HostReplyRequestedAt { get; set; }

    public DateTime? HostReplySentAt { get; set; }

    /// <summary>The code of the last failure of the reply (<see cref="OtaReviewActionState.Error"/>); <c>null</c> otherwise. A code, never a message.</summary>
    [MaxLength(ErrorCodeMaxLength)]
    public string? HostReplyError { get; set; }

    public HostReviewWindow HostReviewWindow { get; set; }

    public OtaReviewActionState HostReviewState { get; set; }

    public DateTime? HostReviewRequestedAt { get; set; }

    public DateTime? HostReviewSentAt { get; set; }

    public string? HostReviewJson { get; set; }

    [MaxLength(IdempotencyKeyMaxLength)]
    public string? HostReviewIdempotencyKey { get; set; }

    /// <summary>The code of the last failure of the host's review (<see cref="OtaReviewActionState.Error"/>); <c>null</c> otherwise.</summary>
    [MaxLength(ErrorCodeMaxLength)]
    public string? HostReviewError { get; set; }

    public DateTime? LastSyncedAt { get; set; }

    [MaxLength(ErrorCodeMaxLength)]
    public string? LastSyncError { get; set; }

    public DateTime? NextPollAt { get; set; }

    public int PollCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optimistic concurrency token: mapped to PostgreSQL's <c>xmin</c> system column, which changes with every update of the row (no column of its own). Two writers that
    /// read the same row cannot both save it: the second one fails and reads the truth, so a reply goes out once (AC-NF69-2).
    /// </summary>
    public uint Version { get; set; }
}
