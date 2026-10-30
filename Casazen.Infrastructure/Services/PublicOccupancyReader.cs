using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The nights that are taken on the public booking site, for one property or many in two queries: the one definition shared by the
/// availability of a property (<see cref="PublicAvailabilityService"/>), the calendar (<see cref="PublicCalendarService"/>) and the
/// per-site reads (DB-07a). A night is taken by a booking that is not cancelled and not an expired hold
/// (<see cref="CheckoutHolds.OccupiesDates"/>), or by an iCal or manual block (<see cref="PropertyOccupancy"/>), the same rule the
/// checks that refuse a booking use. Dates only: never the guest, the source or whether it is a booking or a block.
/// </summary>
/// <remarks>
/// Anonymous reads: the tenant filter is off for them, but a signed-in host of another org who calls a public endpoint would get it
/// back on and see every night free, so both queries name the properties and ignore the tenant filter (as the availability always did).
/// </remarks>
public sealed class PublicOccupancyReader(AppDbContext db, IConfiguration configuration, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// For each of <paramref name="propertyIds"/>, the nights taken from <paramref name="fromDate"/> (included) to
    /// <paramref name="toDate"/> (excluded), as midnight-UTC dates in order and without duplicates. A property with no taken night is
    /// in the answer with an empty set.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, SortedSet<DateTime>>> TakenNightsAsync(
        IReadOnlyCollection<Guid> propertyIds,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(propertyIds);

        var from = fromDate.Date;
        var to = toDate.Date;
        var result = propertyIds.Distinct().ToDictionary(id => id, _ => new SortedSet<DateTime>());
        if (result.Count == 0 || to <= from)
            return result;

        var ids = result.Keys.ToList();
        var expiredHoldCutoff = CheckoutHolds.CutoffAt(
            _clock.GetUtcNow().UtcDateTime, CheckoutHolds.GetTtlMinutes(configuration));

        // Dates only, never the guest or the source of a stay.
        var stays = await db.Bookings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(b => ids.Contains(b.PropertyId))
            .Where(PropertyOccupancy.BookingTakesNightIn(from, to))
            .Where(CheckoutHolds.OccupiesDates(expiredHoldCutoff))
            .Select(b => new { b.PropertyId, Start = b.CheckInDate, End = b.CheckOutDate })
            .ToListAsync(cancellationToken);

        var blocks = await db.CalendarBlocks
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(b => ids.Contains(b.PropertyId))
            .Where(PropertyOccupancy.BlockTakesNightIn(from, to))
            .Select(b => new { b.PropertyId, Start = b.StartUtc, End = b.EndUtc })
            .ToListAsync(cancellationToken);

        foreach (var stay in stays)
            result[stay.PropertyId].UnionWith(PropertyOccupancy.NightsIn(stay.Start, stay.End, from, to));
        foreach (var block in blocks)
            result[block.PropertyId].UnionWith(PropertyOccupancy.NightsIn(block.Start, block.End, from, to));

        return result;
    }
}
