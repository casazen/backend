using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Daily alert to the hosts of properties without a valid CIN (CO-20, A5-31), run by the <c>cin-deadline-alert</c> job in
/// place of the old one, which only wrote a log line every day and whose "days left" stayed at 0 for ever after the
/// deadline. Runbook: <c>docs/runbooks/cin-format.md</c>, section "CIN deadline and host alerts".
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Which properties: active (<see cref="Property.IsActive"/>) with compliance status
/// <see cref="PropertyComplianceStatus.Pending"/> or <see cref="PropertyComplianceStatus.Active"/> and a CIN missing or
/// not in the official format (<see cref="CinFormat"/>). A suspended property is never alerted: its host already got the
/// suspension email of CO-06. A published (<see cref="PropertyComplianceStatus.Active"/>) property without a valid CIN is
/// first re-evaluated by <see cref="IPropertyComplianceStatusService"/>: CO-06 suspends it and sends its own email, the
/// only one the host gets.</item>
/// <item>When: the stage of the day (<see cref="CinAlertStages.Due"/>) from the configured deadline
/// (<see cref="CinDeadlineCalendar"/>): each threshold of <c>Cin:AlertDaysBefore</c>, the deadline day, after it; one
/// reminder of the obligation when no deadline is configured. Before the first threshold the run stops at once, without
/// logging.</item>
/// <item>Each stage is claimed per property before it is sent with a compare-and-set on <see cref="CinAlertState"/>:
/// it is delivered at most once, even with retries, a manual trigger or two runs at once. A delivery that fails after the
/// claim is logged and not repeated. The claimed properties of an org go in one email.</item>
/// <item>One run at a time: a session advisory lock (<see cref="PostgresAdvisoryLocks.Scope.CinDeadlineAlertsRun"/>) on
/// top of Hangfire's <c>DisableConcurrentExecution</c>; a run that finds it taken does nothing.</item>
/// </list>
/// </remarks>
public sealed class CinDeadlineAlertService(
    AppDbContext db,
    IPropertyComplianceStatusService complianceStatus,
    INotificationService notificationService,
    CinDeadlineCalendar calendar,
    ILogger<CinDeadlineAlertService> logger,
    TimeProvider? timeProvider = null) : ICinDeadlineAlertService
{
    private const string RunLockKey = "cin-deadline-alert";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<CinDeadlineAlertRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var deadline = calendar.Today();
        var stage = CinAlertStages.Due(deadline, calendar.Options.GetAlertDaysBefore());
        if (stage is null)
            return new CinDeadlineAlertRunResult(Skipped: false, Stage: null, PropertiesAlerted: 0, EmailsQueued: 0);

        await using var runLock = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
            db, PostgresAdvisoryLocks.Scope.CinDeadlineAlertsRun, RunLockKey, cancellationToken);
        if (runLock is null)
        {
            logger.LogInformation("CIN deadline alert run skipped: another run is in progress");
            return new CinDeadlineAlertRunResult(Skipped: true, Stage: stage, PropertiesAlerted: 0, EmailsQueued: 0);
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        // Background run: no tenant context, every org's properties.
        var candidates = (await db.Properties
                .AsNoTracking()
                .Where(p => p.IsActive
                    && (p.ComplianceStatus == PropertyComplianceStatus.Pending
                        || p.ComplianceStatus == PropertyComplianceStatus.Active))
                .OrderBy(p => p.Id)
                .Select(p => new { p.Id, p.OrgId, p.CinCode, p.ComplianceStatus })
                .ToListAsync(cancellationToken))
            .Where(p => !CinComplianceRules.IsCompliant(p.CinCode))
            .ToList();

        var claimed = new List<(Guid OrgId, Guid PropertyId)>();
        foreach (var property in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (property.ComplianceStatus == PropertyComplianceStatus.Active
                    && await SuspendedByComplianceCheckAsync(property.Id, cancellationToken))
                    continue;

                if (await ClaimAsync(property.OrgId, property.Id, deadline.Deadline, stage.Value, now, cancellationToken))
                    claimed.Add((property.OrgId, property.Id));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "CIN alert of property {PropertyId} could not be processed", property.Id);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        var emails = 0;
        foreach (var org in claimed.GroupBy(c => c.OrgId))
        {
            var propertyIds = org.Select(c => c.PropertyId).ToList();
            try
            {
                if (await notificationService.SendCinDeadlineAlertAsync(
                        new CinDeadlineAlert(org.Key, propertyIds, deadline), cancellationToken))
                    emails++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // At most once: the stage stays claimed, the next run does not send it again.
                logger.LogError(
                    ex,
                    "CIN alert (stage {Stage}) of org {OrgId} claimed for {PropertyCount} properties but not delivered",
                    stage,
                    org.Key,
                    propertyIds.Count);
            }
        }

        if (claimed.Count > 0)
        {
            logger.LogInformation(
                "CIN deadline alert ({Phase}, stage {Stage}): {Properties} properties alerted, {Emails} emails queued",
                deadline.PhaseApiValue,
                stage,
                claimed.Count,
                emails);
        }

        return new CinDeadlineAlertRunResult(Skipped: false, Stage: stage, PropertiesAlerted: claimed.Count, EmailsQueued: emails);
    }

    /// <summary>
    /// A published property without a valid CIN is evaluated again by CO-06, which suspends it and emails the host
    /// (<see cref="IPropertyComplianceStatusService.ReevaluateAsync"/>): true when it is no longer active, so no CIN alert
    /// is added to the suspension email.
    /// </summary>
    private async Task<bool> SuspendedByComplianceCheckAsync(Guid propertyId, CancellationToken cancellationToken)
    {
        var check = await complianceStatus.ReevaluateAsync(propertyId, cancellationToken);
        return check.Status != PropertyComplianceStatus.Active;
    }

    /// <summary>
    /// Moves the property's state to <paramref name="stage"/> of <paramref name="deadline"/> when no stage was sent yet,
    /// the last one refers to another deadline, or it is an earlier stage (a larger number of days), in one conditional
    /// UPDATE: of two concurrent callers only one gets the row. Internal for the tests.
    /// </summary>
    internal async Task<bool> ClaimAsync(
        Guid orgId,
        Guid propertyId,
        DateOnly? deadline,
        int stage,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (!await db.CinAlertStates.AnyAsync(s => s.PropertyId == propertyId, cancellationToken))
        {
            var state = new CinAlertState
            {
                OrgId = orgId,
                PropertyId = propertyId,
                Deadline = deadline,
                Stage = null,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.CinAlertStates.Add(state);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
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

        var claimed = await db.CinAlertStates
            .Where(s => s.PropertyId == propertyId
                && (s.Stage == null || s.Deadline != deadline || s.Stage > stage))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(s => s.Deadline, deadline)
                    .SetProperty(s => s.Stage, (int?)stage)
                    .SetProperty(s => s.AlertCount, s => s.AlertCount + 1)
                    .SetProperty(s => s.LastAlertAt, (DateTime?)now)
                    .SetProperty(s => s.UpdatedAt, now),
                cancellationToken);
        return claimed == 1;
    }
}
