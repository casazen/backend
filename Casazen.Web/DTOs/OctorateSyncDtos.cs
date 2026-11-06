using Casazen.Core.Entities.Enums;

namespace Casazen.Web.DTOs;

/// <summary>
/// The answer of <c>POST /api/bookings/{id}/octorate-sync/retry</c> (202, E08/F01/US01/T03): the push of the booking to the channel manager was
/// asked again and is <c>Pending</c> until the event bus runs it; the state is then read with the booking.
/// </summary>
/// <param name="BookingId">The booking.</param>
/// <param name="State">The state of the mirror after the request: <c>Pending</c>.</param>
public sealed record OctorateSyncRetryResponse(Guid BookingId, OctorateSyncState State);
