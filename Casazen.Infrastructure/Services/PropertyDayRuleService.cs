using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IPropertyDayRuleService"/>
/// <remarks>
/// <para><b>The days read</b> (<see cref="GetDaysAsync"/>) makes a fixed number of queries whatever the number of properties: the properties of the
/// scope (<c>InScope</c>, in SQL), then one each for the rules, the prices the host confirmed and the taken nights
/// (<see cref="PublicOccupancyReader"/>, two queries); everything is read by the property ids and the org, so nothing of another property or org can
/// enter. The price of a night is <see cref="StayPricing.NightPrice"/>, the function the quote adds the nights up with, fed with
/// <see cref="DayRulePricing.HostPrices"/>. It reads the prices in a batch instead of asking
/// <see cref="IPricingAdapterService.GetAppliedNightlyPricesAsync"/> for each property, which would be one query per property.</para>
/// <para><b>The channel manager is advice only.</b> <c>Mirrored</c> asks <see cref="IOctorateBookingTargets"/> once for each property of the answer
/// and only with the connector on; the lookups together take at most <see cref="MirroredBudget"/>. A property whose lookup fails or takes too long, or comes
/// after the budget is spent, is shown as not mirrored: the calendar is never refused because the channel manager cannot be asked. What it said about a
/// property, a failure or a timeout included, is remembered for <see cref="MirroredMemory"/> in the memory of the process, so a channel manager that is slow
/// or down is waited for once a minute for each property and not at every read; the properties a spent budget left out are asked at the next read. Logs
/// carry ids and codes only.</para>
/// <para><b>Writes are staged, never saved</b> (<see cref="ApplyAsync"/>): the caller owns the transaction.</para>
/// </remarks>
public sealed class PropertyDayRuleService(
    AppDbContext db,
    IOctorateBookingTargets targets,
    IFeatureFlags featureFlags,
    IConfiguration configuration,
    ILogger<PropertyDayRuleService> logger,
    TimeProvider? timeProvider = null,
    IMemoryCache? memory = null,
    TimeSpan? mirroredBudget = null) : IPropertyDayRuleService
{
    public static readonly TimeSpan MirroredBudget = TimeSpan.FromSeconds(3);

    public static readonly TimeSpan MirroredMemory = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _budget = mirroredBudget ?? MirroredBudget;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly PublicOccupancyReader _occupancy = new(db, configuration, timeProvider);

    public async Task<CalendarDays> GetDaysAsync(HostScope scope, CalendarDaysQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        HostCalendarRequestRules.EnsureWindow(query.From, query.To);

        var requested = HostCalendarRequestRules.RequestedIds(query.PropertyIds);
        var properties = await ReadPropertiesAsync(scope, requested, cancellationToken);
        var today = _clock.TodayInRomeAsDateOnly();
        if (properties.Count == 0)
            return new CalendarDays(today, []);

        var ids = properties.Select(p => p.Id).ToList();
        var toExclusive = query.To.AddDays(1);
        var rules = await ReadHostRulesAsync(scope, ids, query.From, toExclusive, cancellationToken);
        var seasonal = await ReadSeasonalPricesAsync(scope, ids, query.From, toExclusive, cancellationToken);
        var taken = await _occupancy.TakenNightsAsync(ids, Utc(query.From), Utc(toExclusive), cancellationToken);

        var mirrored = await MirroredAsync(ids, cancellationToken);
        var result = new List<CalendarDaysProperty>(properties.Count);
        foreach (var property in properties)
        {
            rules.TryGetValue(property.Id, out var propertyRules);
            seasonal.TryGetValue(property.Id, out var propertySeasonal);
            var takenNights = taken.TryGetValue(property.Id, out var nights)
                ? nights.Select(DateOnly.FromDateTime).ToHashSet()
                : new HashSet<DateOnly>();
            result.Add(new CalendarDaysProperty(
                property.Id,
                property.Timezone,
                StayCurrency.Normalize(property.Currency),
                mirrored.Contains(property.Id),
                DaysOf(property, query.From, query.To, propertyRules, propertySeasonal, takenNights)));
        }

        return new CalendarDays(today, result);
    }

    private static List<CalendarDay> DaysOf(
        PropertyRow property,
        DateOnly from,
        DateOnly to,
        IReadOnlyDictionary<DateOnly, DayRuleValues>? rules,
        IReadOnlyDictionary<DateOnly, decimal>? seasonal,
        HashSet<DateOnly> taken)
    {
        var hostPrices = DayRulePricing.HostPrices(seasonal, rules);
        var days = new List<CalendarDay>(to.DayNumber - from.DayNumber + 1);
        for (var night = from; night <= to; night = night.AddDays(1))
        {
            DayRuleValues? rule = null;
            rules?.TryGetValue(night, out rule);
            var price = StayPricing.NightPrice(night, property.NightlyRate, property.WeekendSurchargePercent, hostPrices);

            days.Add(new CalendarDay(
                night,
                StayPricing.ToCents(price),
                DayRulePricing.SourceOf(night, rule, seasonal, property.WeekendSurchargePercent),
                DayRuleDefaults.MinNightsOf(rule, property.MinNights),
                rule?.MaxNights,
                rule?.ClosedToArrival ?? false,
                rule?.ClosedToDeparture ?? false,
                rule?.MinAdvanceDays,
                rule?.MaxAdvanceDays,
                rule is not null,
                taken.Contains(night)));
        }

        return days;
    }

    private sealed record PropertyRow(
        Guid Id,
        string Timezone,
        string Currency,
        decimal NightlyRate,
        decimal WeekendSurchargePercent,
        int? MinNights);

    private async Task<List<PropertyRow>> ReadPropertiesAsync(HostScope scope, List<Guid>? requested, CancellationToken cancellationToken)
    {
        var query = db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == scope.OrgId && p.IsActive)
            .Where(PropertyRentalModeRules.IsShortRent)
            .InScope(scope);
        if (requested is { } wanted)
            query = query.Where(p => wanted.Contains(p.Id));

        return await query
            .OrderBy(p => p.Name)
            .ThenBy(p => p.Id)
            .Take(HostCalendarLimits.MaxProperties)
            .Select(p => new PropertyRow(p.Id, p.Timezone, p.Currency, p.NightlyRate, p.WeekendSurchargePercent, p.MinNights))
            .ToListAsync(cancellationToken);
    }

    private async Task<Dictionary<Guid, IReadOnlyDictionary<DateOnly, DayRuleValues>>> ReadHostRulesAsync(
        HostScope scope,
        List<Guid> propertyIds,
        DateOnly from,
        DateOnly toExclusive,
        CancellationToken cancellationToken)
    {
        var rows = await db.PropertyDayRules
            .AsNoTracking()
            .Where(r => r.OrgId == scope.OrgId && propertyIds.Contains(r.PropertyId) && r.StayDate >= from && r.StayDate < toExclusive)
            .Select(r => new RuleRow(
                r.PropertyId, r.StayDate, r.Price, r.MinNights, r.MaxNights, r.ClosedToArrival, r.ClosedToDeparture, r.MinAdvanceDays, r.MaxAdvanceDays))
            .ToListAsync(cancellationToken);
        return ByProperty(rows);
    }

    private async Task<Dictionary<Guid, IReadOnlyDictionary<DateOnly, decimal>>> ReadSeasonalPricesAsync(
        HostScope scope,
        List<Guid> propertyIds,
        DateOnly from,
        DateOnly toExclusive,
        CancellationToken cancellationToken)
    {
        var rows = await db.SeasonalPriceSuggestions
            .AsNoTracking()
            .Where(s => s.OrgId == scope.OrgId
                && propertyIds.Contains(s.PropertyId)
                && s.AppliedPrice != null
                && s.StayDate >= from
                && s.StayDate < toExclusive)
            .Select(s => new { s.PropertyId, s.StayDate, Price = s.AppliedPrice!.Value })
            .ToListAsync(cancellationToken);

        var result = new Dictionary<Guid, IReadOnlyDictionary<DateOnly, decimal>>();
        foreach (var group in rows.GroupBy(row => row.PropertyId))
            result[group.Key] = group.ToDictionary(row => row.StayDate, row => row.Price);

        return result;
    }

    private async Task<HashSet<Guid>> MirroredAsync(IReadOnlyList<Guid> propertyIds, CancellationToken cancellationToken)
    {
        var mirrored = new HashSet<Guid>();

        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return mirrored;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_budget);
        var spent = false;
        foreach (var propertyId in propertyIds)
        {
            if (Remembered(propertyId) is { } known)
            {
                if (known)
                    mirrored.Add(propertyId);

                continue;
            }

            if (spent || budget.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!spent)
                {
                    logger.LogWarning(
                        "The channel manager lookups of the calendar took more than {Seconds} seconds: the rest are shown as not mirrored and asked at the next read",
                        _budget.TotalSeconds);
                    spent = true;
                }

                continue;
            }

            var found = false;
            try
            {
                found = (await targets.LookupAsync(propertyId, budget.Token)).Target is not null;
            }
            catch (OctorateException ex)
            {
                logger.LogWarning(
                    "The channel manager could not be asked about property {PropertyId} ({Code}, HTTP {Status}): shown as not mirrored",
                    propertyId,
                    ex.Code,
                    ex.OctorateStatusCode);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("The channel manager lookup of property {PropertyId} took too long: shown as not mirrored", propertyId);
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning(ex, "The channel manager cannot be asked about property {PropertyId}: shown as not mirrored", propertyId);
            }

            Remember(propertyId, found);
            if (found)
                mirrored.Add(propertyId);
        }

        return mirrored;
    }

    // What the channel manager said about a property in the last minute (true, false, or false because it failed or took too long); null when nothing is remembered.
    private bool? Remembered(Guid propertyId) =>
        memory is not null && memory.TryGetValue(new MirroredKey(propertyId), out MirroredMemo? memo) && memo is not null && memo.Until > _clock.GetUtcNow()
            ? memo.Mirrored
            : null;

    private void Remember(Guid propertyId, bool mirrored) =>
        memory?.Set(new MirroredKey(propertyId), new MirroredMemo(mirrored, _clock.GetUtcNow().Add(MirroredMemory)), MirroredMemory * 2);

    private sealed record MirroredKey(Guid PropertyId);

    private sealed record MirroredMemo(bool Mirrored, DateTimeOffset Until);

    private IQueryable<Property> ReachableProperties(HostScope scope) =>
        db.Properties.AsNoTracking().Where(p => p.OrgId == scope.OrgId).InScope(scope);

    public async Task<IReadOnlyDictionary<DateOnly, DayRuleValues>> GetRulesAsync(
        HostScope scope,
        Guid propertyId,
        DateOnly from,
        DateOnly toExclusive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (!await ReachableProperties(scope).AnyAsync(p => p.Id == propertyId, cancellationToken))
            throw new NotFoundException("Property not found") { MessageKey = "PropertyNotFound" };

        if (toExclusive <= from)
            return new Dictionary<DateOnly, DayRuleValues>();

        var rows = await db.PropertyDayRules
            .AsNoTracking()
            .Where(r => r.OrgId == scope.OrgId && r.PropertyId == propertyId && r.StayDate >= from && r.StayDate < toExclusive)
            .Select(r => new RuleRow(
                r.PropertyId, r.StayDate, r.Price, r.MinNights, r.MaxNights, r.ClosedToArrival, r.ClosedToDeparture, r.MinAdvanceDays, r.MaxAdvanceDays))
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(row => row.StayDate, row => row.Values());
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<DateOnly, DayRuleValues>>> GetRulesForSiteAsync(
        IReadOnlyCollection<Guid> propertyIds,
        DateOnly from,
        DateOnly toExclusive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(propertyIds);

        var result = new Dictionary<Guid, IReadOnlyDictionary<DateOnly, DayRuleValues>>();
        if (propertyIds.Count == 0 || toExclusive <= from)
            return result;

        var ids = propertyIds.Distinct().ToList();
        var rows = await db.PropertyDayRules
            .AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(r => ids.Contains(r.PropertyId) && r.StayDate >= from && r.StayDate < toExclusive)
            .Select(r => new RuleRow(
                r.PropertyId, r.StayDate, r.Price, r.MinNights, r.MaxNights, r.ClosedToArrival, r.ClosedToDeparture, r.MinAdvanceDays, r.MaxAdvanceDays))
            .ToListAsync(cancellationToken);

        return ByProperty(rows);
    }

    public async Task<DayRuleApplyResult> ApplyAsync(
        HostScope scope,
        Guid propertyId,
        DateOnly from,
        DateOnly toInclusive,
        IReadOnlySet<DayOfWeek>? days,
        DayRulePatch patch,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(patch);

        DayRuleRules.EnsureRange(from, toInclusive);
        if (patch.IsEmpty || days is { Count: 0 })
            throw DayRuleRules.ProblemOf(DayRuleErrorCodes.NothingToChange);

        var property = await ReachableProperties(scope)
            .Where(p => p.Id == propertyId)
            .Select(p => new { p.OrgId, p.Timezone })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Property not found") { MessageKey = "PropertyNotFound" };

        var now = _clock.GetUtcNow();
        var today = DayRuleRules.TodayOf(property.Timezone, now);
        var existing = await db.PropertyDayRules
            .Where(r => r.OrgId == property.OrgId && r.PropertyId == propertyId && r.StayDate >= from && r.StayDate <= toInclusive)
            .ToDictionaryAsync(r => r.StayDate, cancellationToken);

        var plan = new List<(DateOnly Night, PropertyDayRule? Row, DayRuleValues After)>();
        var unchanged = 0;
        var skippedPast = 0;
        for (var night = from; night <= toInclusive; night = night.AddDays(1))
        {
            if (days is not null && !days.Contains(night.DayOfWeek))
                continue;

            if (night < today)
            {
                skippedPast++;
                continue;
            }

            existing.TryGetValue(night, out var row);
            var before = row is null ? DayRuleValues.None : DayRuleValues.Of(row);
            var after = patch.ApplyTo(before);
            DayRuleRules.EnsureValid(after);
            if (after == before)
            {
                unchanged++;
                continue;
            }

            plan.Add((night, row, after));
        }

        // Every night the change touches is already over on the clock of the property and nothing is left to change or to leave as it is: there is
        // nothing to stage, and the caller is told why instead of «0 changed».
        if (plan.Count == 0 && unchanged == 0 && skippedPast > 0)
            throw DayRuleRules.ProblemOf(DayRuleErrorCodes.InPast);

        var stamp = now.UtcDateTime;
        foreach (var (night, row, after) in plan)
        {
            if (!after.HasAny)
            {
                if (row is not null)
                    db.PropertyDayRules.Remove(row);

                continue;
            }

            if (row is null)
            {
                var added = new PropertyDayRule
                {
                    OrgId = property.OrgId,
                    PropertyId = propertyId,
                    StayDate = night,
                    CreatedAt = stamp,
                    UpdatedAt = stamp,
                    UpdatedByUserId = userId,
                };
                after.WriteTo(added);
                db.PropertyDayRules.Add(added);
            }
            else
            {
                after.WriteTo(row);
                row.UpdatedAt = stamp;
                row.UpdatedByUserId = userId;
            }
        }

        return new DayRuleApplyResult(plan.Count, unchanged, skippedPast);
    }

    public void DiscardStaged()
    {
        foreach (var entry in db.ChangeTracker.Entries<PropertyDayRule>().ToList())
            entry.State = EntityState.Detached;
    }

    public bool IsDateConflict(Exception exception) =>
        exception is DbUpdateException update && OrgTeamAccess.IsUniqueViolation(update, PropertyDayRule.PropertyDateIndexName);

    private sealed record RuleRow(
        Guid PropertyId,
        DateOnly StayDate,
        decimal? Price,
        int? MinNights,
        int? MaxNights,
        bool ClosedToArrival,
        bool ClosedToDeparture,
        int? MinAdvanceDays,
        int? MaxAdvanceDays)
    {
        public DayRuleValues Values() =>
            new(Price, MinNights, MaxNights, ClosedToArrival, ClosedToDeparture, MinAdvanceDays, MaxAdvanceDays);
    }

    private static Dictionary<Guid, IReadOnlyDictionary<DateOnly, DayRuleValues>> ByProperty(IEnumerable<RuleRow> rows)
    {
        var result = new Dictionary<Guid, IReadOnlyDictionary<DateOnly, DayRuleValues>>();
        foreach (var group in rows.GroupBy(row => row.PropertyId))
            result[group.Key] = group.ToDictionary(row => row.StayDate, row => row.Values());

        return result;
    }

    private static DateTime Utc(DateOnly day) => day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
}
