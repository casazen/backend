using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The mirror of one CasaZen booking on Octorate (E08/F01/US01/T01, ADR-031, NF-74): the state of the last operation CasaZen sent for it,
/// so the host sees whether the reservation exists in the channel manager and can ask again when it does not. One row per booking (unique
/// index <see cref="BookingIndexName"/>), deleted with its booking. A booking whose property is not mirrored has no row: the absence of the
/// row means "not mirrored".
/// </summary>
/// <remarks>
/// <para>No personal data: ids, a state, counters, instants and a stable error code. <see cref="ErrorCode"/> is one of CasaZen's codes
/// (<c>OctorateSyncErrorCodes</c>), never a text of Octorate. <see cref="OctorateVersionJson"/> keeps only the dates and the number of guests
/// Octorate holds after a 409, never a name.</para>
/// <para>The reservation id itself stays on <see cref="Booking.OctorateReservationId"/> (E02/F02/US01/T01), the key of the import.</para>
/// </remarks>
[Table("OctorateBookingSyncs")]
public class OctorateBookingSync : ITenantOwned
{
    public const string BookingIndexName = "UIX_OctorateBookingSyncs_BookingId";

    public const int ErrorCodeMaxLength = 64;

    /// <summary>
    /// Whether the host can ask for the push again from a row in <paramref name="state"/>: a push that failed or was refused. The one definition of
    /// <c>POST /api/bookings/{id}/octorate-sync/retry</c> and of the <c>canRetry</c> of the booking answer.
    /// </summary>
    public static bool IsRetryable(OctorateSyncState state) => state is OctorateSyncState.Failed or OctorateSyncState.Conflict;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid BookingId { get; set; }

    public Guid PropertyId { get; set; }

    public OctorateSyncState State { get; set; } = OctorateSyncState.Pending;

    public OctorateSyncOperation LastOperation { get; set; } = OctorateSyncOperation.Create;

    /// <summary>How many times an operation was sent for the booking (every attempt counts, retries included).</summary>
    public int Attempts { get; set; }

    public DateTime? LastAttemptAt { get; set; }

    public DateTime? LastSyncedAt { get; set; }

    /// <summary>Why the last operation failed: a stable code of CasaZen (<c>octorate_dates_unavailable</c>, ...); <c>null</c> otherwise.</summary>
    [MaxLength(ErrorCodeMaxLength)]
    public string? ErrorCode { get; set; }

    public DateTime? OctorateUpdateTime { get; set; }

    /// <summary>
    /// The dates and the number of guests of the reservation as Octorate holds them, read again after a 409 (jsonb), so the host can see
    /// what differs before deciding; <c>null</c> otherwise. Never a name or a contact. The form is that of <see cref="Services.OctorateVersionSnapshot"/>
    /// (<c>{"checkIn":"yyyy-MM-dd","checkOut":"yyyy-MM-dd","guests":n}</c>): whoever writes it uses <see cref="Services.OctorateVersionSnapshot.ToJson"/>, and the
    /// booking answer (<c>octorateSync.octorateVersion</c>, E08/F03/US03/T02) reads it with <see cref="Services.OctorateVersionSnapshot.Parse"/>, only in the
    /// <see cref="OctorateSyncState.Conflict"/> state.
    /// </summary>
    public string? OctorateVersionJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
