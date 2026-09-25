using System.Linq.Expressions;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IHostDashboardService"/>
/// <remarks>
/// Occupancy is computed on the active properties of the scope (the ones the host lists); the stays (revenue,
/// arrivals, departures, upcoming check-ins, recent bookings) on every property of the scope, since a property paused
/// meanwhile still has its guests and its money. Queries are bounded by the period or by <see cref="ListSize"/>: the
/// bookings of the org are never loaded whole (A2-29).
/// </remarks>
public sealed class HostDashboardService(
    AppDbContext db,
    IConfiguration configuration,
    TimeProvider? timeProvider = null) : IHostDashboardService
{
    /// <summary>Items listed for upcoming check-ins and recent bookings (the count covers all of them).</summary>
    public const int ListSize = 5;

    /// <summary>Items listed for today's arrivals and departures (the count covers all of them).</summary>
    public const int TodayListSize = 10;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<HostDashboardKpis> GetKpisAsync(
        HostScope scope,
        HostDashboardPeriodKind kind,
        DateOnly? month,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var now = _clock.GetUtcNow();
        var today = RomeCalendar.TodayAt(now);
        var period = kind == HostDashboardPeriodKind.Last30Days
            ? HostDashboardPeriod.ForLast30Days(today)
            : HostDashboardPeriod.ForMonth(month ?? DateOnly.FromDateTime(today));

        var propertyIds = await ActivePropertiesInScope(scope)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        var occupancy = await GetOccupancyAsync(propertyIds, period, now.UtcDateTime, cancellationToken);
        var (revenue, revenueStays) = await GetRevenueAsync(scope, period, cancellationToken);

        var stays = BookingsInScope(scope);
        var arrivals = await ListAsync(
            stays.Where(StayKpiRules.ArrivesOn(today)).OrderBy(b => b.Property.Name).ThenBy(b => b.Id),
            TodayListSize, cancellationToken);
        var departures = await ListAsync(
            stays.Where(StayKpiRules.DepartsOn(today)).OrderBy(b => b.Property.Name).ThenBy(b => b.Id),
            TodayListSize, cancellationToken);
        var upcoming = await ListAsync(
            stays.Where(StayKpiRules.UpcomingCheckIn(today)).OrderBy(b => b.CheckInDate).ThenBy(b => b.Id),
            ListSize, cancellationToken);
        var recent = await stays
            .OrderByDescending(b => b.CreatedAt)
            .ThenBy(b => b.Id)
            .Take(ListSize)
            .Select(StayProjection)
            .ToListAsync(cancellationToken);

        return new HostDashboardKpis(
            period,
            today,
            propertyIds.Count,
            occupancy,
            revenue,
            revenueStays,
            arrivals,
            departures,
            upcoming,
            recent.Select(ToRomeDates).ToList());
    }

    public async Task<IReadOnlyList<HostDashboardIcalFeed>> GetIcalFeedsAsync(
        HostScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var feeds = db.PropertyICalFeeds.AsNoTracking().Where(f => f.OrgId == scope.OrgId);
        if (scope.OwnerId is { } ownerId)
            feeds = feeds.Where(f => f.Property.OwnerId == ownerId);

        // Never the import URL (encrypted, A2-20): the projection does not read it. Feeds with a sync error first.
        return await feeds
            .OrderBy(f => f.LastImportStatus == PropertyICalImportStatus.Failure
                          || f.LastImportStatus == PropertyICalImportStatus.PartialFailure ? 0 : 1)
            .ThenBy(f => f.Property.Name)
            .ThenBy(f => f.CreatedAt)
            .ThenBy(f => f.Id)
            .Select(f => new HostDashboardIcalFeed(
                f.Id,
                f.PropertyId,
                f.Property.Name,
                f.Channel,
                f.Label,
                f.LastImportAt,
                f.LastImportStatus,
                f.LastError))
            .ToListAsync(cancellationToken);
    }

    private async Task<HostDashboardOccupancy> GetOccupancyAsync(
        IReadOnlyCollection<Guid> propertyIds,
        HostDashboardPeriod period,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (propertyIds.Count == 0)
            return new HostDashboardOccupancy(0, 0, 0);

        // The nights the booking site shows as taken (BK-05): same expressions and hold TTL as the public availability.
        var expiredHoldCutoff = CheckoutHolds.CutoffAt(nowUtc, CheckoutHolds.GetTtlMinutes(configuration));
        var stays = await db.Bookings
            .AsNoTracking()
            .Where(b => propertyIds.Contains(b.PropertyId))
            .Where(PropertyOccupancy.BookingTakesNightIn(period.From, period.To))
            .Where(CheckoutHolds.OccupiesDates(expiredHoldCutoff))
            .Select(b => new { b.PropertyId, Start = b.CheckInDate, End = b.CheckOutDate })
            .ToListAsync(cancellationToken);

        var blocks = await db.CalendarBlocks
            .AsNoTracking()
            .Where(b => propertyIds.Contains(b.PropertyId))
            .Where(PropertyOccupancy.BlockTakesNightIn(period.From, period.To))
            .Select(b => new { b.PropertyId, b.Source, Start = b.StartUtc, End = b.EndUtc })
            .ToListAsync(cancellationToken);

        var occupied = 0;
        var closed = 0;
        foreach (var propertyId in propertyIds)
        {
            var taken = new HashSet<DateTime>();
            foreach (var stay in stays.Where(s => s.PropertyId == propertyId))
                taken.UnionWith(PropertyOccupancy.NightsIn(stay.Start, stay.End, period.From, period.To));
            foreach (var block in blocks.Where(b => b.PropertyId == propertyId && b.Source != CalendarBlockSource.Manual))
                taken.UnionWith(PropertyOccupancy.NightsIn(block.Start, block.End, period.From, period.To));

            // Closed by the host (owner stay, maintenance): out of the availability, unless a stay takes the night anyway.
            var closedOnly = new HashSet<DateTime>();
            foreach (var block in blocks.Where(b => b.PropertyId == propertyId && b.Source == CalendarBlockSource.Manual))
                closedOnly.UnionWith(PropertyOccupancy.NightsIn(block.Start, block.End, period.From, period.To));
            closedOnly.ExceptWith(taken);

            occupied += taken.Count;
            closed += closedOnly.Count;
        }

        return new HostDashboardOccupancy(occupied, propertyIds.Count * period.Nights - closed, closed);
    }

    private async Task<(decimal Revenue, int Stays)> GetRevenueAsync(
        HostScope scope,
        HostDashboardPeriod period,
        CancellationToken cancellationToken)
    {
        var stays = await BookingsInScope(scope)
            .Where(StayKpiRules.IsConfirmedStay())
            .Where(PropertyOccupancy.BookingTakesNightIn(period.From, period.To))
            .Select(b => new { b.BasePrice, b.CheckInDate, b.CheckOutDate })
            .ToListAsync(cancellationToken);

        var counted = stays
            .Where(s => PropertyOccupancy.NightsIn(s.CheckInDate, s.CheckOutDate, period.From, period.To).Any())
            .ToList();
        var revenue = counted.Sum(s =>
            StayKpiRules.RevenueInPeriod(s.BasePrice, s.CheckInDate, s.CheckOutDate, period.From, period.To));
        return (Math.Round(revenue, 2, MidpointRounding.AwayFromZero), counted.Count);
    }

    // Active properties of the scope, as GET /api/properties lists them.
    private IQueryable<Property> ActivePropertiesInScope(HostScope scope)
    {
        var properties = db.Properties.AsNoTracking().Where(p => p.OrgId == scope.OrgId && p.IsActive);
        return scope.OwnerId is { } ownerId ? properties.Where(p => p.OwnerId == ownerId) : properties;
    }

    // Bookings of the scope (TN-3): the org and, unless org-wide, the properties the caller owns.
    private IQueryable<Booking> BookingsInScope(HostScope scope)
    {
        var bookings = db.Bookings.AsNoTracking().Where(b => b.OrgId == scope.OrgId);
        return scope.OwnerId is { } ownerId ? bookings.Where(b => b.Property.OwnerId == ownerId) : bookings;
    }

    private static async Task<HostDashboardStayList> ListAsync(
        IQueryable<Booking> query,
        int take,
        CancellationToken cancellationToken)
    {
        var count = await query.CountAsync(cancellationToken);
        if (count == 0)
            return new HostDashboardStayList(0, []);

        var items = await query.Take(take).Select(StayProjection).ToListAsync(cancellationToken);
        return new HostDashboardStayList(count, items.Select(ToRomeDates).ToList());
    }

    private static readonly Expression<Func<Booking, HostDashboardStay>> StayProjection = b => new HostDashboardStay(
        b.Id,
        b.PropertyId,
        b.Property.Name,
        (b.Guest.FirstName + " " + b.Guest.LastName).Trim(),
        b.CheckInDate,
        b.CheckOutDate,
        b.Status,
        b.TotalPrice,
        b.CreatedAt);

    // Stay dates as their Europe/Rome calendar date (midnight UTC), whatever time a legacy value carries.
    private static HostDashboardStay ToRomeDates(HostDashboardStay stay) => stay with
    {
        CheckInDate = StayKpiRules.RomeDateOf(stay.CheckInDate),
        CheckOutDate = StayKpiRules.RomeDateOf(stay.CheckOutDate),
    };
}
