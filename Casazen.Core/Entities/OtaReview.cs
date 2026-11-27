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
    /// <summary>Name of the unique index on (<see cref="OrgId"/>, <see cref="BookingId"/>, <see cref="Channel"/>).</summary>
    public const string OrgBookingChannelIndexName = "UIX_OtaReviews_OrgId_BookingId_Channel";

    /// <summary>Name of the index that finds the reviews of a property by the time the guest's review was published.</summary>
    public const string PropertyPublishedIndexName = "IX_OtaReviews_OrgId_PropertyId_GuestReviewPublishedAt";

    /// <summary>Name of the index the sync reads: the rows whose next read is set.</summary>
    public const string NextPollIndexName = "IX_OtaReviews_NextPollAt";

    /// <summary>Longest <see cref="OctorateRefer"/>.</summary>
    public const int OctorateReferMaxLength = 64;

    /// <summary>Longest <see cref="HostReplyIdempotencyKey"/> and <see cref="HostReviewIdempotencyKey"/>: the same as the key of a message (<see cref="OtaMessage.IdempotencyKeyMaxLength"/>).</summary>
    public const int IdempotencyKeyMaxLength = 64;

    /// <summary>Longest <see cref="HostReplyError"/>, <see cref="HostReviewError"/> and <see cref="LastSyncError"/>: a stable code, never a message.</summary>
    public const int ErrorCodeMaxLength = 64;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row: the organization of the booking. Never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The property of the booking, the one the reach of a collaborator «Solo alcuni» is decided on. FK, RESTRICT (a property is soft-deleted).</summary>
    public Guid PropertyId { get; set; }

    public virtual Property Property { get; set; } = null!;

    /// <summary>The booking the reviews are about. FK, CASCADE: the reviews go with the stay.</summary>
    public Guid BookingId { get; set; }

    public virtual Booking Booking { get; set; } = null!;

    /// <summary>The channel the booking came from; only a channel that integrates reviews has a row (<c>OtaReviewCapabilities</c>).</summary>
    public OtaChannel Channel { get; set; }

    /// <summary>
    /// The reference of the booking the review API takes (<c>{refer}</c> of <c>GET /rest/v2/review/{accommodation}/{refer}</c>), as it was used for the last read: kept so a
    /// reply and a review of the host are sent for the same booking the guest's review was read for. The field name and the format are to be confirmed on the sandbox.
    /// </summary>
    [Required]
    [MaxLength(OctorateReferMaxLength)]
    public string OctorateRefer { get; set; } = string.Empty;

    // ─── The review of the guest ───

    /// <summary>The guest's review is published on the channel (<c>REVIEW_PUBLISHED</c>): only then it can be read and answered. <c>false</c> while the channel manager says nothing, or says it is not published.</summary>
    public bool GuestReviewPublished { get; set; }

    /// <summary>When the channel published the guest's review (UTC), when it says; <c>null</c> otherwise.</summary>
    public DateTime? GuestReviewPublishedAt { get; set; }

    /// <summary>The overall rating the guest gave, as the channel reports it (one decimal, 1.0 to 5.0); <c>null</c> when it reports none.</summary>
    [Precision(3, 1)]
    public decimal? GuestRating { get; set; }

    /// <summary>The rating categories the guest gave as the channel reports them (jsonb, a list of <c>{ "category": "…", "value": 5 }</c>); <c>null</c> when it reports none. No text of the guest.</summary>
    [Column(TypeName = "jsonb")]
    public string? GuestCategoryRatingsJson { get; set; }

    /// <summary>What the guest wrote, as the channel reports it. <b>Encrypted at rest</b>; <c>null</c> when the guest wrote nothing, the review is not published, or the guest was erased.</summary>
    public string? GuestReviewText { get; set; }

    // ─── The reply of the host ───

    /// <summary>The reply of the host, as written. <b>Encrypted at rest</b>; <c>null</c> until the host answers (and set again from the channel when it holds one CasaZen did not send).</summary>
    public string? HostReplyText { get; set; }

    /// <summary>The key the screen made for the reply: the same key twice is the same reply and sends nothing again (OCT-13). <c>null</c> while no reply was asked for.</summary>
    [MaxLength(IdempotencyKeyMaxLength)]
    public string? HostReplyIdempotencyKey { get; set; }

    public OtaReviewActionState HostReplyState { get; set; }

    /// <summary>When the reply was saved to be sent (UTC): the clock of the hour after which an outcome nobody confirmed becomes an error the host can act on. <c>null</c> while no reply was asked for.</summary>
    public DateTime? HostReplyRequestedAt { get; set; }

    /// <summary>When the channel manager took the reply (UTC), or when a read of the channel found it there.</summary>
    public DateTime? HostReplySentAt { get; set; }

    /// <summary>The code of the last failure of the reply (<see cref="OtaReviewActionState.Error"/>); <c>null</c> otherwise. A code, never a message.</summary>
    [MaxLength(ErrorCodeMaxLength)]
    public string? HostReplyError { get; set; }

    // ─── The review of the host ───

    public HostReviewWindow HostReviewWindow { get; set; }

    public OtaReviewActionState HostReviewState { get; set; }

    /// <summary>When the host's review was saved to be sent (UTC); <c>null</c> while none was asked for.</summary>
    public DateTime? HostReviewRequestedAt { get; set; }

    /// <summary>When the channel manager took the host's review (UTC), or when a read of the channel found it there.</summary>
    public DateTime? HostReviewSentAt { get; set; }

    /// <summary>What the host sent as its review: the ratings, the public text and the private text (JSON). <b>Encrypted at rest</b>; <c>null</c> until the host writes it.</summary>
    public string? HostReviewJson { get; set; }

    /// <summary>The key the screen made for the host's review (see <see cref="HostReplyIdempotencyKey"/>).</summary>
    [MaxLength(IdempotencyKeyMaxLength)]
    public string? HostReviewIdempotencyKey { get; set; }

    /// <summary>The code of the last failure of the host's review (<see cref="OtaReviewActionState.Error"/>); <c>null</c> otherwise.</summary>
    [MaxLength(ErrorCodeMaxLength)]
    public string? HostReviewError { get; set; }

    // ─── The read of the channel manager ───

    /// <summary>When the booking was last read from the channel manager (UTC); <c>null</c> until the first read.</summary>
    public DateTime? LastSyncedAt { get; set; }

    /// <summary>The code of the last failure of the read (<c>review_not_available</c>, <c>octorate_unavailable</c>, …); <c>null</c> after a read that worked. A code, never a text.</summary>
    [MaxLength(ErrorCodeMaxLength)]
    public string? LastSyncError { get; set; }

    /// <summary>When the sync reads this booking next (UTC); <c>null</c> when it is done with it (the guest's review is published and the host's review is sent or closed).</summary>
    public DateTime? NextPollAt { get; set; }

    /// <summary>How many times the booking was read: the number of the polls that backs off after a failure.</summary>
    public int PollCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optimistic concurrency token: mapped to PostgreSQL's <c>xmin</c> system column, which changes with every update of the row (no column of its own). Two writers that
    /// read the same row cannot both save it: the second one fails and reads the truth, so a reply goes out once (AC-NF69-2).
    /// </summary>
    public uint Version { get; set; }
}
