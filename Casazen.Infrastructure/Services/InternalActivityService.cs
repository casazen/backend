using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class InternalActivityService(
    AppDbContext context,
    ILogger<InternalActivityService> logger,
    TimeProvider? timeProvider = null) : IInternalActivityService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<InternalActivity> CreateActivityAsync(
        Guid propertyId,
        string staffUserId,
        string activityType,
        DateTime scheduledAt,
        decimal? internalRate = null,
        bool rechargeToOwner = false,
        Guid? bookingId = null,
        string? notes = null,
        string? createdByUserId = null)
    {
        ArgumentNullException.ThrowIfNull(staffUserId);
        ArgumentNullException.ThrowIfNull(activityType);

        // Get the property to inherit the OrgId
        var property = await context.Properties
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == propertyId);

        if (property == null)
            throw new NotFoundException("Property not found.");

        var activity = new InternalActivity
        {
            PropertyId = propertyId,
            OrgId = property.OrgId,
            StaffUserId = staffUserId,
            ActivityType = activityType,
            ScheduledAt = scheduledAt,
            InternalRate = internalRate,
            RechargeToOwner = rechargeToOwner,
            BookingId = bookingId,
            Notes = notes,
            Status = InternalActivity.StatusPending,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
            CreatedByUserId = createdByUserId
        };

        context.InternalActivities.Add(activity);
        await context.SaveChangesAsync();

        logger.LogInformation(
            "Created internal activity {ActivityId} of type {Type} for property {PropertyId} assigned to staff {StaffId}",
            activity.Id, activityType, propertyId, staffUserId);

        return activity;
    }

    public async Task<InternalActivity> UpdateStatusAsync(Guid activityId, string status, string? notes = null)
    {
        ArgumentNullException.ThrowIfNull(status);

        // The staff work only: a "To do" is closed by CloseAsync, which records who closed it.
        var activity = await context.InternalActivities
            .FirstOrDefaultAsync(a => a.Id == activityId && a.Action == null);

        if (activity == null)
            throw new NotFoundException("Activity not found.");

        activity.Status = status;

        if (notes != null)
            activity.Notes = notes;

        if (status == InternalActivity.StatusCompleted && activity.CompletedAt == null)
            activity.CompletedAt = _clock.GetUtcNow().UtcDateTime;

        await context.SaveChangesAsync();

        logger.LogInformation(
            "Updated activity {ActivityId} status to {Status}",
            activityId, status);

        return activity;
    }

    public async Task<IReadOnlyList<InternalActivity>> GetActivitiesAsync(
        HostScope scope,
        Guid? propertyId = null,
        string? staffUserId = null,
        CancellationToken cancellationToken = default)
    {
        var query = StaffWork()
            .InScope(scope)
            .Include(a => a.Property)
            .Include(a => a.Booking)
            .AsQueryable();

        if (propertyId.HasValue)
            query = query.Where(a => a.PropertyId == propertyId.Value);

        if (!string.IsNullOrEmpty(staffUserId))
            query = query.Where(a => a.StaffUserId == staffUserId);

        return await query
            .OrderByDescending(a => a.ScheduledAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<InternalActivity>> GetMyActivitiesAsync(
        HostScope scope,
        string staffUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staffUserId);

        return await StaffWork()
            .InScope(scope)
            .Where(a => a.StaffUserId == staffUserId)
            .Include(a => a.Property)
            .Include(a => a.Booking)
            .OrderByDescending(a => a.ScheduledAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<InternalActivity?> GetActivityAsync(Guid activityId, CancellationToken cancellationToken = default)
    {
        return await StaffWork()
            .Include(a => a.Property)
            .Include(a => a.Booking)
            .FirstOrDefaultAsync(a => a.Id == activityId, cancellationToken);
    }

    // ─── The "To do" list (ADR-026) ─────────────────────────────────────────────

    public async Task<IReadOnlyList<InternalActivity>> GetPendingAsync(HostScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return await OpenTodos(scope)
            .OrderByDescending(a => a.Urgency ?? ActivityUrgency.Low)
            .ThenBy(a => a.Deadline == null)
            .ThenBy(a => a.Deadline)
            .ThenBy(a => a.CreatedAt)
            .ThenBy(a => a.Id)
            .Take(TodoLimits.MaxPendingItems)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public Task<int> CountUrgentAsync(HostScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return OpenTodos(scope).CountAsync(a => a.Urgency == ActivityUrgency.High, ct);
    }

    public async Task<Guid> CreateAsync(CreateTodoRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Type) || !Enum.IsDefined(request.Action) || !Enum.IsDefined(request.Urgency))
            throw new ArgumentException("The type, the action and the urgency of a To do must be declared members.", nameof(request));
        if (request.EntityId == Guid.Empty)
            throw new ArgumentException("A To do names the entity it is about.", nameof(request));

        var entityType = Bounded(request.EntityType, TodoLimits.EntityTypeMaxLength, nameof(request.EntityType))
            ?? throw new ArgumentException("A To do names the type of the entity it is about.", nameof(request));
        var reason = Bounded(OneLine(request.Reason), TodoLimits.ReasonMaxLength, nameof(request.Reason));
        var forUserId = Bounded(request.ForUserId, TodoLimits.UserIdMaxLength, nameof(request.ForUserId));
        var type = request.Type.ToString();
        var orgId = await OrgOfAsync(request, ct);

        if (await OpenTodoIdAsync(orgId, type, entityType, request.EntityId, ct) is { } open)
            return open;

        var now = _clock.GetUtcNow().UtcDateTime;
        var activity = new InternalActivity
        {
            OrgId = orgId,
            PropertyId = request.PropertyId,
            StaffUserId = forUserId,
            ActivityType = type,
            Action = request.Action,
            EntityType = entityType,
            EntityId = request.EntityId,
            Urgency = request.Urgency,
            Deadline = request.Deadline,
            Reason = reason,
            Status = InternalActivity.StatusPending,
            ScheduledAt = request.Deadline ?? now,
            CreatedAt = now,
        };

        context.InternalActivities.Add(activity);
        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (OrgTeamAccess.IsUniqueViolation(ex, InternalActivity.OpenTodoIndexName))
        {
            // A parallel call raised the same item first (UIX_InternalActivities_OpenTodo): that item is the one.
            context.Entry(activity).State = EntityState.Detached;
            if (await OpenTodoIdAsync(orgId, type, entityType, request.EntityId, ct) is { } winner)
                return winner;

            throw;
        }

        logger.LogInformation(
            "Raised To do {ActivityId} ({ActivityType}, action {Action}) on {EntityType} {EntityId} of org {OrgId}",
            activity.Id, type, request.Action, entityType, request.EntityId, orgId);

        return activity.Id;
    }

    public async Task CloseAsync(Guid activityId, string closedBy, CancellationToken ct)
    {
        var closer = ClosedByOf(closedBy);
        var activity = await context.InternalActivities
            .FirstOrDefaultAsync(a => a.Id == activityId && a.Action != null, ct)
            ?? throw new NotFoundException("To do not found.");

        await MarkClosedAsync(activity, closer, ct);
    }

    public async Task CloseInScopeAsync(Guid activityId, HostScope scope, string closedBy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var closer = ClosedByOf(closedBy);
        var activity = await Todos(scope)
            .FirstOrDefaultAsync(a => a.Id == activityId, ct)
            ?? throw new NotFoundException("To do not found.");

        await MarkClosedAsync(activity, closer, ct);
    }

    public async Task<InternalActivity?> FindOpenAsync(Guid orgId, string action, string entityType, Guid entityId, CancellationToken ct = default)
    {
        var found = await FindOpenAsync(orgId, action, entityType, [entityId], ct);
        return found.GetValueOrDefault(entityId);
    }

    public async Task<IReadOnlyDictionary<Guid, InternalActivity>> FindOpenAsync(
        Guid orgId,
        string action,
        string entityType,
        IReadOnlyCollection<Guid> entityIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        var parsed = ParseAction(action);
        var type = Bounded(entityType, TodoLimits.EntityTypeMaxLength, nameof(entityType))
                   ?? throw new ArgumentException("A To do names the type of the entity it is about.", nameof(entityType));
        if (entityIds.Count == 0)
            return new Dictionary<Guid, InternalActivity>();

        var ids = entityIds.Distinct().ToList();
        var rows = await context.InternalActivities
            .AsNoTracking()
            .Where(a => a.OrgId == orgId
                        && a.Action == parsed
                        && a.CompletedAt == null
                        && a.EntityType == type
                        && a.EntityId != null
                        && ids.Contains(a.EntityId.Value))
            .OrderBy(a => a.CreatedAt)
            .ThenBy(a => a.Id)
            .ToListAsync(ct);

        return rows
            .GroupBy(a => a.EntityId!.Value)
            .ToDictionary(g => g.Key, g => g.First());
    }

    /// <summary>The member of <see cref="ActivityAction"/> named <paramref name="action"/> (exactly, not a number); anything else is a programming error.</summary>
    private static ActivityAction ParseAction(string action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        return Enum.TryParse<ActivityAction>(action, ignoreCase: false, out var parsed)
               && Enum.IsDefined(parsed)
               && string.Equals(parsed.ToString(), action, StringComparison.Ordinal)
            ? parsed
            : throw new ArgumentException($"'{action}' is not an action of the To do list.", nameof(action));
    }

    /// <summary>
    /// The rows of the staff work, as the staff screens always read them: no <see cref="InternalActivity.Action"/>, on a live
    /// property (while the property was a required navigation the join dropped the activities of a soft-deleted property, PC-05).
    /// </summary>
    private IQueryable<InternalActivity> StaffWork() =>
        context.InternalActivities.Where(a => a.Action == null && a.Property != null);

    /// <summary>
    /// The "To do" rows the scope reaches, open or closed: those of its org, on a property it reaches (an org-level item only for
    /// an org-wide scope, <c>InScope</c>), and never the items of a soft-deleted property.
    /// </summary>
    private IQueryable<InternalActivity> Todos(HostScope scope)
    {
        var orgId = scope.OrgId;
        return context.InternalActivities
            .Where(a => a.OrgId == orgId && a.Action != null)
            .Where(a => a.PropertyId == null || a.Property != null)
            .InScope(scope);
    }

    /// <summary>The open "To do" rows the scope reaches (ADR-026-R4: a closed row is kept, never listed).</summary>
    private IQueryable<InternalActivity> OpenTodos(HostScope scope) => Todos(scope).Where(a => a.CompletedAt == null);

    private Task<Guid?> OpenTodoIdAsync(Guid orgId, string type, string entityType, Guid entityId, CancellationToken ct) =>
        context.InternalActivities
            .AsNoTracking()
            .Where(a => a.OrgId == orgId
                        && a.Action != null
                        && a.CompletedAt == null
                        && a.ActivityType == type
                        && a.EntityType == entityType
                        && a.EntityId == entityId)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>The org of the item: the property's, or the request's for an organization-level item. Never a client value.</summary>
    private async Task<Guid> OrgOfAsync(CreateTodoRequest request, CancellationToken ct)
    {
        if (request.PropertyId is { } propertyId)
        {
            var propertyOrgId = await context.Properties
                .AsNoTracking()
                .Where(p => p.Id == propertyId)
                .Select(p => (Guid?)p.OrgId)
                .FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Property not found.");

            if (request.OrgId is { } requested && requested != propertyOrgId)
                throw new ArgumentException("The org of a To do is the org of its property.", nameof(request));

            return propertyOrgId;
        }

        return request.OrgId is { } orgId && orgId != Guid.Empty
            ? orgId
            : throw new ArgumentException("An organization-level To do needs the org it is raised for.", nameof(request));
    }

    private async Task MarkClosedAsync(InternalActivity activity, string closedBy, CancellationToken ct)
    {
        // Closed once: a second close (a redelivered event, a double click) keeps the first time and the first closer.
        if (activity.CompletedAt is not null)
            return;

        activity.Status = InternalActivity.StatusCompleted;
        activity.CompletedAt = _clock.GetUtcNow().UtcDateTime;
        activity.ClosedBy = closedBy;
        await context.SaveChangesAsync(ct);

        logger.LogInformation(
            "Closed To do {ActivityId} ({ActivityType}) of org {OrgId}, closed by the {Closer}",
            activity.Id, activity.ActivityType, activity.OrgId, closedBy == InternalActivity.ClosedBySystem ? "system" : "user");
    }

    private static string ClosedByOf(string closedBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(closedBy);
        return Bounded(closedBy, TodoLimits.UserIdMaxLength, nameof(closedBy))!;
    }

    /// <summary>The value trimmed, <c>null</c> when blank; longer than <paramref name="maxLength"/> is a programming error.</summary>
    private static string? Bounded(string? value, int maxLength, string name)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        return trimmed.Length <= maxLength
            ? trimmed
            : throw new ArgumentException($"{name} is longer than {maxLength} characters.", name);
    }

    /// <summary>The reason on one line: the line breaks and runs of spaces become one space.</summary>
    private static string? OneLine(string? value) =>
        value is null ? null : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
