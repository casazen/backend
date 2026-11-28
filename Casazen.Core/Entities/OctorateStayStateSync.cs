using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// A state of a stay CasaZen sends to Octorate (NF-45). Stored as an integer, append only. The three states are sent through the check-in sync
/// with a row each (E20/F01/US01/T01): <see cref="NoShow"/> queued by the check-in endpoints, the arrival and the departure queued once by the
/// booking-state push of E02 (so they are not sent twice for one event).
/// </summary>
public enum OctorateStayState
{
    CheckInEnd = 1,

    CheckOutEnd = 2,

    NoShow = 3,
}

/// <summary>
/// One state of a stay sent to Octorate (E03/F01/US01/T03, NF-45, AC-NF45-1): each state is sent once, retried with a backoff when it fails,
/// and after the arrival the arrival Octorate registered is read back. After the last failed attempt one "To do" is raised
/// (<c>octorate_stay_state_failed</c>), closed by a later success. The arrival waits, <c>Pending</c> with no attempt used, while the write of the
/// stay's guests is still to complete. No personal data. One row per booking and state.
/// </summary>
[Table("OctorateStayStateSyncs")]
public class OctorateStayStateSync : ITenantOwned
{
    public const int ErrorCodeMaxLength = 64;

    public const int ArrivalTimeMaxLength = 10;

    public const string StateIndexName = "UIX_OctorateStayStateSyncs_BookingId_State";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid BookingId { get; set; }

    public OctorateStayState State { get; set; }

    public OctorateSyncStatus Status { get; set; } = OctorateSyncStatus.Pending;

    public int Attempts { get; set; }

    public DateTime? LastAttemptAt { get; set; }

    public DateTime? SentAt { get; set; }

    [MaxLength(ErrorCodeMaxLength)]
    public string? ErrorCode { get; set; }

    public DateTime? OctorateEffectiveCheckin { get; set; }

    [MaxLength(ArrivalTimeMaxLength)]
    public string? OctorateArrivalTime { get; set; }

    public DateTime? ReadAt { get; set; }

    public Guid? TodoActivityId { get; set; }
}
