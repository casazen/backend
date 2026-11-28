using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Turns the listings found on the channel manager into a confirmed mapping to the CasaZen properties of the org, and imports the ones
/// CasaZen does not have yet (E02/F01/US02/T03, ADR-023, ADR-027). Nothing is written to a property until a person confirms its
/// mapping (OCT-04); the identifiers a listing already has on its channels are kept exactly as Octorate gives them (D6); the import
/// never creates anything on Octorate (ADR-027-R8).
/// </summary>
/// <remarks>
/// <b>Reach of the person.</b> <c>ConfirmAsync</c>, <c>SetRoomAsync</c> and <c>RejectAsync</c> check the reach of <c>userId</c> as the org
/// team decides it. A person with no membership row (an owner from before the team, a test account) is decided by the roles of its
/// token, which the caller hands over as <c>tokenRoles</c> exactly as the web layer decided it; without them (<c>null</c>) such a person
/// reaches only the properties it created.
/// </remarks>
public interface IChannelMappingService
{
    Task<IReadOnlyList<ChannelMappingRow>> RefreshAsync(Guid orgId, CancellationToken ct);

    Task<IReadOnlyList<ChannelMappingRow>> ListAsync(Guid orgId, CancellationToken ct);

    /// <summary>
    /// Confirms the listing to a property of the org within the reach of <paramref name="userId"/>, not confirmed to another listing;
    /// reads the channel identifiers and the rooms from Octorate. The room is set when the listing has exactly one, or to
    /// <paramref name="octorateProductId"/> when given (it must be a room of the listing). The property the listing is already proposed
    /// or confirmed to must be within the reach of <paramref name="userId"/> too (404 otherwise), and a listing confirmed to another
    /// property is not moved: it is rejected first (409 <c>octorate_mapping_linked_to_other_property</c>).
    /// </summary>
    Task ConfirmAsync(
        Guid orgId,
        string octorateAccommodationId,
        Guid propertyId,
        string? octorateProductId,
        string userId,
        CancellationToken ct,
        IReadOnlySet<string>? tokenRoles = null);

    Task SetRoomAsync(
        Guid orgId,
        string octorateAccommodationId,
        string octorateProductId,
        string userId,
        CancellationToken ct,
        IReadOnlySet<string>? tokenRoles = null);

    Task RejectAsync(Guid orgId, string octorateAccommodationId, string userId, CancellationToken ct, IReadOnlySet<string>? tokenRoles = null);

    /// <summary>
    /// Creates the CasaZen property of a listing CasaZen does not have, through the property create path (plan limit, CIN and tenant
    /// rules), inactive and with a "check the imported listing" To do, and confirms the mapping at once. Never creates anything on
    /// Octorate. Returns the id of the new property.
    /// </summary>
    Task<Guid> ImportAsPropertyAsync(Guid orgId, string octorateAccommodationId, string userId, CancellationToken ct);

    Task<bool> IsWriteBlockedAsync(Guid propertyId, CancellationToken ct);
}

/// <summary>A listing found on the channel manager and its mapping, as the mapping screen shows it. No personal data.</summary>
/// <param name="Id">The row.</param>
/// <param name="OctorateAccommodationId">The accommodation id on Octorate.</param>
/// <param name="Name">The name of the listing on Octorate.</param>
/// <param name="Address">The address of the listing on Octorate.</param>
/// <param name="City">The city of the listing on Octorate.</param>
/// <param name="MaxGuests">The most guests a room of the listing takes, when known.</param>
/// <param name="Rooms">The rooms of the listing (rates derived from a room are not listed), when read.</param>
/// <param name="OctorateProductId">The Octorate room of the confirmed listing, <c>null</c> until set.</param>
/// <param name="Status">Proposed, Confirmed, Ambiguous or Rejected.</param>
/// <param name="MatchScore">The score of the best candidate, 0 to 100.</param>
/// <param name="Property">The proposed or confirmed property; <c>null</c> for an ambiguous row or a listing offered for import.</param>
/// <param name="Candidates">The candidates of an ambiguous row.</param>
/// <param name="Channels">The channels the listing is connected to and its id on each, as read from Octorate.</param>
/// <param name="ConfirmedAt">When a person confirmed the mapping (UTC).</param>
public sealed record ChannelMappingRow(
    Guid Id,
    string OctorateAccommodationId,
    string? Name,
    string? Address,
    string? City,
    int? MaxGuests,
    IReadOnlyList<ChannelMappingRoom> Rooms,
    string? OctorateProductId,
    OctorateMappingStatus Status,
    int MatchScore,
    ChannelMappingProperty? Property,
    IReadOnlyList<ChannelMappingProperty> Candidates,
    IReadOnlyList<ChannelMappingChannel> Channels,
    DateTime? ConfirmedAt)
{
    public bool OfferedForImport => Status == OctorateMappingStatus.Proposed && Property is null;
}

public sealed record ChannelMappingRoom(string Id, string? Name, int? MaxGuests);

public sealed record ChannelMappingProperty(Guid Id, string Name, string Address, string City);

public sealed record ChannelMappingChannel(string ChannelId, string ExternalId);
