namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The three values of the <c>status</c> of an Octorate reservation (<c>WAITING</c>, <c>CONFIRMED</c>, <c>CANCELLED</c>,
/// E02/F02/US01/T01, ADR-031, ADR-033). Check-in, check-out and no-show are not reservation statuses: CasaZen sends them with the check-in
/// status operation and reads <c>effectiveCheckin</c> and <c>arrivalTime</c>. Not stored: <see cref="BookingStatus"/> is what CasaZen
/// keeps (<c>BookingStateMapper</c> turns one into the other).
/// </summary>
public enum OctorateBookingStatus
{
    Waiting = 1,

    Confirmed = 2,

    Cancelled = 3,
}
