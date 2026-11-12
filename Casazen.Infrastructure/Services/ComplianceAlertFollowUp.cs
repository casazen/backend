using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IComplianceAlertFollowUp" />
/// <remarks>
/// <list type="bullet">
/// <item><b>The "To do"</b> goes through <see cref="IInternalActivityService"/> only: <c>CreateAsync</c> is idempotent while an item is open (two parallel
/// calls return the same one) and <c>CloseAsync</c> closes as the system. The row of a closed item is kept for the audit.</item>
/// <item><b>The popup</b> goes to the people <see cref="IComplianceAlertRecipientResolver"/> says reach the property, one by one, through
/// <see cref="IAlertRouter"/>: it stores the alert for the person, applies its quiet hours (a legal alert is never held back) and pushes it to its open
/// sessions; an alert still open for the same person, type and entity is not written twice. A person who cannot be alerted does not stop the others: every
/// failure is logged with the type, the entity and the org, never the person, and thrown after the last one was tried.</item>
/// <item>No personal data is written or logged: ids, types and codes.</item>
/// </list>
/// </remarks>
public sealed class ComplianceAlertFollowUp(
    AppDbContext db,
    IInternalActivityService activities,
    IAlertRouter alertRouter,
    IComplianceAlertRecipientResolver recipients,
    ILogger<ComplianceAlertFollowUp> logger) : IComplianceAlertFollowUp
{
    /// <summary>The most items a sweep of the stays that owe nothing closes in one call.</summary>
    internal const int MaxSweep = 500;

    public async Task OpenTodoAsync(CreateTodoRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            await activities.CreateAsync(request, ct);
        }
        catch (NotFoundException)
        {
            // The property is gone (deleted since): nothing to do about it, and a retry would find the same.
            logger.LogInformation("To do {Type} on {EntityType} {EntityId} not raised: its property was not found", request.Type, request.EntityType, request.EntityId);
        }
    }

    public async Task PopupAsync(ComplianceAlertPopup popup, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(popup);

        if (!ComplianceAlertSchedule.IsLegalDeadlineNear(popup.DeadlineUtc, nowUtc))
            return;

        var audience = await recipients.ForPropertyAsync(popup.OrgId, popup.PropertyId, ct);
        var failures = new List<Exception>();
        foreach (var userId in audience.UserIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            try
            {
                await alertRouter.RouteAlertAsync(popup.Type, popup.EntityId, popup.EntityType, userId, AlertUrgency.High, popup.DeadlineUtc, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(
                    ex,
                    "Popup alert {AlertType} on {EntityType} {EntityId} of org {OrgId}: a person could not be alerted",
                    popup.Type,
                    popup.EntityType,
                    popup.EntityId,
                    popup.OrgId);
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
            throw new AggregateException($"{failures.Count} popup alert(s) of {popup.Type} on {popup.EntityType} {popup.EntityId} failed", failures);
    }

    public async Task CloseTodosAsync(
        IReadOnlyCollection<ActivityType> types,
        string entityType,
        IReadOnlyCollection<Guid> entityIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentNullException.ThrowIfNull(entityIds);

        // An empty list of entities is nothing, never "all of them".
        if (types.Count == 0 || entityIds.Count == 0)
            return;

        var typeNames = types.Select(type => type.ToString()).ToList();
        var ids = entityIds.Distinct().ToList();
        var open = await db.InternalActivities
            .AsNoTracking()
            .Where(a => a.Action != null
                        && a.CompletedAt == null
                        && a.EntityType == entityType
                        && a.EntityId != null
                        && ids.Contains(a.EntityId.Value)
                        && typeNames.Contains(a.ActivityType))
            .OrderBy(a => a.CreatedAt)
            .Select(a => a.Id)
            .ToListAsync(ct);

        await CloseAsync(open, entityType, ct);
    }

    public async Task CloseTodosOfStaysThatOweNothingAsync(IReadOnlyCollection<ActivityType> types, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(types);

        if (types.Count == 0)
            return;

        var typeNames = types.Select(type => type.ToString()).ToList();
        var open = await db.InternalActivities
            .AsNoTracking()
            .Where(a => a.Action != null
                        && a.CompletedAt == null
                        && a.EntityType == InternalActivity.BookingEntityType
                        && a.EntityId != null
                        && typeNames.Contains(a.ActivityType))
            // The one definition of a stay that owes a communication: an active status and no no-show mark.
            .Where(a => db.Bookings.Any(b => b.Id == a.EntityId
                                             && (!AlloggiatiStatusRules.ActiveBookingStatuses.Contains(b.Status) || b.NoShowAt != null)))
            .OrderBy(a => a.CreatedAt)
            .Select(a => a.Id)
            .Take(MaxSweep)
            .ToListAsync(ct);

        await CloseAsync(open, InternalActivity.BookingEntityType, ct);
    }

    private async Task CloseAsync(IReadOnlyList<Guid> activityIds, string entityType, CancellationToken ct)
    {
        foreach (var activityId in activityIds)
            await activities.CloseAsync(activityId, InternalActivity.ClosedBySystem, ct);

        if (activityIds.Count > 0)
            logger.LogInformation("{Count} To do about {EntityType} closed: the condition they stand for is cleared", activityIds.Count, entityType);
    }
}
