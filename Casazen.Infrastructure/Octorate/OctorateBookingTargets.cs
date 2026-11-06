using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// <see cref="IOctorateBookingTargets"/> (E08/F01/US01/T02): where in Octorate a CasaZen property books, from its confirmed
/// <c>OctorateListingMapping</c> (E02/F01/US02/T02). See the interface for the rules; nothing is ever guessed.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Tenant.</b> The property is read through the tenant filter of the caller (a request, or an event handler under the org of its event):
/// a property of another org is not found, so it is not mirrored. Every other read names the org of the property.</item>
/// <item><b>The room.</b> The room of the mapping. A confirmed mapping without one reads the rooms of the accommodation (<c>roomrates</c>) and keeps
/// the only one on the mapping, like the confirmation of the mapping does (<see cref="ChannelMappingService.ChooseRoom"/>); with none or several
/// it answers <see cref="OctorateTargetGap.RoomNotChosen"/>, remembered for <see cref="RoomNotChosenMemory"/>.</item>
/// <item><b>The PMS room</b> of the unit (<c>GET /rest/v1/pms/{acc}</c>, <c>parentId</c> = the unit): Octorate wants it as <c>pmsProduct</c> to create
/// and change a reservation (sandbox 2026-11-04). Kept when the unit has exactly one, remembered for <see cref="PmsRoomMemory"/>; none or several:
/// no PMS room, never a guess. A failed read of Octorate is the caller's error to handle: no target is given on half the facts.</item>
/// <item><b>Logs</b> carry ids only.</item>
/// </list>
/// </remarks>
public sealed class OctorateBookingTargets(
    AppDbContext db,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    IChannelMappingService channelMapping,
    IMemoryCache cache,
    IHostEnvironment hostEnvironment,
    TimeProvider timeProvider,
    ILogger<OctorateBookingTargets> logger) : IOctorateBookingTargets
{
    /// <summary>How long a confirmed mapping without a single room is not asked to Octorate again.</summary>
    public static readonly TimeSpan RoomNotChosenMemory = TimeSpan.FromMinutes(5);

    /// <summary>How long the PMS room of a unit is remembered.</summary>
    public static readonly TimeSpan PmsRoomMemory = TimeSpan.FromHours(1);

    public async Task<OctorateTargetLookup> LookupAsync(Guid propertyId, CancellationToken ct)
    {
        // The property as the caller sees it: the tenant filter of a request or of an event handler hides the property of another org.
        var orgId = await db.Properties
            .AsNoTracking()
            .Where(p => p.Id == propertyId)
            .Select(p => (Guid?)p.OrgId)
            .FirstOrDefaultAsync(ct);
        if (orgId is not { } org)
            return OctorateTargetLookup.NotMirrored;

        var environment = OctorateEnvironments.Of(hostEnvironment);
        var connected = await db.OctorateConnections
            .AsNoTracking()
            .AnyAsync(c => c.OrgId == org && c.Environment == environment && c.Status == OctorateConnectionStatus.Connected, ct);
        if (!connected)
            return OctorateTargetLookup.NotMirrored;

        // A property in a proposed or ambiguous row, as the proposal or as a candidate, is never written to (OCT-04).
        if (await channelMapping.IsWriteBlockedAsync(propertyId, ct))
            return OctorateTargetLookup.NotMirrored;

        var mapping = await db.OctorateListingMappings
            .FirstOrDefaultAsync(m => m.OrgId == org && m.PropertyId == propertyId && m.Status == OctorateMappingStatus.Confirmed, ct);
        if (mapping is null || string.IsNullOrWhiteSpace(mapping.OctorateAccommodationId))
            return OctorateTargetLookup.NotMirrored;

        var accommodation = mapping.OctorateAccommodationId.Trim();
        var product = mapping.OctorateProductId?.Trim();
        if (string.IsNullOrEmpty(product))
        {
            product = await SingleRoomAsync(org, mapping, accommodation, ct);
            if (product is null)
                return OctorateTargetLookup.RoomNotChosen;
        }

        var pmsRoom = await PmsRoomOfAsync(org, accommodation, product, ct);
        return OctorateTargetLookup.Of(new OctorateBookingTarget(accommodation, product, pmsRoom));
    }

    /// <summary>The only room of the accommodation, kept on the mapping; <c>null</c> when it has none or several (remembered a while).</summary>
    private async Task<string?> SingleRoomAsync(
        Guid orgId,
        Core.Entities.OctorateListingMapping mapping,
        string accommodation,
        CancellationToken ct)
    {
        var memory = new RoomNotChosenKey(mapping.Id);
        if (cache.TryGetValue(memory, out _))
            return null;

        OctorateRoom[] rooms;
        using (orgScope.Use(orgId))
            rooms = await client.GetRoomsAsync(accommodation, ct);

        var single = ChannelMappingService.ChooseRoom(rooms, null);
        if (single is null)
        {
            cache.Set(memory, true, RoomNotChosenMemory);
            logger.LogInformation(
                "Octorate mapping {MappingId} of org {OrgId}: the accommodation has no single room, the host chooses it", mapping.Id, orgId);
            return null;
        }

        mapping.OctorateProductId = single;
        mapping.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Octorate mapping {MappingId} of org {OrgId}: its only room is kept", mapping.Id, orgId);
        return single;
    }

    /// <summary>The PMS room of the unit when it has exactly one; <c>null</c> otherwise. Remembered for <see cref="PmsRoomMemory"/>.</summary>
    private async Task<string?> PmsRoomOfAsync(Guid orgId, string accommodation, string product, CancellationToken ct)
    {
        var memory = new PmsRoomKey(orgId, accommodation, product);
        if (cache.TryGetValue(memory, out PmsRoomMemo? remembered) && remembered is not null)
            return remembered.PmsRoomId;

        OctoratePmsRoom[] rooms;
        using (orgScope.Use(orgId))
            rooms = await client.GetPmsRoomsAsync(accommodation, ct);

        var ofUnit = rooms
            .Where(room => string.Equals(room.ParentId?.Trim(), product, StringComparison.Ordinal))
            .Select(room => room.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var pmsRoom = ofUnit.Count == 1 ? ofUnit[0] : null;
        if (ofUnit.Count > 1)
        {
            logger.LogInformation(
                "Octorate accommodation of org {OrgId}: the room has {Count} PMS rooms, the reservation is created on the room alone", orgId, ofUnit.Count);
        }

        cache.Set(memory, new PmsRoomMemo(pmsRoom), PmsRoomMemory);
        return pmsRoom;
    }

    private sealed record RoomNotChosenKey(Guid MappingId);

    private sealed record PmsRoomKey(Guid OrgId, string AccommodationId, string ProductId);

    private sealed record PmsRoomMemo(string? PmsRoomId);
}
