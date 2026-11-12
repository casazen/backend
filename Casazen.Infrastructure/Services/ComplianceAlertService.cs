using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IComplianceAlertService" />
/// <remarks>
/// <list type="bullet">
/// <item><b>Periods</b> (ISTAT, tourist tax): for every org with an active short-rent property the compliance status engine is asked for the closed months
/// of the last <see cref="PeriodLookbackMonths"/> months (<see cref="IComplianceStatusService.EvaluateAsync(HostScope, ComplianceStatusWindow, CancellationToken)"/>:
/// no read of Octorate, the same rules as the traffic light). A month whose item is not green and whose deadline day is known gets the stages of
/// <see cref="ComplianceAlertSchedule.DuePeriodStep"/>. The ISTAT deadline day is the one the engine used (<c>Compliance:IstatDeadlineDays</c>, a region
/// that is not there is «to define» and silent); the tourist tax asks <see cref="ITouristTaxDeadlineSource"/>, which answers «not known» today, so it
/// stays silent: a deadline is never guessed.</item>
/// <item><b>Connection</b>: a property whose channel manager is the sender (only while <c>Features:OctorateConnector</c> is on, the status engine's
/// rule) and that <see cref="IComplianceConnectionHealthService"/> reads as <see cref="ConnectionHealthState.Inactive"/> while a stay arrives within
/// <see cref="ComplianceOptions.ConnectionAlertDays"/> days gets the two stages of <see cref="ComplianceAlertSchedule.DueConnectionStep"/>. A
/// property that was not read this time (the health service reads a few Octorate accounts per call) is neither alerted nor cleared; when a reading
/// says the send is active again (or nobody sends automatically any more) the state is reset, so a later incident is alerted again, and its "To do"
/// is closed.</item>
/// <item><b>Once.</b> Each stage is claimed before it is sent with a compare-and-set on <see cref="ComplianceAlertState"/> (unique per property, type and
/// reference): it is delivered at most once, with retries, a manual trigger or two runs at once. A delivery that fails after the claim is logged and not
/// repeated.</item>
/// <item><b>Behind the module's flag.</b> With <c>Features:ComplianceTrafficLight</c> off the run does nothing and writes nothing.</item>
/// <item><b>Leaves behind</b> (<see cref="IComplianceAlertFollowUp"/>): the "To do" of the condition (the same item the feed of the cockpit raises) and,
/// for ISTAT, a popup alert when the deadline is within 24 hours; the "To do" of a period is closed when the engine says it is green or gone.</item>
/// <item>Logs carry ids, types and counts: no name, no address, no text.</item>
/// </list>
/// </remarks>
public sealed class ComplianceAlertService(
    AppDbContext db,
    IComplianceStatusService complianceStatus,
    IComplianceConnectionHealthService connectionHealth,
    IPropertySenderStore senders,
    IComplianceAlertRecipientResolver recipients,
    ITouristTaxDeadlineSource touristTaxDeadlines,
    INotificationService notificationService,
    IComplianceAlertFollowUp followUp,
    IFeatureFlags featureFlags,
    IOptions<ComplianceOptions> options,
    ILogger<ComplianceAlertService> logger,
    TimeProvider? timeProvider = null) : IComplianceAlertService
{
    /// <summary>
    /// The closed months looked at for the periods: the one just over and the one before it, because a deadline day late in the month plus the
    /// reminders can still be running into the next one.
    /// </summary>
    internal const int PeriodLookbackMonths = 2;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ComplianceAlertRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.ComplianceTrafficLight))
        {
            logger.LogDebug("Compliance alerts skipped: Features:ComplianceTrafficLight is off");
            return new ComplianceAlertRunResult(Skipped: true, Sent: 0);
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var sent = await SendPeriodAlertsAsync(now, cancellationToken)
            + await SendConnectionAlertsAsync(now, cancellationToken);

        logger.LogInformation("Compliance alerts run completed: {Sent} alerts sent", sent);
        return new ComplianceAlertRunResult(Skipped: false, Sent: sent);
    }

    // ─── Periods ───

    private async Task<int> SendPeriodAlertsAsync(DateTime now, CancellationToken ct)
    {
        var today = _clock.TodayInRomeAsDateOnly();
        var window = new ComplianceStatusWindow(new DateOnly(today.Year, today.Month, 1).AddMonths(-PeriodLookbackMonths), today);

        // Background run: no tenant context, every org's properties.
        var orgIds = await db.Properties
            .AsNoTracking()
            .Where(PropertyRentalModeRules.IsShortRent)
            .Where(p => p.IsActive)
            .Select(p => p.OrgId)
            .Distinct()
            .ToListAsync(ct);

        var sent = 0;
        foreach (var orgId in orgIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                sent += await SendPeriodAlertsOfOrgAsync(orgId, window, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Period alerts of org {OrgId} could not be processed", orgId);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        return sent;
    }

    private async Task<int> SendPeriodAlertsOfOrgAsync(Guid orgId, ComplianceStatusWindow window, DateTime now, CancellationToken ct)
    {
        var settings = options.Value;
        var evaluation = await complianceStatus.EvaluateAsync(new HostScope(orgId), window, ct);

        // The periods that still owe a proof: the engine's verdict is not green. Green, or no item at all, is a period settled or owing nothing.
        var open = evaluation.Items
            .Where(i => i.Kind is ComplianceItemKind.Istat or ComplianceItemKind.TouristTax
                        && i.Period is not null
                        && i.Verdict.NeedsAttention)
            .ToList();

        var states = await PeriodStatesAsync(orgId, open.Select(i => i.PropertyId).Distinct().ToList(), ct);
        var sent = 0;
        foreach (var item in open)
        {
            var type = item.Kind == ComplianceItemKind.Istat ? ComplianceAlertType.IstatPeriod : ComplianceAlertType.TouristTaxPeriod;
            var period = item.Period!;

            // The last day of the period: the ISTAT one is the engine's own (a region without it has no item), the tourist tax one comes from a
            // source that does not exist yet. A day that is not known is never guessed: nothing is sent.
            var deadlineDay = type == ComplianceAlertType.IstatPeriod
                ? item.DueAt is { } due ? RomeCalendar.DateInRome(due) : (DateOnly?)null
                : await touristTaxDeadlines.DeadlineOfAsync(item.PropertyId, StartOfPeriod(period), ct);
            if (deadlineDay is not { } day)
                continue;

            var step = ComplianceAlertSchedule.DuePeriodStep(type, item.PropertyId, period, day, settings, now);
            var current = states.GetValueOrDefault((item.PropertyId, type, period))?.Stage ?? 0;
            if (step is null || step.Stage <= current)
                continue;

            sent += await DeliverAsync(orgId, step, now, ct);
        }

        await CloseClearedPeriodTodosAsync(evaluation, open, ct);
        return sent;
    }

    /// <summary>
    /// The "To do" of a period that is settled or owes nothing any more: for every month the engine looked at, the properties it looked at that have no
    /// open item for it. The "To do" of an item that still needs a person (yellow, red) is never touched here.
    /// </summary>
    private async Task CloseClearedPeriodTodosAsync(ComplianceStatusResult evaluation, IReadOnlyList<ComplianceStatusItem> open, CancellationToken ct)
    {
        foreach (var period in evaluation.Evaluated.Periods)
        {
            var entityType = ComplianceActivityFeeder.PeriodEntityTypePrefix + period;
            foreach (var (kind, type) in new[]
                     {
                         (ComplianceItemKind.Istat, ActivityType.IstatDeadline),
                         (ComplianceItemKind.TouristTax, ActivityType.TouristTaxDeadline),
                     })
            {
                var stillOpen = open
                    .Where(i => i.Kind == kind && string.Equals(i.Period, period, StringComparison.Ordinal))
                    .Select(i => i.PropertyId)
                    .ToHashSet();
                var cleared = evaluation.Evaluated.PropertyIds.Where(id => !stillOpen.Contains(id)).ToList();
                if (cleared.Count == 0)
                    continue;

                try
                {
                    await followUp.CloseTodosAsync([type], entityType, cleared, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "The To do of {Type} of {Period} could not be closed", type, period);
                }
            }
        }
    }

    private async Task<Dictionary<(Guid PropertyId, ComplianceAlertType Type, string Period), ComplianceAlertState>> PeriodStatesAsync(
        Guid orgId,
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken ct)
    {
        if (propertyIds.Count == 0)
            return [];

        var ids = propertyIds.ToList();
        var rows = await db.ComplianceAlertStates
            .AsNoTracking()
            .Where(s => s.OrgId == orgId
                        && ids.Contains(s.PropertyId)
                        && (s.Type == ComplianceAlertType.IstatPeriod || s.Type == ComplianceAlertType.TouristTaxPeriod))
            .ToListAsync(ct);
        return rows.ToDictionary(s => (s.PropertyId, s.Type, s.ReferenceKey));
    }

    /// <summary>The first day of the month <c>yyyy-MM</c> of a period alert.</summary>
    private static DateOnly StartOfPeriod(string period) =>
        DateOnly.ParseExact(period + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ─── Connection ───

    private async Task<int> SendConnectionAlertsAsync(DateTime now, CancellationToken ct)
    {
        // The channel manager is a sender only while the connector is on (the rule of the status engine): off, nobody is entrusted and there is
        // nothing to alert or to clear.
        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return 0;

        var settings = options.Value;
        var today = _clock.TodayInRome();
        var lastArrival = today.AddDays(Math.Max(0, settings.ConnectionAlertDays));

        // The properties that have an active stay arriving soon (the one definition of «active»: an active status and no no-show mark)...
        var arriving = await db.Bookings
            .AsNoTracking()
            .Where(b => AlloggiatiStatusRules.ActiveBookingStatuses.Contains(b.Status)
                        && b.NoShowAt == null
                        && b.CheckInDate >= today
                        && b.CheckInDate <= lastArrival)
            .Select(b => new { b.OrgId, b.PropertyId })
            .Distinct()
            .ToListAsync(ct);

        // ...and the ones whose alert is running: they are read too, to see when the connection is active again.
        var running = await db.ComplianceAlertStates
            .AsNoTracking()
            .Where(s => s.Type == ComplianceAlertType.ConnectionInactive && s.Stage > 0)
            .ToListAsync(ct);

        var arrivingIds = arriving.Select(a => a.PropertyId).ToHashSet();
        var orgOf = arriving
            .Select(a => (a.PropertyId, a.OrgId))
            .Concat(running.Select(s => (s.PropertyId, s.OrgId)))
            .DistinctBy(pair => pair.PropertyId)
            .ToDictionary(pair => pair.PropertyId, pair => pair.OrgId);
        if (orgOf.Count == 0)
            return 0;

        var senderOf = await senders.GetManyAsync(orgOf.Keys.ToList(), ct);
        var viaChannelManager = orgOf.Keys
            .Where(id => senderOf.TryGetValue(id, out var info) && info.Applies && info.AlloggiatiSender == ComplianceSenderType.ChannelManager)
            .ToList();

        // Nobody sends automatically (any more): there is no incident to follow, whatever was running.
        var cleared = orgOf.Keys.Where(id => senderOf.ContainsKey(id) && !viaChannelManager.Contains(id)).ToList();

        var health = viaChannelManager.Count == 0
            ? new Dictionary<Guid, ComplianceConnectionHealth>()
            : (await connectionHealth.GetManyAsync(viaChannelManager, ct)).ToDictionary(pair => pair.Key, pair => pair.Value);

        var stateOf = running.ToDictionary(s => s.PropertyId);
        var sent = 0;
        foreach (var propertyId in viaChannelManager.Order())
        {
            ct.ThrowIfCancellationRequested();

            // Not read this time (over the limit of live reads): a placeholder is no reading, so the property is neither alerted nor cleared.
            if (!health.TryGetValue(propertyId, out var reading)
                || reading.Alloggiati.Reason == ComplianceConnectionHealthRules.NotCheckedYetReason)
            {
                continue;
            }

            switch (reading.Alloggiati.State)
            {
                case ConnectionHealthState.Active or ConnectionHealthState.Manual:
                    cleared.Add(propertyId);
                    break;

                case ConnectionHealthState.Inactive when arrivingIds.Contains(propertyId):
                    stateOf.TryGetValue(propertyId, out var state);
                    var step = ComplianceAlertSchedule.DueConnectionStep(propertyId, state?.Stage ?? 0, state?.LastAlertAt, settings, now);
                    if (step is null)
                        break;

                    try
                    {
                        sent += await DeliverAsync(orgOf[propertyId], step, now, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "Connection alert of property {PropertyId} could not be processed", propertyId);
                    }
                    finally
                    {
                        db.ChangeTracker.Clear();
                    }

                    break;
            }
        }

        await ClearConnectionsAsync(cleared.Distinct().ToList(), now, ct);
        return sent;
    }

    /// <summary>
    /// The connection of these properties is active again (or nobody sends automatically any more): the alerts that were running start again from the
    /// first stage for the next incident, and the "To do" about the connection is closed.
    /// </summary>
    private async Task ClearConnectionsAsync(IReadOnlyCollection<Guid> propertyIds, DateTime now, CancellationToken ct)
    {
        if (propertyIds.Count == 0)
            return;

        var ids = propertyIds.ToList();
        var running = db.ComplianceAlertStates
            .Where(s => s.Type == ComplianceAlertType.ConnectionInactive && ids.Contains(s.PropertyId) && s.Stage > 0);
        if (db.Database.IsRelational())
        {
            await running.ExecuteUpdateAsync(
                set => set.SetProperty(s => s.Stage, 0).SetProperty(s => s.UpdatedAt, now),
                ct);
        }
        else
        {
            // Test providers (EF InMemory) have no conditional UPDATE.
            foreach (var state in await running.ToListAsync(ct))
            {
                state.Stage = 0;
                state.UpdatedAt = now;
            }

            await db.SaveChangesAsync(ct);
        }

        try
        {
            await followUp.CloseTodosAsync([ActivityType.ReconnectChannelManager], ComplianceActivityFeeder.PropertyEntityType, ids, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The To do about the connection of {Count} properties could not be closed", ids.Count);
        }
    }

    // ─── Delivery ───

    private async Task<int> DeliverAsync(Guid orgId, ComplianceAlertStep step, DateTime now, CancellationToken ct)
    {
        if (!await ClaimAsync(orgId, step, now, ct))
            return 0;

        var delivered = true;
        try
        {
            var audience = await recipients.ForPropertyAsync(orgId, step.Alert.PropertyId, ct);
            await notificationService.SendComplianceAlertAsync(
                step.Alert,
                audience.Emails,
                PushDeliveryKeys.ComplianceAlert(step.Alert.PropertyId, step.Type, step.ReferenceKey, step.Stage, now),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // At most once: the stage stays claimed, the next run does not send it again.
            delivered = false;
            logger.LogError(
                ex,
                "Compliance alert {Type} (stage {Stage}) of property {PropertyId} claimed but not delivered",
                step.Type,
                step.Stage,
                step.Alert.PropertyId);
        }

        await FollowUpAsync(orgId, step, now, ct);

        if (delivered)
        {
            logger.LogInformation(
                "Compliance alert {Type} (stage {Stage}) sent for property {PropertyId}",
                step.Type,
                step.Stage,
                step.Alert.PropertyId);
        }

        return delivered ? 1 : 0;
    }

    /// <summary>The "To do" of the stage and, for a legal deadline within 24 hours, its popup. A failure is logged and does not undo the stage.</summary>
    private async Task FollowUpAsync(Guid orgId, ComplianceAlertStep step, DateTime now, CancellationToken ct)
    {
        try
        {
            var todo = step.Type == ComplianceAlertType.ConnectionInactive
                ? ComplianceFollowUps.TodoForConnection(step.Alert.PropertyId)
                : ComplianceFollowUps.TodoForPeriod(step);
            if (todo is not null)
                await followUp.OpenTodoAsync(todo, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "To do of compliance alert {Type} (stage {Stage}) of property {PropertyId} could not be raised", step.Type, step.Stage, step.Alert.PropertyId);
        }

        if (step.Type == ComplianceAlertType.ConnectionInactive)
            return;

        try
        {
            await followUp.PopupAsync(ComplianceFollowUps.PopupForPeriod(orgId, step), now, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Popup alert of compliance alert {Type} (stage {Stage}) of property {PropertyId} could not be raised", step.Type, step.Stage, step.Alert.PropertyId);
        }
    }

    /// <summary>
    /// Moves the state of the property for the type and the reference of <paramref name="step"/> to its stage when it is behind, in one conditional
    /// UPDATE: of two concurrent callers only one gets the row. Internal for the tests.
    /// </summary>
    internal async Task<bool> ClaimAsync(Guid orgId, ComplianceAlertStep step, DateTime now, CancellationToken ct)
    {
        var propertyId = step.Alert.PropertyId;
        var exists = await db.ComplianceAlertStates
            .AnyAsync(s => s.PropertyId == propertyId && s.Type == step.Type && s.ReferenceKey == step.ReferenceKey, ct);
        if (!exists)
        {
            var state = new ComplianceAlertState
            {
                OrgId = orgId,
                PropertyId = propertyId,
                Type = step.Type,
                ReferenceKey = step.ReferenceKey,
                Stage = 0,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.ComplianceAlertStates.Add(state);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Created by a concurrent run: the conditional update below decides who sends.
            }
            finally
            {
                db.Entry(state).State = EntityState.Detached;
            }
        }

        var behind = db.ComplianceAlertStates
            .Where(s => s.PropertyId == propertyId && s.Type == step.Type && s.ReferenceKey == step.ReferenceKey && s.Stage < step.Stage);
        if (!db.Database.IsRelational())
        {
            // Test providers (EF InMemory) have no conditional UPDATE: the same compare-and-set, one request at a time.
            var row = await behind.FirstOrDefaultAsync(ct);
            if (row is null)
                return false;

            row.Stage = step.Stage;
            row.AlertCount++;
            row.LastAlertAt = now;
            row.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            return true;
        }

        var claimed = await behind.ExecuteUpdateAsync(
            set => set
                .SetProperty(s => s.Stage, step.Stage)
                .SetProperty(s => s.AlertCount, s => s.AlertCount + 1)
                .SetProperty(s => s.LastAlertAt, (DateTime?)now)
                .SetProperty(s => s.UpdatedAt, now),
            ct);
        return claimed == 1;
    }
}
