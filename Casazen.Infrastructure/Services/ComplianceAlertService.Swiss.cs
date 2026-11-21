using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The notification of the guests of a property in a canton other than Ticino to the cantonal authority (E31/F02/US01/T01, NF-131, casazen/backend#979): the fourth kind of alert of
/// <see cref="ComplianceAlertService"/>, run by the same hourly <c>stay-alerts</c> job behind <c>Features:TicinoNotificationAlerts</c>, and only for the days the service of the assisted
/// notification lists, which needs <c>Features:GuestRegistrationRegimes</c> on, a canton the assisted notification covers and a duty that is <c>Required</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Which days.</b> The arrival days still to notify, from <see cref="ISwissNotificationService.ListPendingDaysAsync"/> only: the rule of a closed day (a declaration of the property, the day and
/// the regime) lives there and is never read again here, so a day that closes stops its alerts. A day that closed between the list and the send is told by the detail of the day, read from the same
/// service before a stage is claimed. A day whose regime has no deadline is not alerted: a deadline is never guessed (the cockpit lists it all the same).</item>
/// <item><b>Which stage.</b> <see cref="ComplianceAlertSchedule.DueSwissStep"/>, which is the Ticino sequence against the deadline of the regime, on the clock of the property and with the first
/// expected check-in of the day (the same stays the service counts). The state is the one of the other alerts (<see cref="ComplianceAlertState"/>: the property, the type
/// <see cref="ComplianceAlertType.SwissNotification"/> and the arrival day), claimed and delivered by the same code, so each stage is sent once whatever the number of runs.</item>
/// <item><b>What it says.</b> The property, the arrival day, the deadline and what the data of the day miss as <i>types of field</i>: never the name of a guest, a value or a document. A day with
/// no complete guest still alerts.</item>
/// </list>
/// </remarks>
public sealed partial class ComplianceAlertService
{
    private async Task<int> SendSwissAlertsAsync(DateTime now, CancellationToken ct)
    {
        // The flag is on but the service of the days is not there (a host that builds this service without it: the application registers it, a test checks it): nothing can be read, and it is
        // said, not silent. Told at information level, not as a warning: the service is an optional dependency added after the Ticino one, and a host that only wires the Ticino days is not broken.
        if (swissNotifications is null)
        {
            logger.LogInformation("Swiss notification alerts skipped: Features:TicinoNotificationAlerts is on but the service of the days to notify of the other cantons is not registered");
            return 0;
        }

        // Background run: no tenant context, every org (null, null). Each day names its own org, and every later read states it. Like the first read of the other alerts, a failure here is not
        // swallowed: it fails the run, which Hangfire retries and the dashboard shows. A regime with no deadline has no alert.
        var days = (await swissNotifications.ListPendingDaysAsync(null, null, _clock.TodayInRomeAsDateOnly(), ct)).Where(d => d.DueAtUtc is not null).ToList();
        if (days.Count == 0)
            return 0;

        var propertyIds = days.Select(d => d.PropertyId).Distinct().ToList();
        var zones = await TicinoZonesAsync(propertyIds, ct);
        var expected = await TicinoExpectedArrivalsAsync(propertyIds, days.Min(d => d.Day), days.Max(d => d.Day), ct);
        var stages = await SwissStagesAsync(propertyIds, ct);

        var sent = 0;
        foreach (var day in days.OrderBy(d => d.Day).ThenBy(d => d.PropertyId))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var step = ComplianceAlertSchedule.DueSwissStep(
                    day.PropertyId,
                    day.Day,
                    day.DueAtUtc!.Value,
                    zones.GetValueOrDefault(day.PropertyId, RomeCalendar.TimeZone),
                    expected.TryGetValue((day.PropertyId, day.Day), out var firstExpected) ? firstExpected : null,
                    options.Value,
                    now);
                var current = stages.GetValueOrDefault((day.PropertyId, ComplianceAlertSchedule.SwissReferenceKey(day.Day)));
                if (step is null || step.Stage <= current)
                    continue;

                if (await WithSwissMissingAsync(step, day, ct) is { } complete)
                    sent += await DeliverAsync(day.OrgId, complete, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One property or day that cannot be processed never stops the others; its stage stays unclaimed when the failure came before the claim, and the next run tries again.
                logger.LogError(ex, "Swiss notification alert of property {PropertyId} for {Day} could not be processed", day.PropertyId, day.Day);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        logger.LogInformation("Swiss notification alerts run completed: {Days} days to notify, {Sent} alerts sent", days.Count, sent);
        return sent;
    }

    /// <summary>
    /// <paramref name="step"/> with what the data of the day miss (types of field only), read from the detail of the day of <see cref="ISwissNotificationService.GetDayAsync"/>; <c>null</c> when there
    /// is nothing to alert any more: the property is gone, or is not one the assisted notification covers any more (its canton or its municipality changed) since the days were read, or the day closed
    /// meanwhile (the detail lists the days still to notify of the property through the same rule as the list).
    /// </summary>
    private async Task<ComplianceAlertStep?> WithSwissMissingAsync(ComplianceAlertStep step, SwissPendingDay day, CancellationToken ct)
    {
        SwissNotificationDay detail;
        try
        {
            detail = await swissNotifications!.GetDayAsync(day.PropertyId, day.Day, ct);
        }
        catch (Exception ex) when (ex is NotFoundException or DomainRuleException { Code: SwissNotificationErrorCodes.NotApplicable })
        {
            logger.LogInformation("Swiss notification alert of property {PropertyId} for {Day} not sent: it is no longer a property the assisted notification covers", day.PropertyId, day.Day);
            return null;
        }

        // The day is still to notify under the regime that raised it: a day that closed, or a property that changed canton, is not alerted.
        if (detail.PendingDays.All(p => p.Day != day.Day || p.RegimeCode != day.RegimeCode))
            return null;

        var missing = detail.Issues
            .SelectMany(issue => issue.Missing)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return step with { Alert = step.Alert with { MissingFields = missing } };
    }

    /// <summary>The last stage sent for each arrival day of these properties (the state rows of <see cref="ComplianceAlertType.SwissNotification"/>).</summary>
    private async Task<Dictionary<(Guid PropertyId, string Day), int>> SwissStagesAsync(IReadOnlyCollection<Guid> propertyIds, CancellationToken ct)
    {
        var ids = propertyIds.ToList();
        var rows = await db.ComplianceAlertStates
            .AsNoTracking()
            .Where(s => s.Type == ComplianceAlertType.SwissNotification && ids.Contains(s.PropertyId))
            .Select(s => new { s.PropertyId, s.ReferenceKey, s.Stage })
            .ToListAsync(ct);
        return rows.ToDictionary(row => (row.PropertyId, row.ReferenceKey), row => row.Stage);
    }
}
