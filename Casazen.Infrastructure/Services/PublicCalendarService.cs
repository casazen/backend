using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IPublicCalendarService"/>
/// <remarks>
/// <para>No rule of its own. The price of a night is <see cref="StayPricing.NightPrice"/>, the function the quote adds the nights up
/// with, fed with what the quote is fed with (the nightly rate, the weekend surcharge of the property and the prices the host
/// set for dates, <see cref="IPricingAdapterService.GetAppliedNightlyPricesAsync"/>: a confirmed seasonal price, overridden by the price of
/// the day rule of the night): the price of the calendar and the line of the quote of a one-night stay are the same number. The rules of the
/// day (the longest stay, the closed arrival and departure, the advance) are the ones the checkout applies, read with the rest of the site
/// through <see cref="IPropertyDayRuleService.GetRulesForSiteAsync"/>. The taken nights are <see cref="PublicOccupancyReader"/>, the nights the
/// availability shows and the booking checks refuse.</para>
/// <para>Anonymous: the tenant filter is off, so every query names the property, and the properties are those of
/// <see cref="PublicListing.IsPublished"/> of an active org. Five small reads for any range up to 13 months: the property, the
/// bookings, the blocks, the host's prices, the rules of the days.</para>
/// </remarks>
public sealed class PublicCalendarService(
    AppDbContext db,
    IPricingAdapterService pricing,
    IConfiguration configuration,
    TimeProvider? timeProvider = null,
    IPropertyDayRuleService? dayRules = null) : IPublicCalendarService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly PublicOccupancyReader _occupancy = new(db, configuration, timeProvider);

    public async Task<PublicCalendar> GetAsync(
        Guid propertyId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken = default)
    {
        var (start, end) = ValidateRange(from, to);
        var property = await PublishedProperties()
            .Where(p => p.Id == propertyId)
            .Select(Project)
            .FirstOrDefaultAsync(cancellationToken);

        return await BuildAsync(property, start, end, cancellationToken);
    }

    public async Task<PublicCalendar> GetAsync(
        Guid orgId,
        string propertySlugOrId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken = default)
    {
        var (start, end) = ValidateRange(from, to);
        var query = PublishedProperties().Where(p => p.OrgId == orgId);
        query = Guid.TryParse(propertySlugOrId, out var id)
            ? query.Where(p => p.Id == id)
            : query.Where(p => p.Slug == propertySlugOrId);

        var property = string.IsNullOrWhiteSpace(propertySlugOrId)
            ? null
            : await query.Select(Project).FirstOrDefaultAsync(cancellationToken);

        return await BuildAsync(property, start, end, cancellationToken);
    }

    private async Task<PublicCalendar> BuildAsync(
        PropertyRow? property,
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken)
    {
        if (property is null)
        {
            throw new NotFoundException("Property not published on the booking site")
            {
                Code = PublicAvailabilityErrorCodes.PropertyNotFound,
                MessageKey = "PublicPropertyNotFound",
            };
        }

        var today = _clock.TodayInRomeAsDateOnly();
        var first = start < today ? today : start;
        var days = new List<PublicCalendarDay>();
        if (first < end)
        {
            var taken = (await _occupancy.TakenNightsAsync(
                [property.Id], ToStoredDate(first), ToStoredDate(end), cancellationToken))[property.Id];
            var takenDates = taken.Select(DateOnly.FromDateTime).ToHashSet();
            var hostPrices = await pricing.GetAppliedNightlyPricesAsync(property.Id, first, end, cancellationToken);

            // The rules of the days (NF-36): dates, numbers and flags of the property, public like its price; no guest data.
            IReadOnlyDictionary<DateOnly, DayRuleValues> rules = DayRuleStayChecks.NoRules;
            if (dayRules is not null
                && (await dayRules.GetRulesForSiteAsync([property.Id], first, end, cancellationToken)).TryGetValue(property.Id, out var ofProperty))
            {
                rules = ofProperty;
            }

            for (var day = first; day < end; day = day.AddDays(1))
            {
                rules.TryGetValue(day, out var rule);
                var price = StayPricing.NightPrice(day, property.NightlyRate, property.WeekendSurchargePercent, hostPrices);
                days.Add(new PublicCalendarDay(
                    day,
                    !takenDates.Contains(day),
                    StayPricing.ToCents(price),
                    DayRuleDefaults.MinNightsOf(rule, property.MinNights),
                    rule?.MaxNights,
                    rule?.ClosedToArrival ?? false,
                    rule?.ClosedToDeparture ?? false,
                    rule?.MinAdvanceDays,
                    rule?.MaxAdvanceDays));
            }
        }

        return new PublicCalendar(property.Id, first, end, today, StayCurrency.Normalize(property.Currency), days, _clock.GetUtcNow().UtcDateTime);
    }

    /// <summary>The range asked for, or its defaults, and the 422 for one that cannot be answered.</summary>
    private (DateOnly Start, DateOnly End) ValidateRange(DateOnly? from, DateOnly? to)
    {
        var start = from ?? _clock.TodayInRomeAsDateOnly();
        var end = to ?? start.AddMonths(PublicCalendar.DefaultMonths);
        if (end <= start || end > start.AddMonths(PublicCalendar.MaxMonths))
        {
            throw new DomainRuleException(
                PublicCalendarErrorCodes.InvalidRange, "PublicCalendarInvalidRange", PublicCalendar.MaxMonths);
        }

        return (start, end);
    }

    // Public data of a property on the site: published, of an active org, whoever signs in (a signed-in host of another org would
    // otherwise see every night free).
    private IQueryable<Property> PublishedProperties() =>
        db.Properties
            .AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(PublicListing.IsPublished)
            .Where(p => p.Org.IsActive);

    private static readonly System.Linq.Expressions.Expression<Func<Property, PropertyRow>> Project =
        p => new PropertyRow(p.Id, p.NightlyRate, p.WeekendSurchargePercent, p.MinNights, p.Currency);

    private static DateTime ToStoredDate(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    private sealed record PropertyRow(Guid Id, decimal NightlyRate, decimal WeekendSurchargePercent, int? MinNights, string Currency);
}
