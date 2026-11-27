using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

public enum OctorateSyncStatus
{
    Pending = 1,

    Sent = 2,

    /// <summary>The last attempt failed (<c>ErrorCode</c> says why): the retry job tries again with a backoff.</summary>
    Failed = 3,
}

/// <summary>
/// The guest of a stay as sent to Octorate (E03/F01/US01/T03, NF-41): the Octorate id of the guest at a position of the booking, the hash of
/// what was sent and where the write stands. Keyed by position, because <c>StayGuestService.ReplaceAsync</c> keeps the row of a position. No
/// personal data: no name, no document, only ids, a hash and codes. The absence of a row means the guest was never mirrored.
/// </summary>
[Table("StayGuestOctorateLinks")]
public class StayGuestOctorateLink : ITenantOwned
{
    public const int OctorateGuestIdMaxLength = 100;

    public const int PayloadHashLength = 64;

    public const int ErrorCodeMaxLength = 64;

    public const int ErrorFieldsMaxLength = 100;

    public const string PositionIndexName = "UIX_StayGuestOctorateLinks_BookingId_Position";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid BookingId { get; set; }

    public int Position { get; set; }

    [MaxLength(OctorateGuestIdMaxLength)]
    public string? OctorateGuestId { get; set; }

    [Required, MaxLength(PayloadHashLength)]
    public string PayloadHash { get; set; } = string.Empty;

    public bool SentAsDraft { get; set; }

    public OctorateSyncStatus Status { get; set; } = OctorateSyncStatus.Pending;

    public int Attempts { get; set; }

    [MaxLength(ErrorCodeMaxLength)]
    public string? ErrorCode { get; set; }

    /// <summary>
    /// The fields of the guest that caused <see cref="ErrorCode"/> when it is <c>place_not_recognized</c>: the camelCase names of the Alloggiati record
    /// (<c>birthComune</c>, <c>residenceComune</c>, <c>documentIssuePlace</c>), comma separated, so the host knows which place to correct. Never a
    /// value of the guest. <c>null</c> for any other error and after a success.
    /// </summary>
    [MaxLength(ErrorFieldsMaxLength)]
    public string? ErrorFields { get; set; }

    public DateTime? SentAt { get; set; }

    public Guid? TodoActivityId { get; set; }

    /// <summary>When the row last changed (UTC): the time of the last attempt for the backoff of the retry.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
