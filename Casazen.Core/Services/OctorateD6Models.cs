using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// A connection of an accommodation to a channel, as the org's Octorate token sees it (<c>GET /rest/v1/connection</c>, E02/F01/US03): the
/// ids that tie a channel to an accommodation, and whether the person gave the access on the channel. Only what the import needs: the
/// portal username and password of a connection are never asked for.
/// </summary>
/// <param name="Id">The connection id (a number, as Octorate gives it).</param>
/// <param name="ChannelId">The channel (portal) code: 142 Booking.com, 274 Airbnb, 235 Vrbo, ...</param>
/// <param name="AccommodationId">The accommodation the connection belongs to.</param>
/// <param name="ExternalId">The id of the listing on the channel (<c>hotelId</c>), when Octorate has it.</param>
/// <param name="Status">The state of the connection as Octorate gives it (<c>DISABLED</c>, <c>WAITING</c>, <c>ENABLED</c>).</param>
/// <param name="UserLogged">Octorate's flag that the person logged in and the connection is authorized to operate; <c>null</c> when not said.</param>
public sealed record OctorateConnectionSummary(
    string Id,
    string ChannelId,
    string AccommodationId,
    string? ExternalId,
    string? Status,
    bool? UserLogged)
{
    /// <summary>The <c>status</c> of a connection that works.</summary>
    public const string EnabledStatus = "ENABLED";

    /// <summary>
    /// The access on the channel was given: Octorate says the person logged in, or the connection is enabled. A connection that is only
    /// waiting, disabled or silent is not an access given.
    /// </summary>
    public bool IsAuthorized =>
        UserLogged == true || string.Equals(Status?.Trim(), EnabledStatus, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What an operation of the D6 sequence reported (<c>POST /rest/v1/connection/{action}/{acc}/{connection}</c>, E02/F01/US03): the answer
/// is a list of the mappings between Octorate products and external rooms the action touched. Only ids; never the payload.
/// </summary>
/// <param name="Operation">The operation that was asked.</param>
/// <param name="ConnectionId">The connection it was asked for.</param>
/// <param name="Mappings">The mappings the answer listed (empty is a valid answer: nothing to map).</param>
public sealed record OctorateConnectionActionResult(
    OctorateD6Operation Operation,
    string ConnectionId,
    IReadOnlyList<OctorateImportedMapping> Mappings);

/// <summary>A product (room) of Octorate and the external room it is mapped to, as an operation of the D6 sequence listed it.</summary>
/// <param name="ProductId">The Octorate product id.</param>
/// <param name="ExternalId">The id of the room on the channel.</param>
public sealed record OctorateImportedMapping(string ProductId, string ExternalId);
