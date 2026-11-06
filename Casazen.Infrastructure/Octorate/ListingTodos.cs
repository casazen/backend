using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// The «To do» items of the publication of a listing (E02/F03/US01/T02, T07, ADR-026, ADR-034-R3): <c>PublicationError</c> when a channel cannot be
/// published, <c>IntegrationError</c> when it went silent, <c>ListingChangedOutside</c> when its content was changed on the channel manager. Each is
/// one item for a property and a channel (the entity is the <see cref="ListingChannelState"/> row), so the item raised when the publication failed and
/// the one raised when its status was read are the same item, and a closed item is raised anew only by a new problem. Nothing here is personal data.
/// </summary>
public sealed class ListingTodos(AppDbContext db, IInternalActivityService activities)
{
    /// <summary>The kind of entity the items are about.</summary>
    public const string EntityType = nameof(ListingChannelState);

    /// <summary>Raises the item for the channel row (idempotent while one is open) and returns its id.</summary>
    public Task<Guid> RaiseAsync(ActivityType type, ListingChannelState row, ActivityUrgency urgency, string reason, CancellationToken ct) =>
        activities.CreateAsync(
            new CreateTodoRequest
            {
                Type = type,
                Action = ActivityAction.FixListingError,
                EntityType = EntityType,
                EntityId = row.Id,
                PropertyId = row.PropertyId,
                Urgency = urgency,
                Reason = reason,
            },
            ct);

    /// <summary>Closes one item by its id (idempotent: an item already closed stays as it was closed the first time).</summary>
    public Task CloseItemAsync(Guid activityId, string closedBy, CancellationToken ct) => activities.CloseAsync(activityId, closedBy, ct);

    /// <summary>Closes the open items of that kind for the channel row; nothing happens when there is none.</summary>
    public async Task CloseAsync(ActivityType type, ListingChannelState row, string closedBy, CancellationToken ct)
    {
        var name = type.ToString();
        var open = await db.InternalActivities
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(a => a.OrgId == row.OrgId
                        && a.Action != null
                        && a.CompletedAt == null
                        && a.ActivityType == name
                        && a.EntityType == EntityType
                        && a.EntityId == row.Id)
            .Select(a => a.Id)
            .ToListAsync(ct);

        foreach (var id in open)
            await activities.CloseAsync(id, closedBy, ct);
    }
}
