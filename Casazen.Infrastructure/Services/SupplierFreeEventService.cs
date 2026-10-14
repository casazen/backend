using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>Stable codes of the answer about a free-looking calendar event. Each has a resource key. Never rename a code.</summary>
public static class SupplierFreeEventErrorCodes
{
    /// <summary>The answer is not "Available" or "Busy". HTTP 422.</summary>
    public const string InvalidDecision = "free_event_decision_invalid";

    /// <summary>The event was already answered (by this or another session). HTTP 409.</summary>
    public const string AlreadyDecided = "free_event_already_decided";

    public const string InvalidDecisionMessageKey = "FreeEventDecisionInvalid";
    public const string AlreadyDecidedMessageKey = "FreeEventAlreadyDecided";
}

/// <summary>One unanswered free-looking event of the supplier's calendar, for its console.</summary>
public sealed record SupplierFreeEventItem(
    Guid Id,
    string? Summary,
    bool IsAllDay,
    bool Recurring,
    int Occurrences,
    DateTime NextStartUtc,
    DateTime NextEndUtc,
    DateTime FirstSeenAt);

/// <summary>
/// The supplier's answers about the events of its calendar feed that the calendar marks "Available" (see
/// <see cref="SupplierFreeCalendarEvent"/>). Every statement carries the supplier org as an explicit predicate (the table is
/// not tenant-filtered, <c>SupplierFreeEventTenancyTests</c>), so a supplier never reaches the row of another.
/// </summary>
public class SupplierFreeEventService(AppDbContext db, ILogger<SupplierFreeEventService> logger)
{
    /// <summary>Rows listed at most: the sync keeps no more than <see cref="ICal.ICalFreeEvents.MaxPendingOccurrences"/> unanswered events.</summary>
    private const int MaxListed = ICal.ICalFreeEvents.MaxPendingOccurrences;

    /// <summary>Events not answered yet, the next to happen first.</summary>
    public async Task<IReadOnlyList<SupplierFreeEventItem>> ListPendingAsync(Guid orgId, CancellationToken ct = default)
    {
        var rows = await db.SupplierFreeCalendarEvents
            .AsNoTracking()
            .Where(e => e.OrgId == orgId && e.Decision == SupplierFreeEventDecision.Pending)
            .OrderBy(e => e.NextStartUtc)
            .ThenBy(e => e.Id)
            .Take(MaxListed)
            .ToListAsync(ct);

        return rows
            .Select(e => new SupplierFreeEventItem(
                e.Id, e.Summary, e.IsAllDay, e.Recurring, e.Occurrences, e.NextStartUtc, e.NextEndUtc, e.FirstSeenAt))
            .ToList();
    }

    /// <summary>How many events wait for an answer (the dashboard shows it).</summary>
    public Task<int> CountPendingAsync(Guid orgId, CancellationToken ct = default) =>
        db.SupplierFreeCalendarEvents
            .AsNoTracking()
            .CountAsync(e => e.OrgId == orgId && e.Decision == SupplierFreeEventDecision.Pending, ct);

    /// <summary>Reads the answer of a request: "Available" or "Busy", any case. Null for anything else (including "Pending").</summary>
    public static SupplierFreeEventDecision? ParseDecision(string? value)
    {
        var text = value?.Trim();
        if (string.Equals(text, nameof(SupplierFreeEventDecision.Available), StringComparison.OrdinalIgnoreCase))
            return SupplierFreeEventDecision.Available;

        return string.Equals(text, nameof(SupplierFreeEventDecision.Busy), StringComparison.OrdinalIgnoreCase)
            ? SupplierFreeEventDecision.Busy
            : null;
    }

    /// <summary>
    /// Stores the answer about an event, under the supplier's availability lock (the sync reads the answers under the same lock).
    /// Returns null when the event is not the supplier's (or does not exist), else whether the days and hours the event
    /// occupied must be freed now (<see cref="SupplierFreeEventDecision.Available"/>): the caller queues the sync.
    /// </summary>
    /// <exception cref="DomainRuleException"><see cref="SupplierFreeEventErrorCodes.InvalidDecision"/>.</exception>
    /// <exception cref="DomainConflictException"><see cref="SupplierFreeEventErrorCodes.AlreadyDecided"/>: the answer is not changed.</exception>
    public async Task<bool?> DecideAsync(Guid orgId, Guid id, string? decision, CancellationToken ct = default)
    {
        if (ParseDecision(decision) is not { } answer)
        {
            throw new DomainRuleException(
                SupplierFreeEventErrorCodes.InvalidDecision, SupplierFreeEventErrorCodes.InvalidDecisionMessageKey);
        }

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, ct, CalendarSyncService.AvailabilityLock(orgId));

        var row = await db.SupplierFreeCalendarEvents.FirstOrDefaultAsync(e => e.OrgId == orgId && e.Id == id, ct);
        if (row is null)
            return null;

        if (row.Decision != SupplierFreeEventDecision.Pending)
        {
            logger.LogInformation(
                "Free calendar event {EventId} of supplier {OrgId} was already answered ({Decision})", id, orgId, row.Decision);
            throw new DomainConflictException(
                SupplierFreeEventErrorCodes.AlreadyDecided, SupplierFreeEventErrorCodes.AlreadyDecidedMessageKey);
        }

        row.Decision = answer;
        row.DecidedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        logger.LogInformation("Free calendar event {EventId} of supplier {OrgId} answered {Decision}", id, orgId, answer);
        return answer == SupplierFreeEventDecision.Available;
    }
}
