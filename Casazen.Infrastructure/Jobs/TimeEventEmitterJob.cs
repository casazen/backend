using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// Writes the time triggers of the stays into the outbox (ADR-022-R8): "2 days before check-in", "3 hours after check-out", one
/// <see cref="DomainEventType.TimeTrigger"/> event per booking, kind and offset, when it becomes due. The dispatcher hands them to
/// the routine engine like any other event.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Only what a routine asks for</b>: the offsets come from the active routines of each org whose event type is a stay
/// trigger (<c>BeforeCheckin</c>, <c>AfterCheckin</c>, <c>BeforeCheckout</c>, <c>AfterCheckout</c>), in whole hours
/// (<see cref="TimeTrigger.OffsetHours"/>); an org without such a routine gets nothing.</item>
/// <item><b>Only the stays that happen</b>: the bookings of <see cref="AlloggiatiStatusRules.ActiveBookingStatuses"/> (<c>Confirmed</c>,
/// <c>CheckedIn</c> or <c>CheckedOut</c>: an after-check-out trigger is about a stay already checked out) that are not marked as a no-show, the one
/// definition of a stay that is due (<see cref="AlloggiatiStatusRules.IsDue"/>); a pending, cancelled or no-show booking emits nothing.</item>
/// <item><b>When it is due</b>: a check-in or check-out date is a calendar date stored as midnight UTC, and the trigger is due at the
/// time of that day on the clock of the property (<c>Property.CheckInFrom</c> for a check-in, <c>Property.CheckOutBy</c> for a check-out, in
/// <c>Property.Timezone</c>, <see cref="RomeCalendar.ToUtc(DateOnly, TimeOnly, TimeZoneInfo)"/>) shifted by the offset: "2 hours before
/// check-in" fires two hours before guests may arrive. A property with no such time, or one the job cannot find, counts the start of the
/// day in its zone (Rome when it has none or the machine does not know it). A run emits the
/// triggers due in (now - look-back, now]; the look-back is <see cref="CatchUpWindow"/>, or two evaluation intervals when that is
/// longer, so a missed run or a short downtime is caught up, while a trigger due longer ago (a booking confirmed after it, a routine
/// created after it) is not emitted late.</item>
/// <item><b>Never twice</b>: the dedup key names the due hour (<see cref="TimeTrigger.DedupKey"/>); a run skips the keys already
/// written, and the unique index of the org refuses a key another run wrote meanwhile.</item>
/// <item><b>Behind <c>Features:Routines</c></b> (E01/F04/US01/T03): with the module closed a run writes nothing.</item>
/// <item>A system job over every org (<c>IgnoreQueryFilters([TenantQueryFilter])</c>): each event carries the org of its booking.
/// The payload holds ids, the kind and the offset, no personal data (ADR-022-R21).</item>
/// </list>
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 60)]
public class TimeEventEmitterJob(
    AppDbContext db,
    IDomainEventRecorder recorder,
    IOptions<RoutineOptions> options,
    IFeatureFlags featureFlags,
    TimeProvider timeProvider,
    ILogger<TimeEventEmitterJob> logger)
{
    public const string RecurringJobId = "time-event-emitter";

    /// <summary>The least a run looks back for the triggers that became due.</summary>
    public static readonly TimeSpan CatchUpWindow = TimeSpan.FromHours(1);

    private static readonly RoutineEventType[] StayEventTypes = [.. TimeTrigger.StayKinds.Keys];

    // Every run is followed by the next one in a few minutes: a failed run is not retried, the next one catches up.
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // The module is closed (Features:Routines): nothing is written to the outbox, whatever routines the orgs have saved.
        if (!featureFlags.IsEnabled(FeatureFlags.Routines))
        {
            logger.LogDebug("Routines are off (Features:Routines): no time trigger is written");
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var twoIntervals = TimeSpan.FromMinutes(2 * options.Value.EffectiveEvaluationIntervalMinutes);
        var windowStart = now - (twoIntervals > CatchUpWindow ? twoIntervals : CatchUpWindow);

        var routines = await db.Routines
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(r => r.Active && StayEventTypes.Contains(r.EventType))
            .Select(r => new { r.Id, r.OrgId, r.EventType, r.EventConfig })
            .ToListAsync(cancellationToken);

        var emitted = 0;
        var skipped = 0;
        foreach (var org in routines.GroupBy(r => r.OrgId))
        {
            var points = new HashSet<TriggerPoint>();
            foreach (var routine in org)
            {
                if (TimeTrigger.OffsetHours(routine.EventConfig) is { } offset)
                    points.Add(new TriggerPoint(TimeTrigger.StayKinds[routine.EventType], offset));
                else
                    logger.LogWarning("Routine {RoutineId} of org {OrgId} has no valid offset in its configuration: no time trigger", routine.Id, org.Key);
            }

            if (points.Count == 0)
                continue;

            var due = await DueTriggersAsync(org.Key, points, windowStart, now, cancellationToken);
            var (orgEmitted, orgSkipped) = await EmitAsync(org.Key, due, cancellationToken);
            emitted += orgEmitted;
            skipped += orgSkipped;
        }

        logger.LogInformation(
            "Time triggers from {WindowStart:O} to {Now:O}: {Emitted} emitted, {Skipped} already emitted",
            windowStart,
            now,
            emitted,
            skipped);
    }

    /// <summary>The triggers of <paramref name="orgId"/> due in (<paramref name="windowStart"/>, <paramref name="now"/>].</summary>
    private async Task<List<DueTrigger>> DueTriggersAsync(
        Guid orgId,
        HashSet<TriggerPoint> points,
        DateTime windowStart,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var checkIn = DatesToRead(points.Where(p => TimeTrigger.IsCheckIn(p.Kind)), windowStart, now);
        var checkOut = DatesToRead(points.Where(p => !TimeTrigger.IsCheckIn(p.Kind)), windowStart, now);

        var query = db.Bookings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(b => b.OrgId == orgId && AlloggiatiStatusRules.ActiveBookingStatuses.Contains(b.Status) && b.NoShowAt == null);
        query = (checkIn, checkOut) switch
        {
            ({ } i, { } o) => query.Where(b =>
                (b.CheckInDate >= i.From && b.CheckInDate <= i.To) || (b.CheckOutDate >= o.From && b.CheckOutDate <= o.To)),
            ({ } i, null) => query.Where(b => b.CheckInDate >= i.From && b.CheckInDate <= i.To),
            (null, { } o) => query.Where(b => b.CheckOutDate >= o.From && b.CheckOutDate <= o.To),
            _ => query.Where(b => false),
        };

        var bookings = await query
            .Select(b => new { b.Id, b.PropertyId, b.CheckInDate, b.CheckOutDate })
            .ToListAsync(cancellationToken);

        // The clock of each property: when guests may arrive and when they must leave, in its own zone.
        var propertyIds = bookings.Select(b => b.PropertyId).Distinct().ToList();
        var clocks = propertyIds.Count == 0
            ? new Dictionary<Guid, PropertyClock>()
            : await db.Properties
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AsNoTracking()
                .Where(p => p.OrgId == orgId && propertyIds.Contains(p.Id))
                .Select(p => new PropertyClock(p.Id, p.CheckInFrom, p.CheckOutBy, p.Timezone))
                .ToDictionaryAsync(clock => clock.PropertyId, cancellationToken);

        var due = new List<DueTrigger>();
        foreach (var booking in bookings)
        {
            clocks.TryGetValue(booking.PropertyId, out var clock);
            var zone = RomeCalendar.ZoneOf(clock?.Timezone);
            foreach (var point in points)
            {
                var isCheckIn = TimeTrigger.IsCheckIn(point.Kind);
                var date = DateOnly.FromDateTime(isCheckIn ? booking.CheckInDate : booking.CheckOutDate);
                var time = (isCheckIn ? clock?.CheckInFrom : clock?.CheckOutBy) ?? TimeOnly.MinValue;
                var dueAt = RomeCalendar.ToUtc(date, time, zone).AddHours(TimeTrigger.IsBefore(point.Kind) ? -point.Offset : point.Offset);
                if (dueAt > windowStart && dueAt <= now)
                    due.Add(new DueTrigger(booking.Id, booking.PropertyId, point.Kind, point.Offset, dueAt));
            }
        }

        return due;
    }

    /// <summary>
    /// The stored dates whose trigger can be due in the window. The time of a date on the clock of a property is up to 14 hours before and up
    /// to 36 hours after its stored midnight UTC (a zone from UTC+14 to UTC-12, a time of the day up to 23:59), so the range is two days wider
    /// before and one day wider after; the exact due time is checked on each booking.
    /// </summary>
    private static DateWindow? DatesToRead(IEnumerable<TriggerPoint> points, DateTime windowStart, DateTime now)
    {
        DateWindow? dates = null;
        foreach (var point in points)
        {
            var shift = TimeSpan.FromHours(TimeTrigger.IsBefore(point.Kind) ? -point.Offset : point.Offset);
            var from = (windowStart - shift).AddDays(-2);
            var to = (now - shift).AddDays(1);
            dates = dates is { } known
                ? new DateWindow(from < known.From ? from : known.From, to > known.To ? to : known.To)
                : new DateWindow(from, to);
        }

        return dates;
    }

    /// <summary>Writes the triggers not written yet, one save each: a key another run wrote meanwhile is refused and skipped.</summary>
    private async Task<(int Emitted, int Skipped)> EmitAsync(Guid orgId, List<DueTrigger> due, CancellationToken cancellationToken)
    {
        if (due.Count == 0)
            return (0, 0);

        var keys = due.Select(t => TimeTrigger.DedupKey(t.Kind, t.Offset, t.BookingId, t.DueAt)).ToList();
        var written = await db.OutboxEvents
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(e => e.OrgId == orgId && e.DedupKey != null && keys.Contains(e.DedupKey))
            .Select(e => e.DedupKey!)
            .ToListAsync(cancellationToken);
        var known = written.ToHashSet(StringComparer.Ordinal);

        var emitted = 0;
        var skipped = 0;
        for (var i = 0; i < due.Count; i++)
        {
            var trigger = due[i];
            if (!known.Add(keys[i]))
            {
                skipped++;
                continue;
            }

            recorder.Record(
                DomainEventType.TimeTrigger,
                trigger.BookingId,
                new TimeTriggerPayload(trigger.Kind, trigger.Offset, trigger.BookingId, trigger.BookingId, trigger.PropertyId),
                keys[i],
                orgId);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                emitted++;
            }
            catch (DbUpdateException ex) when (IsDedupConflict(ex))
            {
                // Another run wrote the same trigger meanwhile: the database kept the first one.
                foreach (var entry in db.ChangeTracker.Entries<OutboxEvent>().Where(e => e.State == EntityState.Added).ToList())
                    entry.State = EntityState.Detached;
                skipped++;
            }
        }

        return (emitted, skipped);
    }

    private static bool IsDedupConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: OutboxEvent.DedupIndexName };

    private readonly record struct TriggerPoint(string Kind, int Offset);

    private readonly record struct DateWindow(DateTime From, DateTime To);

    private sealed record PropertyClock(Guid PropertyId, TimeOnly? CheckInFrom, TimeOnly? CheckOutBy, string? Timezone);

    private sealed record DueTrigger(Guid BookingId, Guid PropertyId, string Kind, int Offset, DateTime DueAt);
}
