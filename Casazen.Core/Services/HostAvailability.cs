using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Whether a stay of the host's property is free, read the way the host needs it before entering a booking by hand (SR-06a,
/// <c>GET /api/properties/{id}/availability</c>). It adds no rule of occupancy: the bookings that take their dates are
/// <see cref="CheckoutHolds.OccupiesDates"/> (an expired checkout hold does not, a "pay at the property" request does) and the
/// nights they and the calendar blocks take are <see cref="PropertyOccupancy"/>, the same as the calendar, the iCal export, the public
/// availability and the final check that refuses a booking on taken dates. Read only: nothing is cancelled or written.
/// </summary>
public interface IHostAvailabilityService
{
    /// <summary>
    /// The state of the stay <paramref name="from"/> (arrival day) to <paramref name="to"/> (departure day, not a night) of
    /// <paramref name="property"/>, which the caller has already been authorized to book. <paramref name="channel"/> narrows the block
    /// that <see cref="HostAvailabilityState.LinkableBlock"/> offers to the calendar of that portal.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="PropertyRentalModeErrorCodes.NotBookableInLongMode"/>: the property is in long-term mode (PM-01).
    /// </exception>
    Task<HostStayAvailability> CheckAsync(
        Property property,
        DateOnly from,
        DateOnly to,
        BookingChannel? channel = null,
        CancellationToken cancellationToken = default);
}

public enum HostAvailabilityState
{
    /// <summary>The nights are free and the stay is not shorter than the minimum.</summary>
    Ok,

    /// <summary>The nights are free but the stay is shorter than the minimum stay of the property: the host may go on, it is their booking.</summary>
    BelowMinimum,

    /// <summary>At least one night is taken: see the conflicts and the nearest free window.</summary>
    Occupied,

    /// <summary>
    /// The only thing on those exact dates is a reservation a portal sent as a block of its calendar, not yet turned into a stay:
    /// the host completes it with the guest's name instead of creating a second booking.
    /// </summary>
    LinkableBlock,

    /// <summary>The dates cannot be a stay: see the reason.</summary>
    Invalid,
}

public enum HostAvailabilityInvalidReason
{
    /// <summary>The departure is not after the arrival.</summary>
    CheckOutNotAfterCheckIn,

    /// <summary>The arrival is before today (Europe/Rome): the host enters stays from today on.</summary>
    CheckInInPast,

    /// <summary>The stay is longer than <see cref="TouristTax.TouristTaxCalculator.MaxNights"/> nights.</summary>
    StayTooLong,
}

public enum HostAvailabilityConflictType
{
    /// <summary>A stay of CasaZen (the booking site, entered by the host, or a portal's reservation the host turned into a stay).</summary>
    Booking,

    /// <summary>A reservation of a portal as it arrives, a block of its iCal calendar.</summary>
    Ical,

    /// <summary>The host's own block.</summary>
    Block,
}

/// <summary>Something that takes at least one night of the dates asked for.</summary>
/// <param name="Title">The guest's name for a booking (the host sees it), the name of the calendar for a portal, the host's note for a block.</param>
/// <param name="Channel">Where a booking came from, or the portal of a block of its calendar; null for the host's own block.</param>
/// <param name="From">Arrival day.</param>
/// <param name="To">Departure day, the first day it no longer takes a night.</param>
public sealed record HostAvailabilityConflict(
    Guid Id,
    HostAvailabilityConflictType Type,
    string? Title,
    BookingChannel? Channel,
    DateOnly From,
    DateOnly To);

/// <summary>A free stay of the same number of nights as the one asked for.</summary>
public sealed record HostAvailabilityWindow(DateOnly From, DateOnly To);

/// <summary>The state of a stay and what the host needs to act on it.</summary>
/// <param name="Nights">The nights asked for; 0 when the departure is not after the arrival.</param>
/// <param name="MinNights">The minimum stay of the property, when it has one.</param>
/// <param name="Conflicts">What takes the nights, by arrival day; empty unless <see cref="HostAvailabilityState.Occupied"/> or <see cref="HostAvailabilityState.LinkableBlock"/> (then, the block).</param>
/// <param name="NearestFreeWindow">With <see cref="HostAvailabilityState.Occupied"/>: the free stay of the same length nearest to the arrival asked, never before today; null when there is none close by.</param>
/// <param name="LinkableBlockId">With <see cref="HostAvailabilityState.LinkableBlock"/>: the block to complete.</param>
public sealed record HostStayAvailability(
    HostAvailabilityState State,
    HostAvailabilityInvalidReason? InvalidReason,
    int Nights,
    int? MinNights,
    IReadOnlyList<HostAvailabilityConflict> Conflicts,
    HostAvailabilityWindow? NearestFreeWindow,
    Guid? LinkableBlockId);

/// <summary>The part of the availability that needs no database (SR-06a).</summary>
public static class HostAvailabilityRules
{
    /// <summary>How many days before and after the arrival asked the nearest free window is looked for.</summary>
    public const int NearestWindowDays = 40;

    /// <summary>
    /// The free stay of <paramref name="nights"/> nights nearest to <paramref name="from"/>: first that same arrival, then one day
    /// before and one day after, two days before and two after, and so on up to <see cref="NearestWindowDays"/> days; the earlier of two
    /// equally near wins. A stay that would arrive before <paramref name="today"/> is never offered. <paramref name="taken"/> are the
    /// nights taken, each from its arrival day to its departure day (the departure day stays free for a new arrival).
    /// </summary>
    public static HostAvailabilityWindow? NearestFreeWindow(
        IReadOnlyCollection<(DateOnly From, DateOnly To)> taken,
        DateOnly from,
        int nights,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(taken);
        if (nights < 1)
            return null;

        for (var distance = 0; distance <= NearestWindowDays; distance++)
        {
            foreach (var offset in distance == 0 ? new[] { 0 } : new[] { -distance, distance })
            {
                var start = from.AddDays(offset);
                if (start < today)
                    continue;

                var end = start.AddDays(nights);
                if (!taken.Any(night => night.From < end && start < night.To))
                    return new HostAvailabilityWindow(start, end);
            }
        }

        return null;
    }
}
