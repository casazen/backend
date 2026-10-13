using System.Linq.Expressions;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
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

    public Task<HostDashboardKpis> GetKpisAsync(
        HostScope scope,
        HostDashboardPeriodKind kind,
        DateOnly? month,
        CancellationToken cancellationToken = default) =>
        GetKpisAsync(scope, new HostDashboardQuery(kind, month), cancellationToken);

    public async Task<HostDashboardKpis> GetKpisAsync(
        HostScope scope,
        HostDashboardQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        var now = _clock.GetUtcNow();
        var today = RomeCalendar.TodayAt(now);
        var period = PeriodOf(query, today);

        // One 404 for a property that is missing, another org's or not reached by the caller: nothing tells them apart.
        if (query.PropertyId is { } filtered && !await IsPropertyInScopeAsync(scope, filtered, cancellationToken))
            throw new NotFoundException($"Property {filtered} not found") { MessageKey = "PropertyNotFound" };

        var propertyIds = await ActivePropertiesInScope(scope, query.PropertyId)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        var current = await GetFiguresAsync(scope, query, propertyIds, period, now.UtcDateTime, cancellationToken);
        var previous = query.Compare
            ? await GetFiguresAsync(scope, query, propertyIds, period.Previous(), now.UtcDateTime, cancellationToken)
            : null;

        var stays = BookingsInScope(scope, query.PropertyId);
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
            current.Occupancy,
            current.Revenue,
            current.RevenueStayCount,
            arrivals,
            departures,
            upcoming,
            recent.Select(ToRomeDates).ToList())
        {
            Collected = current.Collected,
            DirectShare = current.DirectShare,
            Previous = previous,
        };
    }

    public async Task<HostDashboardTodayStays> GetTodayStaysAsync(HostScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var today = RomeCalendar.TodayAt(_clock.GetUtcNow());
        var stays = BookingsInScope(scope, null);
        var arrivals = await ListAsync(
            stays.Where(StayKpiRules.ArrivesOn(today)).OrderBy(b => b.Property.Name).ThenBy(b => b.Id),
            TodayListSize, cancellationToken);
        var departures = await ListAsync(
            stays.Where(StayKpiRules.DepartsOn(today)).OrderBy(b => b.Property.Name).ThenBy(b => b.Id),
            TodayListSize, cancellationToken);
        // After today: the arrivals of today are already in the first list.
        var upcoming = await ListAsync(
            stays.Where(StayKpiRules.UpcomingCheckIn(today.AddDays(1))).OrderBy(b => b.CheckInDate).ThenBy(b => b.Id),
            ListSize, cancellationToken);

        return new HostDashboardTodayStays(today, arrivals, departures, upcoming);
    }

    public async Task<IReadOnlyList<HostDashboardIcalFeed>> GetIcalFeedsAsync(
        HostScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var feeds = db.PropertyICalFeeds.AsNoTracking().Where(f => f.OrgId == scope.OrgId).InScope(scope);

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

    // The figures of one period (SR-03): the current one, and the one before it when the caller compares.
    private async Task<HostDashboardFigures> GetFiguresAsync(
        HostScope scope,
        HostDashboardQuery query,
        IReadOnlyCollection<Guid> propertyIds,
        HostDashboardPeriod period,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var occupancy = await GetOccupancyAsync(propertyIds, period, nowUtc, cancellationToken);
        var (revenue, revenueStays, directStays) = await GetRevenueAsync(scope, query.PropertyId, period, cancellationToken);
        var collected = query.IncludeCollected
            ? await GetCollectedAsync(scope, query.PropertyId, period, cancellationToken)
            : null;

        return new HostDashboardFigures(
            period, occupancy, revenue, revenueStays, collected, new HostDashboardDirectShare(directStays, revenueStays));
    }

    private async Task<(decimal Revenue, int Stays, int DirectStays)> GetRevenueAsync(
        HostScope scope,
        Guid? propertyId,
        HostDashboardPeriod period,
        CancellationToken cancellationToken)
    {
        var stays = await BookingsInScope(scope, propertyId)
            .Where(StayKpiRules.IsConfirmedStay())
            .Where(PropertyOccupancy.BookingTakesNightIn(period.From, period.To))
            .Select(b => new { b.BasePrice, b.CheckInDate, b.CheckOutDate, b.Source })
            .ToListAsync(cancellationToken);

        var counted = stays
            .Where(s => PropertyOccupancy.NightsIn(s.CheckInDate, s.CheckOutDate, period.From, period.To).Any())
            .ToList();
        var revenue = counted.Sum(s =>
            StayKpiRules.RevenueInPeriod(s.BasePrice, s.CheckInDate, s.CheckOutDate, period.From, period.To));
        return (
            Math.Round(revenue, 2, MidpointRounding.AwayFromZero),
            counted.Count,
            counted.Count(s => s.Source == BookingSource.Direct));
    }

    // Money collected in the period, by cash (SR-03, PaymentCashRules): the payments of the scope that came in on those days.
    private async Task<HostDashboardCollected> GetCollectedAsync(
        HostScope scope,
        Guid? propertyId,
        HostDashboardPeriod period,
        CancellationToken cancellationToken)
    {
        var payments = db.Payments.AsNoTracking().Where(p => p.OrgId == scope.OrgId).InScope(scope);
        if (propertyId is { } id)
            payments = payments.Where(p => p.Booking.PropertyId == id);

        var settled = await payments
            .Where(PaymentCashRules.CollectedBetween(period.From, period.To))
            .Select(p => new { p.Amount, p.RefundedAmount })
            .ToListAsync(cancellationToken);

        var amount = settled.Sum(p => PaymentCashRules.Collected(p.Amount, p.RefundedAmount));
        return new HostDashboardCollected(Math.Round(amount, 2, MidpointRounding.AwayFromZero), settled.Count);
    }

    private Task<bool> IsPropertyInScopeAsync(HostScope scope, Guid propertyId, CancellationToken cancellationToken) =>
        db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == scope.OrgId && p.Id == propertyId)
            .InScope(scope)
            .AnyAsync(cancellationToken);

    private static HostDashboardPeriod PeriodOf(HostDashboardQuery query, DateTime todayInRome) => query.Kind switch
    {
        HostDashboardPeriodKind.Last30Days => HostDashboardPeriod.ForLast30Days(todayInRome),
        HostDashboardPeriodKind.Next30Days => HostDashboardPeriod.ForNext30Days(todayInRome),
        _ => HostDashboardPeriod.ForMonth(query.Month ?? DateOnly.FromDateTime(todayInRome)),
    };

    // Active short-rent properties of the scope, as GET /api/properties lists them, minus the ones in long-term mode (SR-03:
    // they were still counted in the number of properties and in the denominator of the occupancy, PM-01 follow-up).
    private IQueryable<Property> ActivePropertiesInScope(HostScope scope, Guid? propertyId)
    {
        var properties = db.Properties
            .AsNoTracking()
            .Where(PropertyRentalModeRules.IsShortRent)
            .Where(p => p.OrgId == scope.OrgId && p.IsActive)
            .InScope(scope);
        return propertyId is { } id ? properties.Where(p => p.Id == id) : properties;
    }

    // Bookings of the scope (TN-3, AM-03): the org and, unless org-wide, the properties the caller reaches; of one property when asked.
    private IQueryable<Booking> BookingsInScope(HostScope scope, Guid? propertyId)
    {
        var bookings = db.Bookings.AsNoTracking().Where(b => b.OrgId == scope.OrgId).InScope(scope);
        return propertyId is { } id ? bookings.Where(b => b.PropertyId == id) : bookings;
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
        b.CreatedAt)
    {
        BookingCode = b.BookingCode,
        Source = b.Source,
        NumberOfGuests = b.NumberOfGuests,
        ArrivedAt = b.ArrivedAt,
    };

    // Stay dates as their Europe/Rome calendar date (midnight UTC), whatever time a legacy value carries.
    private static HostDashboardStay ToRomeDates(HostDashboardStay stay) => stay with
    {
        CheckInDate = StayKpiRules.RomeDateOf(stay.CheckInDate),
        CheckOutDate = StayKpiRules.RomeDateOf(stay.CheckOutDate),
    };
}
