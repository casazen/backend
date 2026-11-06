using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>Where a write to Octorate stands (a check-in guest or a state of a stay). Stored as an integer, append only.</summary>
public enum OctorateSyncStatus
{
    /// <summary>Not sent yet, or to send again.</summary>
    Pending = 1,

    /// <summary>Octorate accepted it.</summary>
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
    /// <summary>Length of <see cref="OctorateGuestId"/>.</summary>
    public const int OctorateGuestIdMaxLength = 100;

    /// <summary>Length of <see cref="PayloadHash"/> (hex SHA-256).</summary>
    public const int PayloadHashLength = 64;

    /// <summary>Length of <see cref="ErrorCode"/>.</summary>
    public const int ErrorCodeMaxLength = 64;

    /// <summary>Name of the unique index on (<see cref="BookingId"/>, <see cref="Position"/>).</summary>
    public const string PositionIndexName = "UIX_StayGuestOctorateLinks_BookingId_Position";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key, the org of the booking.</summary>
    public Guid OrgId { get; set; }

    public Guid BookingId { get; set; }

    /// <summary>The position of the guest in the stay (<see cref="StayGuest.Position"/>).</summary>
    public int Position { get; set; }

    /// <summary>The id Octorate gave the guest; null until it is created.</summary>
    [MaxLength(OctorateGuestIdMaxLength)]
    public string? OctorateGuestId { get; set; }

    /// <summary>SHA-256 (hex) of the canonical payload last sent (or tried), <c>draft</c> included: a different hash means a change to send.</summary>
    [Required, MaxLength(PayloadHashLength)]
    public string PayloadHash { get; set; } = string.Empty;

    /// <summary>The guest was sent with <c>draft = true</c> (the sender of the property is not the channel manager).</summary>
    public bool SentAsDraft { get; set; }

    public OctorateSyncStatus Status { get; set; } = OctorateSyncStatus.Pending;

    /// <summary>Attempts of the current payload, the first included.</summary>
    public int Attempts { get; set; }

    /// <summary>Why the last attempt failed (<c>octorate_auth</c>, <c>octorate_guest_rejected</c>, ...); null after a success.</summary>
    [MaxLength(ErrorCodeMaxLength)]
    public string? ErrorCode { get; set; }

    /// <summary>When Octorate last accepted the guest (UTC).</summary>
    public DateTime? SentAt { get; set; }

    /// <summary>The "To do" raised after the last failed attempt, closed when a later send succeeds.</summary>
    public Guid? TodoActivityId { get; set; }

    /// <summary>When the row last changed (UTC): the time of the last attempt for the backoff of the retry.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
