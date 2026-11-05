using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// <see cref="IChannelMappingService"/> (E02/F01/US02/T03, ADR-023-R4, ADR-027-R8): the listings of the org's Octorate connection, the
/// proposals of <see cref="PropertyMatcher"/>, the confirmation by a person, the import of a listing CasaZen does not have.
/// </summary>
/// <remarks>
/// <para><b>Nothing written before a person confirms</b> (OCT-04): a property in a proposed or ambiguous row is blocked
/// (<see cref="IsWriteBlockedAsync"/>), and an ambiguous row raises one "To do" (<see cref="ActivityType.ConfirmChannelMapping"/>,
/// organization-level), closed when the row is confirmed or rejected.</para>
/// <para><b>Reach.</b> Confirming, choosing the room and rejecting check that the property belongs to the org and is within the reach of
/// the person as the org team decides it (<see cref="IHostScopeResolver"/>, from the membership in the database); a person in no org
/// team is decided by the roles of its token, which the web layer hands over (<c>tokenRoles</c>) so the service decides with the same
/// inputs as the controller that has already checked the policy and the row (none given: only the properties the person created).
/// Confirming also needs the reach of the property the listing is already proposed or confirmed to, and does not move a confirmed
/// listing to another property (409): it is rejected first. A row without a property is an org-level decision: only a person who
/// reaches the whole org rejects it.</para>
/// <para><b>Quota.</b> A refresh reads the rooms of each open listing (one call each) only while the org has more than
/// <see cref="RoomReadReserve"/> calls left in its bucket, the listings without rooms first; the others keep their snapshot until the next
/// refresh. Confirming and choosing the room read the rooms of their listing again.</para>
/// <para><b>Audit.</b> Every confirmation, room, rejection and import writes the structured line <c>OctorateMappingAudit</c> (ids and the
/// user id, never personal data or a token).</para>
/// </remarks>
public sealed class ChannelMappingService(
    AppDbContext db,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    OctorateRateLimiter rateLimiter,
    IHostScopeResolver hostScopeResolver,
    IPropertyService propertyService,
    IEntitlementService entitlements,
    IInternalActivityService activities,
    IOptions<OctorateOptions> options,
    IHostEnvironment hostEnvironment,
    TimeProvider timeProvider,
    ILogger<ChannelMappingService> logger) : IChannelMappingService
{
    /// <summary>The entity type of the "To do" about a mapping.</summary>
    public const string MappingEntityType = "OctorateListingMapping";

    /// <summary>The entity type of the "To do" about an imported property.</summary>
    public const string PropertyEntityType = "Property";

    /// <summary>Calls of the org's bucket a refresh leaves to the person's own actions when it reads the rooms.</summary>
    public const int RoomReadReserve = 20;

    private static readonly IReadOnlySet<string> NoTokenRoles = new HashSet<string>(StringComparer.Ordinal);

    private static readonly EventId AuditEvent = new(52_201, "OctorateMappingAudit");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private OctorateEnvironment Environment => OctorateEnvironments.Of(hostEnvironment);

    public async Task<IReadOnlyList<ChannelMappingRow>> RefreshAsync(Guid orgId, CancellationToken ct)
    {
        var connection = await db.OctorateConnections.FirstOrDefaultAsync(
                c => c.OrgId == orgId && c.Environment == Environment && c.Status == OctorateConnectionStatus.Connected, ct)
            ?? throw OctorateAuthException.NotConnected();

        OctorateProperty[] listings;
        using (orgScope.Use(orgId))
            listings = await client.GetPropertiesAsync(ct);

        var rows = await db.OctorateListingMappings
            .Where(m => m.OrgId == orgId && m.ConnectionId == connection.Id)
            .ToDictionaryAsync(m => m.OctorateAccommodationId, StringComparer.Ordinal, ct);
        var now = Now();
        var open = new List<(OctorateListingMapping Row, OctorateProperty Listing)>();
        foreach (var listing in listings)
        {
            if (rows.TryGetValue(listing.Id, out var row) && row.Status is OctorateMappingStatus.Confirmed or OctorateMappingStatus.Rejected)
                continue;

            if (row is null)
            {
                row = new OctorateListingMapping
                {
                    OrgId = orgId,
                    ConnectionId = connection.Id,
                    OctorateAccommodationId = listing.Id,
                    CreatedAt = now,
                };
                db.OctorateListingMappings.Add(row);
                rows[listing.Id] = row;
            }

            ApplySnapshot(row, listing);
            open.Add((row, listing));
        }

        // The rooms (one call per listing), while the org has calls to spare: the listings without rooms first.
        var roomsLeft = 0;
        foreach (var (row, _) in open.OrderBy(entry => entry.Row.SnapshotRoomsJson == OctorateListingMapping.EmptyJsonArray ? 0 : 1))
        {
            if (rateLimiter.AvailableCalls(orgId) <= RoomReadReserve)
            {
                roomsLeft++;
                continue;
            }

            using (orgScope.Use(orgId))
                ApplyRooms(row, await client.GetRoomsAsync(row.OctorateAccommodationId, ct));
        }

        var candidates = await CandidatesAsync(orgId, ct);
        var thresholds = options.Value.Mapping;
        foreach (var (row, listing) in open)
        {
            var proposal = PropertyMatcher.Propose(
                new MatchListing(listing.Name, listing.Address, listing.City, listing.PostalCode, listing.Latitude, listing.Longitude, row.SnapshotMaxGuests),
                candidates,
                thresholds);
            row.Status = proposal.Status;
            row.PropertyId = proposal.PropertyId;
            row.CandidatePropertyIdsJson = JsonSerializer.Serialize(proposal.CandidateIds, Json);
            row.MatchScore = proposal.BestScore;
            row.UpdatedAt = now;
        }

        connection.LastSyncedAt = now;
        connection.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (OrgTeamAccess.IsUniqueViolation(ex, OctorateListingMapping.AccommodationIndexName))
        {
            // Another refresh of the org inserted the same listing first.
            throw new DomainConflictException("octorate_mapping_refresh_conflict", "OctorateMappingRefreshConflict");
        }

        foreach (var (row, _) in open.Where(entry => entry.Row.Status == OctorateMappingStatus.Ambiguous))
            await RaiseConfirmTodoAsync(row, ct);

        logger.LogInformation(
            "Octorate listings of org {OrgId} refreshed: {Found} found, {Open} to decide, {Ambiguous} ambiguous, rooms of {RoomsLeft} left for the next refresh",
            orgId, listings.Length, open.Count, open.Count(entry => entry.Row.Status == OctorateMappingStatus.Ambiguous), roomsLeft);
        return await ListAsync(orgId, ct);
    }

    public async Task<IReadOnlyList<ChannelMappingRow>> ListAsync(Guid orgId, CancellationToken ct)
    {
        var rows = await db.OctorateListingMappings
            .AsNoTracking()
            .Where(m => m.OrgId == orgId)
            .ToListAsync(ct);
        if (rows.Count == 0)
            return [];

        var candidatesByRow = rows.ToDictionary(row => row.Id, row => CandidatesOf(row.CandidatePropertyIdsJson));
        var propertyIds = rows.Where(row => row.PropertyId is not null).Select(row => row.PropertyId!.Value)
            .Concat(candidatesByRow.Values.SelectMany(ids => ids))
            .Distinct()
            .ToList();
        var properties = await db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == orgId && propertyIds.Contains(p.Id))
            .Select(p => new ChannelMappingProperty(p.Id, p.Name, p.Address, p.City))
            .ToDictionaryAsync(p => p.Id, ct);

        return rows
            .OrderBy(row => row.Status switch
            {
                OctorateMappingStatus.Ambiguous => 0,
                OctorateMappingStatus.Proposed => 1,
                OctorateMappingStatus.Confirmed => 2,
                _ => 3,
            })
            .ThenBy(row => row.SnapshotName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.OctorateAccommodationId, StringComparer.Ordinal)
            .Select(row => new ChannelMappingRow(
                row.Id,
                row.OctorateAccommodationId,
                row.SnapshotName,
                row.SnapshotAddress,
                row.SnapshotCity,
                row.SnapshotMaxGuests,
                RoomsOf(row.SnapshotRoomsJson),
                row.OctorateProductId,
                row.Status,
                row.MatchScore,
                row.PropertyId is { } id && properties.TryGetValue(id, out var property) ? property : null,
                [.. candidatesByRow[row.Id].Where(properties.ContainsKey).Select(candidate => properties[candidate])],
                ChannelsOf(row.ChannelIdsJson),
                row.ConfirmedAt))
            .ToList();
    }

    public async Task ConfirmAsync(
        Guid orgId,
        string octorateAccommodationId,
        Guid propertyId,
        string? octorateProductId,
        string userId,
        CancellationToken ct,
        IReadOnlySet<string>? tokenRoles = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var row = await RowAsync(orgId, octorateAccommodationId, ct);

        // The property the listing is proposed or confirmed to is the decision of whoever reaches it, not only the new property's:
        // reaching the new one alone does not take the listing over, and a confirmed listing is not moved at all (it is rejected first).
        // A property that was deleted is no property.
        var linked = row.PropertyId is { } linkedId && linkedId != propertyId ? await FindPropertyAsync(orgId, linkedId, ct) : null;
        if (linked is not null && !await CanReachAsync(orgId, linked, userId, tokenRoles, ct))
            throw PropertyNotFound();

        await EnsureReachableAsync(orgId, propertyId, userId, tokenRoles, ct);
        if (linked is not null && row.Status == OctorateMappingStatus.Confirmed)
            throw LinkedToOtherProperty();

        if (await db.OctorateListingMappings.AnyAsync(
                m => m.OrgId == orgId && m.Status == OctorateMappingStatus.Confirmed && m.PropertyId == propertyId && m.Id != row.Id, ct))
        {
            throw PropertyTaken();
        }

        // The rooms and the identifiers on the channels, as Octorate has them now (never regenerated, D6).
        OctorateRoom[] rooms;
        OctorateChannelConnection[] connections;
        using (orgScope.Use(orgId))
        {
            rooms = await client.GetRoomsAsync(row.OctorateAccommodationId, ct);
            connections = await client.GetConnectionsAsync(row.OctorateAccommodationId, ct);
        }

        ApplyRooms(row, rooms);
        row.OctorateProductId = ChooseRoom(rooms, octorateProductId);
        row.ChannelIdsJson = ChannelIdsJsonOf(connections);
        MarkConfirmed(row, propertyId, userId);
        await ReleaseFromOtherRowsAsync(orgId, propertyId, row.Id, ct);
        await SaveConfirmedAsync(ct);
        await CloseConfirmTodoAsync(row, userId, ct);

        Audit("confirmed", row, userId);
    }

    public async Task SetRoomAsync(
        Guid orgId,
        string octorateAccommodationId,
        string octorateProductId,
        string userId,
        CancellationToken ct,
        IReadOnlySet<string>? tokenRoles = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var row = await RowAsync(orgId, octorateAccommodationId, ct);
        if (row.Status != OctorateMappingStatus.Confirmed || row.PropertyId is not { } propertyId)
            throw new DomainRuleException("octorate_mapping_not_confirmed", "OctorateMappingNotConfirmed");

        await EnsureReachableAsync(orgId, propertyId, userId, tokenRoles, ct);
        if (string.IsNullOrWhiteSpace(octorateProductId))
            throw RoomInvalid();

        OctorateRoom[] rooms;
        using (orgScope.Use(orgId))
            rooms = await client.GetRoomsAsync(row.OctorateAccommodationId, ct);

        ApplyRooms(row, rooms);
        row.OctorateProductId = ChooseRoom(rooms, octorateProductId);
        row.UpdatedAt = Now();
        await db.SaveChangesAsync(ct);

        Audit("room-set", row, userId);
    }

    public async Task RejectAsync(Guid orgId, string octorateAccommodationId, string userId, CancellationToken ct, IReadOnlySet<string>? tokenRoles = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var row = await RowAsync(orgId, octorateAccommodationId, ct);
        if (row.PropertyId is { } propertyId)
        {
            await EnsureReachableAsync(orgId, propertyId, userId, tokenRoles, ct);
        }
        else if (await hostScopeResolver.ResolveAsync(userId, tokenRoles ?? NoTokenRoles, orgId, ct) is not { IsOrgWide: true })
        {
            // A listing with no property is an org-level decision.
            throw MappingNotFound();
        }

        row.Status = OctorateMappingStatus.Rejected;
        row.PropertyId = null;
        row.CandidatePropertyIdsJson = OctorateListingMapping.EmptyJsonArray;
        row.OctorateProductId = null;
        row.ConfirmedByUserId = null;
        row.ConfirmedAt = null;
        row.UpdatedAt = Now();
        await db.SaveChangesAsync(ct);
        await CloseConfirmTodoAsync(row, userId, ct);

        Audit("rejected", row, userId);
    }

    public async Task<Guid> ImportAsPropertyAsync(Guid orgId, string octorateAccommodationId, string userId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var row = await RowAsync(orgId, octorateAccommodationId, ct);
        if (row.Status == OctorateMappingStatus.Confirmed)
            throw new DomainConflictException("octorate_mapping_already_confirmed", "OctorateMappingAlreadyConfirmed");

        if (!await entitlements.CanAddPropertyAsync(orgId, ct))
            throw PlanLimitReached();

        // Read only: the import never creates or changes anything on Octorate (ADR-027-R8).
        OctorateProperty listing;
        OctorateRoom[] rooms;
        OctorateChannelConnection[] connections;
        using (orgScope.Use(orgId))
        {
            listing = await client.GetPropertyAsync(row.OctorateAccommodationId, ct);
            rooms = await client.GetRoomsAsync(row.OctorateAccommodationId, ct);
            connections = await client.GetConnectionsAsync(row.OctorateAccommodationId, ct);
        }

        // The CasaZen create path: the plan limit (count and insert under the org's lock), the CIN and address rules, the tenant.
        var property = NewProperty(orgId, userId, listing, rooms);
        var created = await entitlements.CreatePropertyWithinLimitAsync(orgId, () => propertyService.CreatePropertyAsync(property), ct)
            ?? throw PlanLimitReached();

        ApplySnapshot(row, listing);
        ApplyRooms(row, rooms);
        row.OctorateProductId = ChooseRoom(rooms, null);
        row.ChannelIdsJson = ChannelIdsJsonOf(connections);
        MarkConfirmed(row, created.Id, userId);
        await SaveConfirmedAsync(ct);

        // Inactive until a person checks what was imported (LST-03).
        await activities.CreateAsync(
            new CreateTodoRequest
            {
                Type = ActivityType.CheckImportedListing,
                Action = ActivityAction.CheckImportedListing,
                EntityType = PropertyEntityType,
                EntityId = created.Id,
                PropertyId = created.Id,
                Urgency = ActivityUrgency.Medium,
                Reason = "octorate_listing_imported",
            },
            ct);
        await CloseConfirmTodoAsync(row, userId, ct);

        Audit("imported", row, userId);
        return created.Id;
    }

    public async Task<bool> IsWriteBlockedAsync(Guid propertyId, CancellationToken ct)
    {
        // Called by the content push and the booking actions, also from jobs: the property's own org, read across orgs.
        var orgId = await db.Properties
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter, AppDbContext.SoftDeleteQueryFilter])
            .AsNoTracking()
            .Where(p => p.Id == propertyId)
            .Select(p => (Guid?)p.OrgId)
            .FirstOrDefaultAsync(ct);
        if (orgId is null)
            return true;

        var undecided = await db.OctorateListingMappings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(m => m.OrgId == orgId && (m.Status == OctorateMappingStatus.Proposed || m.Status == OctorateMappingStatus.Ambiguous))
            .Select(m => new { m.PropertyId, m.CandidatePropertyIdsJson })
            .ToListAsync(ct);

        return undecided.Any(m => m.PropertyId == propertyId || CandidatesOf(m.CandidatePropertyIdsJson).Contains(propertyId));
    }

    /// <summary>The room of a confirmed listing: the one asked (it must be a room of the listing), else the only room, else none.</summary>
    internal static string? ChooseRoom(IReadOnlyList<OctorateRoom> rooms, string? requested)
    {
        var units = rooms.Where(room => room.IsRoom).ToList();
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var wanted = requested.Trim();
            return units.Any(room => string.Equals(room.Id, wanted, StringComparison.Ordinal)) ? wanted : throw RoomInvalid();
        }

        return units.Count == 1 ? units[0].Id : null;
    }

    /// <summary>Channel code to the id of the listing on the channel, exactly as Octorate gives them (D6); the first of a channel wins.</summary>
    internal static string ChannelIdsJsonOf(IReadOnlyList<OctorateChannelConnection> connections)
    {
        var ids = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var connection in connections)
        {
            if (!string.IsNullOrWhiteSpace(connection.ChannelId) && !string.IsNullOrWhiteSpace(connection.ExternalId))
                ids.TryAdd(connection.ChannelId, connection.ExternalId);
        }

        return JsonSerializer.Serialize(ids, Json);
    }

    private static Property NewProperty(Guid orgId, string userId, OctorateProperty listing, IReadOnlyList<OctorateRoom> rooms)
    {
        var units = rooms.Where(room => room.IsRoom).ToList();
        var room = units.Count == 1 ? units[0] : units.OrderByDescending(unit => unit.MaxGuests ?? 0).FirstOrDefault();
        return new Property
        {
            OrgId = orgId,
            OwnerId = userId,
            Name = Cut(listing.Name, 100) ?? $"Octorate {listing.Id}",
            Address = Cut(listing.Address, 500) ?? string.Empty,
            City = Cut(listing.City, 50) ?? string.Empty,
            PostalCode = Cut(listing.PostalCode, 10) ?? string.Empty,
            Country = listing.Country is { Length: 2 } country ? country.ToUpperInvariant() : "IT",
            Latitude = listing.Latitude ?? 0,
            Longitude = listing.Longitude ?? 0,
            MaxGuests = Math.Clamp(room?.MaxGuests ?? 0, 0, 100),
            Bedrooms = Math.Clamp(room?.Bedrooms ?? 0, 0, 100),
            Bathrooms = Math.Clamp(room?.Bathrooms ?? 1, 1, 50),
            Beds = room?.Beds is > 0 ? room.Beds : null,
            AreaSqm = room?.AreaSqm is > 0 ? room.AreaSqm : null,
            RentalMode = RentalMode.Short,
            NightlyRate = 0,
            IsActive = false,
            PublishedOnSite = false,
        };
    }

    private void ApplySnapshot(OctorateListingMapping row, OctorateProperty listing)
    {
        row.SnapshotName = Cut(listing.Name, OctorateListingMapping.SnapshotNameMaxLength);
        row.SnapshotAddress = Cut(listing.Address, OctorateListingMapping.SnapshotAddressMaxLength);
        row.SnapshotCity = Cut(listing.City, OctorateListingMapping.SnapshotCityMaxLength);
        row.UpdatedAt = Now();
    }

    private static void ApplyRooms(OctorateListingMapping row, IReadOnlyList<OctorateRoom> rooms)
    {
        var units = rooms
            .Where(room => room.IsRoom)
            .Select(room => new ChannelMappingRoom(room.Id, Cut(room.Name, OctorateListingMapping.SnapshotNameMaxLength), room.MaxGuests))
            .ToList();
        row.SnapshotRoomsJson = JsonSerializer.Serialize(units, Json);
        row.SnapshotMaxGuests = units.Max(unit => unit.MaxGuests);
    }

    private void MarkConfirmed(OctorateListingMapping row, Guid propertyId, string userId)
    {
        var now = Now();
        row.Status = OctorateMappingStatus.Confirmed;
        row.PropertyId = propertyId;
        row.CandidatePropertyIdsJson = OctorateListingMapping.EmptyJsonArray;
        row.ConfirmedByUserId = Cut(userId, OctorateListingMapping.UserIdMaxLength);
        row.ConfirmedAt = now;
        row.UpdatedAt = now;
    }

    /// <summary>
    /// A property confirmed to one listing is no longer the proposal or a candidate of the other open listings of the org: they would
    /// block its writes for nothing. An ambiguous listing left without candidates is offered for import.
    /// </summary>
    private async Task ReleaseFromOtherRowsAsync(Guid orgId, Guid propertyId, Guid confirmedRowId, CancellationToken ct)
    {
        var open = await db.OctorateListingMappings
            .Where(m => m.OrgId == orgId
                        && m.Id != confirmedRowId
                        && (m.Status == OctorateMappingStatus.Proposed || m.Status == OctorateMappingStatus.Ambiguous))
            .ToListAsync(ct);
        foreach (var other in open)
        {
            var changed = false;
            if (other.PropertyId == propertyId)
            {
                other.PropertyId = null;
                changed = true;
            }

            var candidates = CandidatesOf(other.CandidatePropertyIdsJson);
            if (candidates.Remove(propertyId))
            {
                other.CandidatePropertyIdsJson = JsonSerializer.Serialize(candidates, Json);
                changed = true;
            }

            if (other.Status == OctorateMappingStatus.Ambiguous && candidates.Count == 0)
            {
                other.Status = OctorateMappingStatus.Proposed;
                changed = true;
            }

            if (changed)
                other.UpdatedAt = Now();
        }
    }

    private async Task SaveConfirmedAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (OrgTeamAccess.IsUniqueViolation(ex, OctorateListingMapping.ConfirmedPropertyIndexName))
        {
            // Confirmed to another listing by a parallel request: the index is the guarantee.
            throw PropertyTaken();
        }
    }

    /// <summary>The row of the listing in the org's connection of this environment.</summary>
    private async Task<OctorateListingMapping> RowAsync(Guid orgId, string octorateAccommodationId, CancellationToken ct)
    {
        var accommodationId = octorateAccommodationId?.Trim();
        if (string.IsNullOrEmpty(accommodationId) || accommodationId.Length > OctorateListingMapping.AccommodationIdMaxLength)
            throw MappingNotFound();

        var environment = Environment;
        var connectionId = await db.OctorateConnections
            .Where(c => c.OrgId == orgId && c.Environment == environment)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);
        if (connectionId is null)
            throw MappingNotFound();

        return await db.OctorateListingMappings.FirstOrDefaultAsync(
                m => m.OrgId == orgId && m.ConnectionId == connectionId.Value && m.OctorateAccommodationId == accommodationId, ct)
            ?? throw MappingNotFound();
    }

    /// <summary>
    /// The property belongs to the org and the person reaches it (org team in the database; the roles of the token only for a person in no
    /// org team, as the web layer decided it): 404 otherwise.
    /// </summary>
    private async Task EnsureReachableAsync(Guid orgId, Guid propertyId, string userId, IReadOnlySet<string>? tokenRoles, CancellationToken ct)
    {
        var property = await FindPropertyAsync(orgId, propertyId, ct);
        if (property is null || !await CanReachAsync(orgId, property, userId, tokenRoles, ct))
            throw PropertyNotFound();
    }

    /// <summary>A property of the org with the creator the reach of an account in no org team depends on; <c>null</c> when it is not the org's or was deleted.</summary>
    private Task<PropertyRef?> FindPropertyAsync(Guid orgId, Guid propertyId, CancellationToken ct) =>
        db.Properties
            .AsNoTracking()
            .Where(p => p.Id == propertyId && p.OrgId == orgId)
            .Select(p => new PropertyRef(p.Id, p.OwnerId))
            .FirstOrDefaultAsync(ct);

    private Task<bool> CanReachAsync(Guid orgId, PropertyRef property, string userId, IReadOnlySet<string>? tokenRoles, CancellationToken ct) =>
        hostScopeResolver.CanReachPropertyAsync(userId, tokenRoles ?? NoTokenRoles, orgId, property.Id, property.OwnerId, ct);

    /// <summary>The properties of the org a listing can be proposed for: not deleted, and not confirmed to another listing already.</summary>
    private async Task<IReadOnlyList<MatchProperty>> CandidatesAsync(Guid orgId, CancellationToken ct)
    {
        var confirmed = await db.OctorateListingMappings
            .AsNoTracking()
            .Where(m => m.OrgId == orgId && m.Status == OctorateMappingStatus.Confirmed && m.PropertyId != null)
            .Select(m => m.PropertyId!.Value)
            .ToListAsync(ct);
        var properties = await db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == orgId && !confirmed.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.Address, p.City, p.PostalCode, p.Latitude, p.Longitude, p.MaxGuests })
            .ToListAsync(ct);

        return properties
            .Select(p => new MatchProperty(
                p.Id,
                p.Name,
                p.Address,
                p.City,
                p.PostalCode,
                p.Latitude == 0 && p.Longitude == 0 ? null : p.Latitude,
                p.Latitude == 0 && p.Longitude == 0 ? null : p.Longitude,
                p.MaxGuests > 0 ? p.MaxGuests : null))
            .ToList();
    }

    private async Task RaiseConfirmTodoAsync(OctorateListingMapping row, CancellationToken ct) =>
        await activities.CreateAsync(
            new CreateTodoRequest
            {
                Type = ActivityType.ConfirmChannelMapping,
                Action = ActivityAction.ConfirmChannelMapping,
                EntityType = MappingEntityType,
                EntityId = row.Id,
                OrgId = row.OrgId,
                Urgency = ActivityUrgency.Medium,
                Reason = "octorate_mapping_ambiguous",
            },
            ct);

    private async Task CloseConfirmTodoAsync(OctorateListingMapping row, string closedBy, CancellationToken ct)
    {
        var type = nameof(ActivityType.ConfirmChannelMapping);
        var open = await db.InternalActivities
            .AsNoTracking()
            .Where(a => a.OrgId == row.OrgId
                        && a.Action != null
                        && a.CompletedAt == null
                        && a.ActivityType == type
                        && a.EntityType == MappingEntityType
                        && a.EntityId == row.Id)
            .Select(a => a.Id)
            .ToListAsync(ct);

        foreach (var id in open)
            await activities.CloseAsync(id, closedBy, ct);
    }

    private void Audit(string action, OctorateListingMapping row, string userId) =>
        logger.LogInformation(
            AuditEvent,
            "Octorate mapping {Action}: mapping {MappingId} of org {OrgId}, property {PropertyId}, room set {RoomSet}, by user {UserId}",
            action, row.Id, row.OrgId, row.PropertyId, row.OctorateProductId is not null, userId);

    private static List<Guid> CandidatesOf(string json) => Deserialize<List<Guid>>(json) ?? [];

    private static IReadOnlyList<ChannelMappingRoom> RoomsOf(string json) => Deserialize<List<ChannelMappingRoom>>(json) ?? [];

    private static IReadOnlyList<ChannelMappingChannel> ChannelsOf(string json) =>
        (Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>())
        .Select(entry => new ChannelMappingChannel(entry.Key, entry.Value))
        .ToList();

    private static T? Deserialize<T>(string? json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Cut(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    private static NotFoundException MappingNotFound() =>
        new("Octorate mapping not found.") { Code = "octorate_mapping_not_found", MessageKey = "OctorateMappingNotFound" };

    private static NotFoundException PropertyNotFound() =>
        new("Property not found for the Octorate mapping.") { Code = "octorate_property_not_found", MessageKey = "OctoratePropertyNotFound" };

    private static DomainConflictException PropertyTaken() =>
        new("octorate_mapping_property_taken", "OctorateMappingPropertyTaken");

    private static DomainConflictException LinkedToOtherProperty() =>
        new("octorate_mapping_linked_to_other_property", "OctorateMappingLinkedToOtherProperty");

    private static DomainRuleException RoomInvalid() => new("octorate_room_invalid", "OctorateRoomInvalid");

    private static DomainConflictException PlanLimitReached() => new("plan_limit_reached", "PlanLimitReached");

    /// <summary>A property of the org and the user who created it, which decides the reach of an account in no org team.</summary>
    private sealed record PropertyRef(Guid Id, string OwnerId);
}
