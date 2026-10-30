using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IPublicSiteSearchService"/>
/// <remarks>
/// <para>Reads, whatever the number of houses (at most 50): the published houses of the org, the taken nights of all of them
/// (<see cref="PublicOccupancyReader"/>, two queries), the prices the hosts confirmed (one), the tourist tax rates of the period (one) and
/// the ratings (one). The decisions are <see cref="PublicStayPlanner"/>: no price or availability rule lives here, so the list, the
/// calendar and the quote of the checkout cannot disagree.</para>
/// <para>Anonymous: the tenant filter is off, so every query names the org (a signed-in host of another org who looks at a site must get the
/// answer everybody gets). The search and the calendar are kept for a few seconds in <see cref="PublicReadCache"/>.</para>
/// </remarks>
public sealed class PublicSiteSearchService(
    AppDbContext db,
    ITouristTaxRateRepository taxRates,
    IConfiguration configuration,
    PublicReadCache cache,
    TimeProvider? timeProvider = null) : IPublicSiteSearchService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly PublicOccupancyReader _occupancy = new(db, configuration, timeProvider);

    // How far before the check-in the nearby dates (alternatives) go.
    private const int AlternativeLookBehindDays = 21;

    public async Task<PublicStaysResult> SearchStaysAsync(Org org, PublicStaysCriteria criteria, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(org);
        ArgumentNullException.ThrowIfNull(criteria);

        var today = _clock.TodayInRomeAsDateOnly();
        ValidateStay(criteria);

        // The answer is shared by whoever asks the same thing within a few seconds, so it is not worked out for one caller's token.
        var key = string.Join('|', "stays", org.Id.ToString("N"), today.DayNumber, org.CanTakeDirectPayments, CriteriaKey(criteria));
        return await cache.GetOrCreateAsync(key, () => BuildStaysAsync(org, criteria, today));
    }

    public async Task<PublicSiteCalendar> GetSiteCalendarAsync(
        Org org,
        DateOnly? from,
        DateOnly? to,
        int adults,
        int children,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(org);

        var today = _clock.TodayInRomeAsDateOnly();
        var start = from ?? today;
        var end = to ?? start.AddMonths(PublicCalendar.DefaultMonths);
        if (end <= start || end > start.AddMonths(PublicCalendar.MaxMonths))
        {
            throw new DomainRuleException(
                PublicSiteSearchErrorCodes.CalendarRangeInvalid, "PublicCalendarInvalidRange", PublicCalendar.MaxMonths);
        }

        var key = string.Join(
            '|', "site-calendar", org.Id.ToString("N"), today.DayNumber, org.CanTakeDirectPayments, start.DayNumber, end.DayNumber, adults, children);
        return await cache.GetOrCreateAsync(key, () => BuildSiteCalendarAsync(org, start, end, adults, children, today));
    }

    public async Task<IReadOnlyList<PublicDestination>> GetDestinationsAsync(Guid orgId, CancellationToken cancellationToken = default) =>
        PublicStayPlanner.Destinations(await LoadHousesAsync(orgId, cancellationToken));

    public async Task<IReadOnlyList<PublicStayCard>?> GetSimilarAsync(
        Guid orgId,
        string propertySlugOrId,
        int max,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(propertySlugOrId))
            return null;

        var houses = await LoadHousesAsync(orgId, cancellationToken);
        var target = Guid.TryParse(propertySlugOrId, out var id)
            ? houses.FirstOrDefault(house => house.Id == id)
            : houses.FirstOrDefault(house => house.Slug == propertySlugOrId);
        if (target is null)
            return null;

        var today = _clock.TodayInRomeAsDateOnly();
        var ids = houses.Select(house => house.Id).ToList();
        var data = new PublicStayData(
            today,
            AcceptsBookings: true,
            Taken: new Dictionary<Guid, IReadOnlySet<DateOnly>>(),
            await LoadHostPricesAsync(ids, today, today.AddDays(PublicSiteSearch.FromPriceDays), cancellationToken),
            TaxRates: [],
            await LoadRatingsAsync(orgId, ids, cancellationToken));
        return PublicStayPlanner.Similar(target, houses, data, max);
    }

    // ─── The list ───────────────────────────────────────────────────────────────────────────────────

    private async Task<PublicStaysResult> BuildStaysAsync(Org org, PublicStaysCriteria criteria, DateOnly today)
    {
        var houses = await LoadHousesAsync(org.Id, CancellationToken.None);
        var ids = houses.Select(house => house.Id).ToList();

        // The window the answer needs: the nights of the stay and of the nearby dates, and the days the first free date is looked for in.
        DateOnly from;
        DateOnly to;
        var taxRatesOfPeriod = new List<TouristTaxRate>();
        if (criteria.CheckIn is { } checkIn && criteria.CheckOut is { } checkOut)
        {
            var nights = checkOut.DayNumber - checkIn.DayNumber;
            var length = Math.Max(nights, houses.Count == 0 ? 1 : houses.Max(house => house.MinNights ?? 1));
            from = Max(today, checkIn.AddDays(-AlternativeLookBehindDays));
            to = Max(from, checkIn.AddDays(PublicSiteSearch.FirstFreeHorizonDays + length + 1));
            if (checkIn >= today && ids.Count > 0)
            {
                taxRatesOfPeriod.AddRange(await taxRates.GetActiveInPeriodAsync(
                    ToStoredDate(checkIn), ToStoredDate(checkOut.AddDays(-1)), CancellationToken.None));
            }
        }
        else
        {
            from = today;
            to = today.AddDays(PublicSiteSearch.FromPriceDays);
        }

        var taken = criteria.CheckIn is null
            ? new Dictionary<Guid, IReadOnlySet<DateOnly>>()
            : await LoadTakenAsync(ids, from, to);
        var data = new PublicStayData(
            today,
            org.CanTakeDirectPayments,
            taken,
            await LoadHostPricesAsync(ids, from, to, CancellationToken.None),
            taxRatesOfPeriod,
            await LoadRatingsAsync(org.Id, ids, CancellationToken.None));

        return PublicStayPlanner.Search(houses, data, criteria, _clock.GetUtcNow().UtcDateTime);
    }

    private async Task<PublicSiteCalendar> BuildSiteCalendarAsync(Org org, DateOnly start, DateOnly end, int adults, int children, DateOnly today)
    {
        var houses = await LoadHousesAsync(org.Id, CancellationToken.None);
        var ids = houses.Select(house => house.Id).ToList();
        var first = Max(start, today);

        var days = new List<PublicSiteCalendarDay>();
        if (first < end)
        {
            var data = new PublicStayData(
                today,
                org.CanTakeDirectPayments,
                await LoadTakenAsync(ids, first, end),
                await LoadHostPricesAsync(ids, first, end, CancellationToken.None),
                TaxRates: [],
                Ratings: new Dictionary<Guid, PublicRatingSummary>());
            days.AddRange(PublicStayPlanner.SiteDays(houses, data, first, end, adults + children));
        }

        return new PublicSiteCalendar(first, end, today, "EUR", adults, children, days, _clock.GetUtcNow().UtcDateTime);
    }

    /// <summary>The rules of the stay asked, the ones the quote and the checkout apply to every stay: a 422 before anything is read.</summary>
    private static void ValidateStay(PublicStaysCriteria criteria)
    {
        if (criteria.CheckIn is null && criteria.CheckOut is null)
            return;

        if (criteria.CheckIn is not { } checkIn
            || criteria.CheckOut is not { } checkOut
            || checkOut <= checkIn
            || checkOut.DayNumber - checkIn.DayNumber > TouristTaxCalculator.MaxNights)
        {
            throw new DomainRuleException(
                PublicSiteSearchErrorCodes.StaysDatesInvalid, "PublicStaysDatesInvalid", TouristTaxCalculator.MaxNights);
        }
    }

    // ─── What is read ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The published houses of the org (at most <see cref="PublicSiteSearch.MaxHouses"/>, by name): the rule of the site search.</summary>
    private async Task<IReadOnlyList<PublicStayHouse>> LoadHousesAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var rows = await db.Properties
            .AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(PublicListing.IsPublished)
            .Where(p => p.OrgId == orgId && p.Org.IsActive)
            .OrderBy(p => p.Name)
            .ThenBy(p => p.Id)
            .Take(PublicSiteSearch.MaxHouses)
            .Select(p => new HouseRow
            {
                Id = p.Id,
                Slug = p.Slug,
                Name = p.Name,
                City = p.City,
                ComuneIstatCode = p.ComuneIstatCode,
                Latitude = p.Latitude,
                Longitude = p.Longitude,
                PhotoUrls = p.PhotoUrls,
                Bedrooms = p.Bedrooms,
                Bathrooms = p.Bathrooms,
                MaxGuests = p.MaxGuests,
                AreaSqm = p.AreaSqm,
                NightlyRate = p.NightlyRate,
                CleaningFee = p.CleaningFee,
                WeekendSurchargePercent = p.WeekendSurchargePercent,
                MinNights = p.MinNights,
                PetsPolicy = p.PetsPolicy,
                SmokingPolicy = p.SmokingPolicy,
                PartiesPolicy = p.PartiesPolicy,
                Amenities = p.Amenities,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new PublicStayHouse(
                row.Id,
                row.Slug,
                row.Name,
                row.City,
                row.ComuneIstatCode,
                PropertyAddress.ToPublicCoordinate(row.Latitude),
                PropertyAddress.ToPublicCoordinate(row.Longitude),
                row.PhotoUrls.Take(5).ToList(),
                row.Bedrooms,
                row.Bathrooms,
                row.MaxGuests,
                row.AreaSqm,
                row.NightlyRate,
                row.CleaningFee,
                row.WeekendSurchargePercent,
                row.MinNights,
                row.PetsPolicy,
                row.SmokingPolicy,
                row.PartiesPolicy,
                row.Amenities))
            .ToList();
    }

    private async Task<IReadOnlyDictionary<Guid, IReadOnlySet<DateOnly>>> LoadTakenAsync(
        IReadOnlyCollection<Guid> ids,
        DateOnly from,
        DateOnly to)
    {
        var result = new Dictionary<Guid, IReadOnlySet<DateOnly>>();
        if (ids.Count == 0 || to <= from)
            return result;

        var taken = await _occupancy.TakenNightsAsync(ids, ToStoredDate(from), ToStoredDate(to));
        foreach (var (id, nights) in taken)
            result[id] = nights.Select(DateOnly.FromDateTime).ToHashSet();

        return result;
    }

    /// <summary>The prices the hosts confirmed for dates of <paramref name="from"/> (included) to <paramref name="to"/> (excluded): what the quote reads for one house, for all of them.</summary>
    private async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<DateOnly, decimal>>> LoadHostPricesAsync(
        IReadOnlyCollection<Guid> ids,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, IReadOnlyDictionary<DateOnly, decimal>>();
        if (ids.Count == 0 || to <= from)
            return result;

        var rows = await db.SeasonalPriceSuggestions
            .AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(s => ids.Contains(s.PropertyId) && s.AppliedPrice != null && s.StayDate >= from && s.StayDate < to)
            .Select(s => new { s.PropertyId, s.StayDate, Price = s.AppliedPrice!.Value })
            .ToListAsync(cancellationToken);
        foreach (var group in rows.GroupBy(row => row.PropertyId))
            result[group.Key] = group.ToDictionary(row => row.StayDate, row => row.Price);

        return result;
    }

    private async Task<IReadOnlyDictionary<Guid, PublicRatingSummary>> LoadRatingsAsync(
        Guid orgId,
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, PublicRatingSummary>();
        if (ids.Count == 0)
            return result;

        // The reviews the public reads: published and reported ones, never a removed one (the same rule as the property page).
        var rows = await db.Reviews
            .AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(r => r.OrgId == orgId && r.Status != ReviewStatus.Removed && ids.Contains(r.PropertyId))
            .GroupBy(r => r.PropertyId)
            .Select(g => new { PropertyId = g.Key, Count = g.Count(), Sum = g.Sum(r => (long)r.Rating) })
            .ToListAsync(cancellationToken);
        foreach (var row in rows.Where(row => row.Count > 0))
            result[row.PropertyId] = new PublicRatingSummary(row.Count, ReviewRules.Average(row.Sum, row.Count));

        return result;
    }

    // ─── Small helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Everything in the query that changes the answer, in a fixed order: the key the answer is kept under.</summary>
    private static string CriteriaKey(PublicStaysCriteria c) => string.Join(
        '|',
        c.CheckIn?.DayNumber,
        c.CheckOut?.DayNumber,
        c.Adults,
        c.Children,
        c.Pets,
        c.Where?.Trim().ToLowerInvariant(),
        c.MinPrice?.ToString(CultureInfo.InvariantCulture),
        c.MaxPrice?.ToString(CultureInfo.InvariantCulture),
        c.Bedrooms,
        c.Bathrooms,
        c.MinSqm,
        string.Join(',', c.Amenities.Order(StringComparer.OrdinalIgnoreCase)).ToLowerInvariant(),
        string.Join(',', c.Rules.Order(StringComparer.OrdinalIgnoreCase)).ToLowerInvariant(),
        string.Join(',', c.Tags.Order(StringComparer.OrdinalIgnoreCase)).ToLowerInvariant(),
        c.Sort,
        c.Page,
        c.PageSize,
        string.Join(',', c.IgnoredValues));

    private static DateOnly Max(DateOnly a, DateOnly b) => a >= b ? a : b;

    private static DateTime ToStoredDate(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    private sealed class HouseRow
    {
        public Guid Id { get; init; }
        public string? Slug { get; init; }
        public string Name { get; init; } = string.Empty;
        public string City { get; init; } = string.Empty;
        public string? ComuneIstatCode { get; init; }
        public decimal Latitude { get; init; }
        public decimal Longitude { get; init; }
        public List<string> PhotoUrls { get; init; } = [];
        public int Bedrooms { get; init; }
        public int Bathrooms { get; init; }
        public int MaxGuests { get; init; }
        public int? AreaSqm { get; init; }
        public decimal NightlyRate { get; init; }
        public decimal CleaningFee { get; init; }
        public decimal WeekendSurchargePercent { get; init; }
        public int? MinNights { get; init; }
        public PetsPolicy? PetsPolicy { get; init; }
        public SmokingPolicy? SmokingPolicy { get; init; }
        public PartiesPolicy? PartiesPolicy { get; init; }
        public List<PropertyAmenity> Amenities { get; init; } = [];
    }
}
