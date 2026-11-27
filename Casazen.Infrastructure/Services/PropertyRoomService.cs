using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.SiteProfile;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The rooms of a unit with their beds (NF-34). The host sends the whole list; it is saved in one save, under the property's photo lock (the
/// rooms rewrite the room of the photos too): a room sent with the id of a stored one is updated (so the photos assigned to it keep it), a
/// stored room not sent is removed and its photos lose the assignment. The same save sets <c>Property.Bedrooms</c> to the number of bedrooms and
/// <c>Property.Beds</c> to the number of beds, so the counters stay the one numeric truth the rest of the app reads; <c>MaxGuests</c>,
/// <c>Bathrooms</c> and the area are not derived and stay as they are (AC-NF34-2). The row is read through the tenant query filter: another
/// org's property does not exist here.
/// </summary>
public class PropertyRoomService(
    AppDbContext db,
    ILogger<PropertyRoomService> logger,
    TimeProvider? timeProvider = null) : IPropertyRoomService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<PropertyRoomView>?> GetAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        if (!await db.Properties.AsNoTracking().AnyAsync(p => p.Id == propertyId, cancellationToken))
            return null;

        var rows = await db.PropertyRooms.AsNoTracking()
            .Where(r => r.PropertyId == propertyId)
            .OrderBy(r => r.Position)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(ToView)];
    }

    public async Task<IReadOnlyList<PropertyRoomView>?> ReplaceAsync(
        Guid propertyId,
        IReadOnlyList<RoomInput> rooms,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rooms);

        // The rules first: nothing is locked or read for a list that cannot be stored.
        var normalized = PropertyRoomRules.Normalize(rooms);

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.PropertyPhotos, propertyId.ToString()));

        var property = await db.Properties.FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken);
        if (property is null)
            return null;

        // The rooms are part of the listing, which only a short-stay property has.
        PropertyRentalModeRules.EnsureShortRent(property);

        // The request may already have read (and tracked) this row before the lock was free: the room of the photos must be the committed one.
        await db.Entry(property).ReloadAsync(cancellationToken);

        var stored = await db.PropertyRooms.Where(r => r.PropertyId == propertyId).ToListAsync(cancellationToken);
        var byId = stored.ToDictionary(room => room.Id);
        var kept = new HashSet<Guid>();
        var saved = new List<PropertyRoom>(normalized.Rooms.Count);
        for (var position = 0; position < normalized.Rooms.Count; position++)
        {
            var input = normalized.Rooms[position];
            PropertyRoom row;
            if (input.Id is { } id)
            {
                // A room of another property is as if it did not exist.
                row = byId.GetValueOrDefault(id)
                    ?? throw new NotFoundException($"Room not in property {propertyId}")
                    {
                        Code = PropertyRoomErrorCodes.RoomNotFound,
                        MessageKey = "PropertyRoomNotFound",
                    };
                kept.Add(id);
            }
            else
            {
                row = new PropertyRoom { PropertyId = propertyId, OrgId = property.OrgId };
                db.PropertyRooms.Add(row);
            }

            row.Position = position;
            row.Type = input.Type;
            row.Beds = input.Beds ?? [];
            saved.Add(row);
        }

        var removed = stored.Where(room => !kept.Contains(room.Id)).ToList();
        db.PropertyRooms.RemoveRange(removed);

        property.Bedrooms = normalized.Bedrooms;
        property.Beds = normalized.Beds;
        if (removed.Count > 0)
            property.PhotoAltJson = PropertyPhotoMeta.WithoutRooms(property.PhotoAltJson, [.. removed.Select(room => room.Id)]);
        property.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Rooms of property {PropertyId} of org {OrgId} saved: {Rooms} rooms, {Beds} beds", property.Id, property.OrgId, saved.Count, normalized.Beds);
        return [.. saved.Select(ToView)];
    }

    private static PropertyRoomView ToView(PropertyRoom row) => new(row.Id, row.Position, row.Type, row.Beds);
}
