using System.Text;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Octorate;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// <see cref="ICalendarChangeService"/> (E08/F05/US02/T03, NF-36, NF-78): the preview and the application of a change of price and rules for one or many properties.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Reach.</b> The properties of the request are read through the <see cref="HostScope"/> of the caller (<c>InScope</c>, in SQL) and the short-rent ones only:
/// one that is not found for the caller (another org's, outside its reach, deleted, long-term) fails the whole request with 404 <c>property_not_found</c>, and the rule
/// service takes the same scope for every write.</item>
/// <item><b>All or nothing.</b> The process and its chunks (<see cref="ICalendarChangeRecorder"/>, whose lookups come first), then the rules of every property
/// (<see cref="IPropertyDayRuleService.ApplyAsync"/>, staged), then <b>one</b> <c>SaveChangesAsync</c>: one database transaction, so a failure of any property leaves no rule
/// and no process. A change that is refused, or that changes nothing, stages nothing that stays: what was staged is forgotten. A night inserted by a concurrent change
/// (<see cref="IPropertyDayRuleService.IsDateConflict"/>) is staged once more and saved.</item>
/// <item><b>The preview stages nothing and saves nothing:</b> the nights are judged by the same pure rules (<see cref="CalendarChangeRules"/>). Taken nights are counted,
/// not skipped: a rule on a booked night changes nothing for that booking.</item>
/// <item><b>After the commit</b> a process that is <c>Queued</c> is enqueued once for an immediate run (<see cref="CalendarChangeProcessJob"/>).</item>
/// <item>No personal data in the summary of the process: the weekdays and the actions and values of the patch.</item>
/// </list>
/// </remarks>
public sealed class CalendarChangeService(
    AppDbContext db,
    IPropertyDayRuleService dayRules,
    ICalendarChangeRecorder recorder,
    IOctorateBookingTargets targets,
    IFeatureFlags featureFlags,
    IOptions<OctorateCalendarOptions> options,
    IConfiguration configuration,
    ILogger<CalendarChangeService> logger,
    TimeProvider? timeProvider = null,
    IBackgroundJobClient? jobs = null,
    TimeSpan? lookupBudget = null) : ICalendarChangeService
{
    private static readonly IReadOnlyDictionary<DateOnly, decimal> NoPrices = new Dictionary<DateOnly, decimal>();

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private readonly TimeSpan _budget = lookupBudget ?? PropertyDayRuleService.MirroredBudget;

    private sealed record PropertyRow(Guid Id, string Name, string Timezone, decimal NightlyRate, decimal WeekendSurchargePercent, int? MinNights);

    private enum Reach
    {
        NotMirrored,
        Mirrored,
        RoomNotChosen,
    }

    public async Task<CalendarChangePreview> PreviewAsync(HostScope scope, CalendarChangeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);

        var ids = CalendarChangeRules.EnsureValid(request);
        var properties = await ResolvePropertiesAsync(scope, ids, cancellationToken);
        var now = _clock.GetUtcNow();
        var settings = options.Value;
        var toExclusive = request.To.AddDays(1);

        var seasonal = await ReadSeasonalPricesAsync(scope.OrgId, ids, request.From, toExclusive, cancellationToken);
        var occupancy = new PublicOccupancyReader(db, configuration, _clock);
        var taken = await occupancy.TakenNightsAsync(ids, Utc(request.From), Utc(toExclusive), cancellationToken);

        var rows = new List<CalendarChangePreviewProperty>(properties.Count);
        var warnings = new List<CalendarChangeWarning>();

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_budget);
        foreach (var property in properties)
        {
            var today = DayRuleRules.TodayOf(property.Timezone, now);
            var rules = await dayRules.GetRulesAsync(scope, property.Id, request.From, toExclusive, cancellationToken);
            var takenNights = taken.TryGetValue(property.Id, out var nights)
                ? nights.Select(DateOnly.FromDateTime).ToHashSet()
                : new HashSet<DateOnly>();
            var evaluation = CalendarChangeRules.Evaluate(
                today,
                request,
                rules,
                seasonal.TryGetValue(property.Id, out var prices) ? prices : NoPrices,
                new OctorateCalendarProperty(property.NightlyRate, property.WeekendSurchargePercent, property.MinNights),
                takenNights);

            var reach = await ReachOfAsync(property.Id, budget.Token, cancellationToken);
            if (reach == Reach.NotMirrored)
                warnings.Add(new CalendarChangeWarning(CalendarPushErrorCodes.PropertyNotMirrored, property.Id));
            else if (reach == Reach.RoomNotChosen)
                warnings.Add(new CalendarChangeWarning(CalendarPushErrorCodes.RoomNotChosenWarning, property.Id));

            rows.Add(new CalendarChangePreviewProperty(
                property.Id,
                property.Name,
                reach != Reach.NotMirrored,
                evaluation.Days,
                evaluation.ChangedDays,
                evaluation.UnchangedDays,
                evaluation.TakenDays,
                evaluation.SkippedPastDays,
                evaluation.Samples,
                reach == Reach.Mirrored ? CalendarChangeRules.EstimateQuotaUnits(today, request.From, request.To, settings) : 0));
        }

        return new CalendarChangePreview(
            rows,
            rows.Sum(r => r.Days),
            rows.Sum(r => r.ChangedDays),
            rows.Sum(r => r.UnchangedDays),
            rows.Sum(r => r.TakenDays),
            rows.Sum(r => r.SkippedPastDays),
            rows.Sum(r => r.EstimatedQuotaUnits),
            warnings);
    }

    public async Task<CalendarChangeApplyResult> ApplyAsync(
        HostScope scope,
        CalendarChangeRequest request,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);

        var ids = CalendarChangeRules.EnsureValid(request);
        await ResolvePropertiesAsync(scope, ids, cancellationToken);

        CalendarChangeRecord? record = null;
        try
        {
            record = await recorder.RecordAsync(
                ids.Count == 1 ? CalendarChangeKind.DayRules : CalendarChangeKind.Bulk,
                [.. ids.Select(id => new CalendarWindow(id, request.From, request.To))],
                userId,
                SummaryOf(request),
                cancellationToken);

            var staged = await StageRulesAsync(scope, ids, request, userId, cancellationToken);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (dayRules.IsDateConflict(ex))
            {
                // A concurrent change inserted a night first: the rows exist now and are updated. The process and its chunks stay staged.
                dayRules.DiscardStaged();
                staged = await StageRulesAsync(scope, ids, request, userId, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            if (record.NeedsRun)
                Enqueue(record.ProcessId);

            logger.LogInformation(
                "Calendar change {ProcessId} applied: {Properties} properties, {Changed} days changed, state {State}",
                record.ProcessId,
                ids.Count,
                staged.Changed,
                record.Process.State);
            return new CalendarChangeApplyResult(record.ProcessId, record.Process.State, staged.Changed, staged.Unchanged, staged.SkippedPast);
        }
        catch
        {
            dayRules.DiscardStaged();
            Forget(record);
            throw;
        }
    }

    private async Task<DayRuleApplyResult> StageRulesAsync(
        HostScope scope,
        IReadOnlyList<Guid> ids,
        CalendarChangeRequest request,
        string? userId,
        CancellationToken ct)
    {
        int changed = 0, unchanged = 0, skippedPast = 0;
        foreach (var id in ids)
        {
            var applied = await dayRules.ApplyAsync(scope, id, request.From, request.To, request.DaysOfWeek, request.Patch, userId, ct);
            changed += applied.Changed;
            unchanged += applied.Unchanged;
            skippedPast += applied.SkippedPast;
        }

        if (changed == 0)
            throw DayRuleRules.ProblemOf(DayRuleErrorCodes.NothingToChange);

        return new DayRuleApplyResult(changed, unchanged, skippedPast);
    }

    private void Enqueue(Guid processId)
    {
        try
        {
            jobs?.Enqueue<CalendarChangeProcessJob>(job => job.RunAsync(processId, CancellationToken.None));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Calendar change {ProcessId} could not be enqueued: the recurring job sends it", processId);
        }
    }

    private void Forget(CalendarChangeRecord? record)
    {
        if (record is null)
            return;

        db.Entry(record.Process).State = EntityState.Detached;
        foreach (var chunk in record.Chunks)
            db.Entry(chunk).State = EntityState.Detached;
    }

    private async Task<List<PropertyRow>> ResolvePropertiesAsync(HostScope scope, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var found = await db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == scope.OrgId && ids.Contains(p.Id))
            .Where(PropertyRentalModeRules.IsShortRent)
            .InScope(scope)
            .Select(p => new PropertyRow(p.Id, p.Name, p.Timezone, p.NightlyRate, p.WeekendSurchargePercent, p.MinNights))
            .ToListAsync(ct);
        if (found.Count != ids.Count)
        {
            throw new NotFoundException("Property not found")
            {
                Code = ManualBlockErrorCodes.PropertyNotFound,
                MessageKey = ManualBlockErrorCodes.PropertyNotFoundMessageKey,
            };
        }

        return [.. ids.Select(id => found.First(p => p.Id == id))];
    }

    private async Task<Dictionary<Guid, IReadOnlyDictionary<DateOnly, decimal>>> ReadSeasonalPricesAsync(
        Guid orgId,
        IReadOnlyList<Guid> ids,
        DateOnly from,
        DateOnly toExclusive,
        CancellationToken ct)
    {
        var rows = await db.SeasonalPriceSuggestions
            .AsNoTracking()
            .Where(s => s.OrgId == orgId && ids.Contains(s.PropertyId) && s.AppliedPrice != null && s.StayDate >= from && s.StayDate < toExclusive)
            .Select(s => new { s.PropertyId, s.StayDate, Price = s.AppliedPrice!.Value })
            .ToListAsync(ct);

        var result = new Dictionary<Guid, IReadOnlyDictionary<DateOnly, decimal>>();
        foreach (var group in rows.GroupBy(row => row.PropertyId))
            result[group.Key] = group.ToDictionary(row => row.StayDate, row => row.Price);

        return result;
    }

    private async Task<Reach> ReachOfAsync(Guid propertyId, CancellationToken budget, CancellationToken ct)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return Reach.NotMirrored;

        ct.ThrowIfCancellationRequested();
        if (budget.IsCancellationRequested)
            return Reach.Mirrored;

        try
        {
            var lookup = await targets.LookupAsync(propertyId, budget);
            return lookup.Gap switch
            {
                OctorateTargetGap.None => Reach.Mirrored,
                OctorateTargetGap.RoomNotChosen => Reach.RoomNotChosen,
                _ => Reach.NotMirrored,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "The channel manager lookups of the preview took more than {Seconds} seconds: the rest are not asked and the properties are taken as mirrored",
                _budget.TotalSeconds);
            return Reach.Mirrored;
        }
        catch (OctorateException ex)
        {
            logger.LogWarning(
                "The channel manager could not be asked about property {PropertyId} ({Code}, HTTP {Status}): the preview takes it as mirrored",
                propertyId,
                ex.Code,
                ex.OctorateStatusCode);
            return Reach.Mirrored;
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "The channel manager cannot be asked about property {PropertyId}: the preview takes it as not mirrored", propertyId);
            return Reach.NotMirrored;
        }
    }

    private static string SummaryOf(CalendarChangeRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (request.DaysOfWeek is { } weekdays)
            {
                writer.WriteStartArray("daysOfWeek");
                foreach (var weekday in weekdays.OrderBy(day => (int)day))
                    writer.WriteStringValue(weekday.ToString());

                writer.WriteEndArray();
            }

            var patch = request.Patch;
            Write(writer, "price", patch.Price, (w, value) => w.WriteNumberValue(value));
            Write(writer, "minNights", patch.MinNights, (w, value) => w.WriteNumberValue(value));
            Write(writer, "maxNights", patch.MaxNights, (w, value) => w.WriteNumberValue(value));
            Write(writer, "closedToArrival", patch.ClosedToArrival, (w, value) => w.WriteBooleanValue(value));
            Write(writer, "closedToDeparture", patch.ClosedToDeparture, (w, value) => w.WriteBooleanValue(value));
            Write(writer, "minAdvanceDays", patch.MinAdvanceDays, (w, value) => w.WriteNumberValue(value));
            Write(writer, "maxAdvanceDays", patch.MaxAdvanceDays, (w, value) => w.WriteNumberValue(value));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write<T>(Utf8JsonWriter writer, string name, FieldPatch<T> patch, Action<Utf8JsonWriter, T> writeValue)
        where T : struct
    {
        if (patch.Kind == FieldPatchKind.Keep)
            return;

        writer.WriteStartObject(name);
        writer.WriteString("action", patch.Kind.ToString());
        if (patch.Kind == FieldPatchKind.Set)
        {
            writer.WritePropertyName("value");
            writeValue(writer, patch.Value);
        }

        writer.WriteEndObject();
    }

    private static DateTime Utc(DateOnly day) => day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
}
