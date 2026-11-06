using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>A state of a stay CasaZen sends to Octorate (NF-45). Stored as an integer, append only.</summary>
public enum OctorateStayState
{
    /// <summary><c>CHECKIN_END</c>: the guest arrived.</summary>
    CheckInEnd = 1,

    /// <summary><c>CHECKOUT_END</c>: the guest left.</summary>
    CheckOutEnd = 2,

    /// <summary><c>NO_SHOW</c>: the guest never came.</summary>
    NoShow = 3,
}

/// <summary>
/// One state of a stay sent to Octorate (E03/F01/US01/T03, NF-45, AC-NF45-1): each state is sent once, retried with a backoff when it fails,
/// and after the arrival the arrival Octorate registered is read back. No personal data. One row per booking and state.
/// </summary>
[Table("OctorateStayStateSyncs")]
public class OctorateStayStateSync : ITenantOwned
{
    /// <summary>Length of <see cref="ErrorCode"/>.</summary>
    public const int ErrorCodeMaxLength = 64;

    /// <summary>Length of <see cref="OctorateArrivalTime"/>.</summary>
    public const int ArrivalTimeMaxLength = 10;

    /// <summary>Name of the unique index on (<see cref="BookingId"/>, <see cref="State"/>).</summary>
    public const string StateIndexName = "UIX_OctorateStayStateSyncs_BookingId_State";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key, the org of the booking.</summary>
    public Guid OrgId { get; set; }

    public Guid BookingId { get; set; }

    public OctorateStayState State { get; set; }

    public OctorateSyncStatus Status { get; set; } = OctorateSyncStatus.Pending;

    /// <summary>Attempts made, the first included.</summary>
    public int Attempts { get; set; }

    /// <summary>When the last attempt was made (UTC).</summary>
    public DateTime? LastAttemptAt { get; set; }

    /// <summary>When Octorate accepted the state (UTC).</summary>
    public DateTime? SentAt { get; set; }

    /// <summary>Why the last attempt failed; null after a success.</summary>
    [MaxLength(ErrorCodeMaxLength)]
    public string? ErrorCode { get; set; }

    /// <summary>The arrival Octorate registered (<c>effectiveCheckin</c>, UTC), read back after <see cref="OctorateStayState.CheckInEnd"/>.</summary>
    public DateTime? OctorateEffectiveCheckin { get; set; }

    /// <summary>The expected arrival time of the reservation on Octorate (<c>arrivalTime</c>).</summary>
    [MaxLength(ArrivalTimeMaxLength)]
    public string? OctorateArrivalTime { get; set; }

    /// <summary>When the arrival was read back (UTC).</summary>
    public DateTime? ReadAt { get; set; }

    /// <summary>The "To do" raised after the last failed attempt, closed when a later attempt succeeds.</summary>
    public Guid? TodoActivityId { get; set; }
}
