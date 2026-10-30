using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ISupplierReliabilityService"/>
/// <remarks>
/// <para><b>A record, never a blocker.</b> Every write is best effort: it runs after the work of the caller is saved, and a failure is
/// logged and swallowed (the caller must not fail because a point could not be written). It shares the caller's context, so it is
/// called when nothing else is waiting to be saved.</para>
/// <para><b>Fairness.</b> A missed deadline is not held against a supplier that could not answer: one that is not <c>Active</c>, that was
/// on time off, or whose working hours do not touch the time the request waited (an hour at night). A supplier that has written no hours
/// is taken to have been there: nothing says it was away. A reply in time earns a point only when the request had a deadline. Hours and
/// time off are read through <see cref="ISupplierAgendaService"/>, the only reader of the tables of the agenda.</para>
/// <para><b>Tenancy.</b> <c>SupplierReliabilityEvents</c> is keyed by the supplier org and not tenant-filtered (TN-2 allow-list); every
/// statement here carries an explicit supplier predicate (<c>SupplierReliabilityTenancyTests</c>).</para>
/// </remarks>
public sealed class SupplierReliabilityService(
    AppDbContext db,
    ISupplierAgendaService agenda,
    IOptions<SupplierRankingOptions> options,
    ILogger<SupplierReliabilityService> logger,
    TimeProvider? timeProvider = null) : ISupplierReliabilityService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task RecordAsync(
        Guid supplierOrgId,
        SupplierReliabilityEventKind kind,
        Guid? requestId,
        DateTime occurredAt,
        bool asBackup = false,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<SupplierReliabilityEvent>? entry = null;
        try
        {
            // Once for each supplier, request and kind: the unique index is the last guard, this is the usual one.
            if (requestId is { } id
                && await db.SupplierReliabilityEvents.AsNoTracking()
                    .AnyAsync(e => e.SupplierOrgId == supplierOrgId && e.RequestId == id && e.Kind == kind, cancellationToken))
            {
                return;
            }

            entry = db.SupplierReliabilityEvents.Add(new SupplierReliabilityEvent
            {
                SupplierOrgId = supplierOrgId,
                Kind = kind,
                RequestId = requestId,
                AsBackup = asBackup,
                OccurredAt = UtcDateTime.Normalize(occurredAt),
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
                Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim()[..Math.Min(detail.Trim().Length, 200)],
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Another writer recorded the same event first (the unique index), or the record could not be written: the work of the caller
            // is done either way. What was not saved must not stay in the context, or the next save of the caller would try it again.
            if (entry is not null)
                entry.State = EntityState.Detached;

            if (ex is DbUpdateException)
            {
                logger.LogInformation("Reliability event {Kind} of supplier {SupplierOrgId} for request {RequestId} was not written (recorded already?)", kind, supplierOrgId, requestId);
            }
            else
            {
                logger.LogError(ex, "Reliability event {Kind} of supplier {SupplierOrgId} for request {RequestId} could not be recorded", kind, supplierOrgId, requestId);
            }
        }
    }

    public Task RecordReplyAsync(
        Guid supplierOrgId,
        Guid requestId,
        DateTime repliedAt,
        DateTime? dueAt,
        bool asBackup = false,
        CancellationToken cancellationToken = default)
    {
        // A request with no deadline asks for no punctuality, and a late reply earns nothing.
        if (dueAt is not { } due || UtcDateTime.Normalize(repliedAt) > UtcDateTime.Normalize(due))
            return Task.CompletedTask;

        return RecordAsync(supplierOrgId, SupplierReliabilityEventKind.RepliedInTime, requestId, repliedAt, asBackup, null, cancellationToken);
    }

    public async Task<bool> RecordNoReplyAsync(
        Guid supplierOrgId,
        Guid requestId,
        DateTime windowStartUtc,
        DateTime dueAtUtc,
        bool asBackup = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var start = UtcDateTime.Normalize(windowStartUtc);
            var due = UtcDateTime.Normalize(dueAtUtc);
            if (!await WasReachableAsync(supplierOrgId, start < due ? start : due, due, cancellationToken))
                return false;

            await RecordAsync(supplierOrgId, SupplierReliabilityEventKind.NoReplyInTime, requestId, due, asBackup, null, cancellationToken);

            // The supplier is told it loses points only if it does.
            return options.Value.ReliabilityEnabled && SupplierRanking.NoReplyPenaltyOf(options.Value, asBackup) > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The missed reply of supplier {SupplierOrgId} to request {RequestId} could not be judged", supplierOrgId, requestId);
            return false;
        }
    }

    public async Task<IReadOnlyDictionary<Guid, int>> PointsAsync(
        IReadOnlyCollection<Guid> supplierOrgIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(supplierOrgIds);
        var settings = options.Value;
        if (!settings.ReliabilityEnabled || supplierOrgIds.Count == 0)
            return new Dictionary<Guid, int>();

        var ids = supplierOrgIds.Distinct().ToList();
        var since = _clock.GetUtcNow().UtcDateTime.AddDays(-settings.WindowDays);
        var rows = await db.SupplierReliabilityEvents
            .AsNoTracking()
            .Where(e => ids.Contains(e.SupplierOrgId) && e.OccurredAt >= since)
            .Select(e => new { e.SupplierOrgId, e.Kind, e.RequestId, e.AsBackup })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.SupplierOrgId)
            .ToDictionary(
                group => group.Key,
                group => SupplierRanking.ReliabilityPoints(
                    settings,
                    SupplierRanking.Tally(group.Select(row => new ReliabilityEventRecord(row.Kind, row.RequestId, row.AsBackup)))));
    }

    /// <summary>
    /// Whether the supplier could have answered while the request waited: it is active and some part of the window falls in a working
    /// band of a day that is not time off. Without any working hours written nothing says it was away.
    /// </summary>
    private async Task<bool> WasReachableAsync(Guid supplierOrgId, DateTime windowStartUtc, DateTime windowEndUtc, CancellationToken cancellationToken)
    {
        // SupplierProfile is keyed by the supplier org and not tenant-filtered; scoped by the supplier org id.
        var status = await db.SupplierProfiles
            .AsNoTracking()
            .Where(sp => sp.OrgId == supplierOrgId)
            .Select(sp => (SupplierStatus?)sp.Status)
            .FirstOrDefaultAsync(cancellationToken);
        if (status != SupplierStatus.Active)
            return false;

        // The tables of the agenda are read through the agenda service only (SupplierAgendaTenancyTests). Its list of time off keeps what
        // has not ended yet: a holiday that ended before today is not seen, which only matters when the job is a day late.
        var hours = await agenda.GetHoursAsync(supplierOrgId, cancellationToken);
        if (hours.Bands.Count == 0)
            return true;

        var timeOff = await agenda.ListTimeOffAsync(supplierOrgId, cancellationToken);
        var firstDay = RomeCalendar.DateInRome(windowStartUtc);
        var lastDay = RomeCalendar.DateInRome(windowEndUtc);
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            if (timeOff.Any(t => t.FromDate <= day && day <= t.ToDate))
                continue;

            foreach (var band in hours.Bands.Where(b => b.Weekday == day.DayOfWeek))
            {
                if (RomeCalendar.ToUtc(day, band.StartMinute) < windowEndUtc && RomeCalendar.ToUtc(day, band.EndMinute) > windowStartUtc)
                    return true;
            }
        }

        return false;
    }
}
