using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The 24 hours of the notification of the guests of a Ticino property to the cantonal police (E30/F02/US01/T01, NF-127, D9, casazen/backend#979): the
/// third kind of alert of <see cref="ComplianceAlertService"/>, run by the same hourly <c>stay-alerts</c> job behind <c>Features:TicinoNotificationAlerts</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Which days.</b> The arrival days still to notify of the Ticino properties of every org, from
/// <see cref="ITicinoNotificationService.ListPendingDaysAsync"/> only: the rule of a closed day (a receipt of the portal and, when it exists, the
/// declaration) lives there and is never read again here, so a day that closes stops its alerts. A day that closed between the list and the send is told
/// by the detail of the day, read from the same service before a stage is claimed.</item>
/// <item><b>Which stage.</b> <see cref="ComplianceAlertSchedule.DueTicinoStep"/> on the clock of the property (<c>Property.Timezone</c>) and the first expected
/// check-in of the day (the earliest <c>ExpectedArrivalTime</c> of its stays that owe a notification: the same stays the service counts, an active status and
/// no no-show). The state is the one of the other alerts (<see cref="ComplianceAlertState"/>: the property, the type and the arrival day, <c>yyyy-MM-dd</c>),
/// claimed and delivered by the same code, so each stage is sent once whatever the number of runs.</item>
/// <item><b>What it says.</b> The property, the arrival day, the deadline and what the data of the day miss as <i>types of field</i>
/// (<c>TicinoMissing</c> and the structure identifier): never the name of a guest, a value or a document. A day with no complete row still alerts.</item>
/// </list>
/// </remarks>
public sealed partial class ComplianceAlertService
{
    /// <summary>The code of the 422 <see cref="ITicinoNotificationService"/> answers for a property that is not in canton Ticino.</summary>
    private const string PropertyNotInTicinoCode = "property_not_in_ticino";

    private async Task<int> SendTicinoAlertsAsync(DateTime now, CancellationToken ct)
    {
        // The flag is on but the service of the days is not there (a host that builds this service without it): nothing can be read, and it is said, not silent.
        if (ticinoNotifications is null)
        {
            logger.LogWarning("Ticino notification alerts skipped: Features:TicinoNotificationAlerts is on but the service of the days to notify is not registered");
            return 0;
        }

        // Background run: no tenant context, every org (null, null). Each day names its own org, and every later read states it. Like the first read of the period
        // alerts, a failure here is not swallowed: it fails the run, which Hangfire retries and the dashboard shows.
        var days = await ticinoNotifications.ListPendingDaysAsync(null, null, _clock.TodayInRomeAsDateOnly(), ct);
        if (days.Count == 0)
            return 0;

        var propertyIds = days.Select(d => d.PropertyId).Distinct().ToList();
        var zones = await TicinoZonesAsync(propertyIds, ct);
        var expected = await TicinoExpectedArrivalsAsync(propertyIds, days.Min(d => d.Day), days.Max(d => d.Day), ct);
        var stages = await TicinoStagesAsync(propertyIds, ct);

        var sent = 0;
        foreach (var day in days.OrderBy(d => d.Day).ThenBy(d => d.PropertyId))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var step = ComplianceAlertSchedule.DueTicinoStep(
                    day.PropertyId,
                    day.Day,
                    day.DueAtUtc,
                    zones.GetValueOrDefault(day.PropertyId, RomeCalendar.TimeZone),
                    expected.TryGetValue((day.PropertyId, day.Day), out var firstExpected) ? firstExpected : null,
                    options.Value,
                    now);
                var current = stages.GetValueOrDefault((day.PropertyId, ComplianceAlertSchedule.TicinoReferenceKey(day.Day)));
                if (step is null || step.Stage <= current)
                    continue;

                if (await WithTicinoMissingAsync(step, day, ct) is { } complete)
                    sent += await DeliverAsync(day.OrgId, complete, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One property or day that cannot be processed never stops the others; its stage stays unclaimed when the failure came before the claim, and
                // the next run tries again.
                logger.LogError(ex, "Ticino notification alert of property {PropertyId} for {Day} could not be processed", day.PropertyId, day.Day);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        logger.LogInformation("Ticino notification alerts run completed: {Days} days to notify, {Sent} alerts sent", days.Count, sent);
        return sent;
    }

    /// <summary>
    /// <paramref name="step"/> with what the data of the day miss (types of field only), read from the detail of the day of
    /// <see cref="ITicinoNotificationService.GetDayAsync"/>; <c>null</c> when there is nothing to alert any more: the property is not in Ticino (or is gone) since
    /// the days were read, or the day closed meanwhile (the detail lists the days still to notify of the property through the same rule as the list).
    /// </summary>
    private async Task<ComplianceAlertStep?> WithTicinoMissingAsync(ComplianceAlertStep step, TicinoPendingDay day, CancellationToken ct)
    {
        TicinoNotificationDay detail;
        try
        {
            detail = await ticinoNotifications!.GetDayAsync(day.PropertyId, day.Day, ct);
        }
        catch (Exception ex) when (ex is NotFoundException or DomainRuleException { Code: PropertyNotInTicinoCode })
        {
            logger.LogInformation("Ticino notification alert of property {PropertyId} for {Day} not sent: it is no longer a property of Ticino", day.PropertyId, day.Day);
            return null;
        }

        if (detail.PendingDays.All(p => p.Day != day.Day))
            return null;

        var missing = detail.Plan.Issues
            .SelectMany(issue => issue.Missing)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (!TicinoStructureId.IsValid(detail.StructureId))
            missing.Insert(0, TicinoAlertMissing.StructureId);

        return step with { Alert = step.Alert with { MissingFields = missing } };
    }

    /// <summary>The clock of each property: its <c>Timezone</c>, and Europe/Rome (the same rules as Europe/Zurich) for one the machine does not know.</summary>
    private async Task<Dictionary<Guid, TimeZoneInfo>> TicinoZonesAsync(IReadOnlyCollection<Guid> propertyIds, CancellationToken ct)
    {
        var ids = propertyIds.ToList();
        var rows = await db.Properties
            .AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Timezone })
            .ToListAsync(ct);
        return rows.ToDictionary(row => row.Id, row => RomeCalendar.ZoneOf(row.Timezone));
    }

    /// <summary>
    /// The earliest expected check-in of each arrival day of these properties (<see cref="ComplianceAlertSchedule.TicinoFirstExpectedArrival"/>), from the stays
    /// that owe a notification: the one definition of them (an active status and no no-show mark, <see cref="AlloggiatiStatusRules.ActiveBookingStatuses"/>).
    /// A day without an expected time is not in the answer.
    /// </summary>
    private async Task<Dictionary<(Guid PropertyId, DateOnly Day), TimeOnly>> TicinoExpectedArrivalsAsync(
        IReadOnlyCollection<Guid> propertyIds,
        DateOnly firstDay,
        DateOnly lastDay,
        CancellationToken ct)
    {
        var ids = propertyIds.ToList();
        var from = StoredDay(firstDay);
        var to = StoredDay(lastDay.AddDays(1));
        var stays = await db.Bookings
            .AsNoTracking()
            .Where(b => ids.Contains(b.PropertyId)
                        && AlloggiatiStatusRules.ActiveBookingStatuses.Contains(b.Status)
                        && b.NoShowAt == null
                        && b.CheckInDate >= from
                        && b.CheckInDate < to
                        && b.ExpectedArrivalTime != null)
            .Select(b => new { b.PropertyId, b.CheckInDate, b.ExpectedArrivalTime })
            .ToListAsync(ct);

        var result = new Dictionary<(Guid, DateOnly), TimeOnly>();
        foreach (var group in stays.GroupBy(s => (s.PropertyId, Day: DateOnly.FromDateTime(s.CheckInDate))))
        {
            if (ComplianceAlertSchedule.TicinoFirstExpectedArrival(group.Select(s => s.ExpectedArrivalTime)) is { } first)
                result[group.Key] = first;
        }

        return result;
    }

    /// <summary>The last stage sent for each arrival day of these properties (the state rows of <see cref="ComplianceAlertType.TicinoNotification"/>).</summary>
    private async Task<Dictionary<(Guid PropertyId, string Day), int>> TicinoStagesAsync(IReadOnlyCollection<Guid> propertyIds, CancellationToken ct)
    {
        var ids = propertyIds.ToList();
        var rows = await db.ComplianceAlertStates
            .AsNoTracking()
            .Where(s => s.Type == ComplianceAlertType.TicinoNotification && ids.Contains(s.PropertyId))
            .Select(s => new { s.PropertyId, s.ReferenceKey, s.Stage })
            .ToListAsync(ct);
        return rows.ToDictionary(row => (row.PropertyId, row.ReferenceKey), row => row.Stage);
    }

    /// <summary>A calendar day as the bookings store it: midnight UTC of that day.</summary>
    private static DateTime StoredDay(DateOnly day) => DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
}
