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
/// the one raised when its status was read are the same item, and a closed item is raised anew only by a new problem. Two more are about the photos
/// (<see cref="RaisePhotosNotNamedAsync"/>, <see cref="RaisePhotoNotRemovedAsync"/>): what CasaZen cannot do for a photo is said, never done in
/// silence. Nothing here is personal data.
/// </summary>
public sealed class ListingTodos(AppDbContext db, IInternalActivityService activities)
{
    /// <summary>The kind of entity the items are about.</summary>
    public const string EntityType = nameof(ListingChannelState);

    /// <summary>The kind of entity the item about the photos that the channel manager did not name is about: the photos of a property, one item for each property.</summary>
    public const string PhotosEntityType = "ListingPhotos";

    /// <summary>The reason of that item: the channel manager took photos and did not say how it names them, so removing one in CasaZen cannot remove it there.</summary>
    public const string PhotoNotNamedReason = "listing_photo_not_named";

    /// <summary>The reason of the item about a photo that could not be removed on the channel manager (one item for each photo).</summary>
    public const string PhotoNotRemovedReason = "listing_photo_not_removed";

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

    /// <summary>
    /// Raises the item about the photos of a property that the channel manager took without naming them (idempotent while one is open): the host
    /// learns that a photo removed in CasaZen will have to be removed there by hand. One item for the property, however many photos.
    /// </summary>
    public Task<Guid> RaisePhotosNotNamedAsync(Guid propertyId, CancellationToken ct) =>
        activities.CreateAsync(
            new CreateTodoRequest
            {
                Type = ActivityType.PublicationError,
                Action = ActivityAction.FixListingError,
                EntityType = PhotosEntityType,
                EntityId = propertyId,
                PropertyId = propertyId,
                Urgency = ActivityUrgency.Medium,
                Reason = PhotoNotNamedReason,
            },
            ct);

    /// <summary>
    /// Raises the item about a photo that was removed in CasaZen and could not be removed on the channel manager, which may still show it (idempotent
    /// while one is open): the host removes it there by hand.
    /// </summary>
    public Task<Guid> RaisePhotoNotRemovedAsync(PhotoUploadTask photo, CancellationToken ct) =>
        activities.CreateAsync(
            new CreateTodoRequest
            {
                Type = ActivityType.PublicationError,
                Action = ActivityAction.FixListingError,
                EntityType = nameof(PhotoUploadTask),
                EntityId = photo.Id,
                PropertyId = photo.PropertyId,
                Urgency = ActivityUrgency.Medium,
                Reason = PhotoNotRemovedReason,
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
