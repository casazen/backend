using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// A connection of an accommodation to a channel, as the org's Octorate token sees it (<c>GET /rest/v1/connection</c>, E02/F01/US03): the
/// ids that tie a channel to an accommodation, and whether the channel took it (<see cref="IsAuthorized"/>). Only what the import needs: the
/// portal username and password of a connection are never asked for.
/// </summary>
/// <param name="Id">The connection id (a number, as Octorate gives it).</param>
/// <param name="ChannelId">The channel (portal) code: 142 Booking.com, 274 Airbnb, 235 Vrbo, ...</param>
/// <param name="AccommodationId">The accommodation the connection belongs to.</param>
/// <param name="ExternalId">The id of the listing on the channel (<c>hotelId</c>), when Octorate has it.</param>
/// <param name="Status">The state of the connection as Octorate gives it (<c>DISABLED</c>, <c>WAITING</c>, <c>ENABLED</c>).</param>
/// <param name="UserLogged">
/// Octorate's flag <c>userLogged</c>, kept as Octorate gives it; <c>null</c> when not said. The specification says it means that the person logged
/// in and the connection is authorized, but it is no proof of an access: see <see cref="IsAuthorized"/>.
/// </param>
/// <param name="LoginAt">When the person logged in on the channel, as Octorate says it (<c>loginAt</c>, UTC); <c>null</c> when not said. It tells a
/// login made after a hand-over to the panel from an authorization that was there before it.</param>
public sealed record OctorateConnectionSummary(
    string Id,
    string ChannelId,
    string AccommodationId,
    string? ExternalId,
    string? Status,
    bool? UserLogged,
    DateTime? LoginAt = null)
{
    /// <summary>The <c>status</c> of a connection that works.</summary>
    public const string EnabledStatus = "ENABLED";

    /// <summary>
    /// The access on the channel was given: the connection is <c>ENABLED</c> (the status is read trimmed and without regard to case), and nothing
    /// else counts. A connection that is waiting, disabled or silent is not an access given, whatever <c>userLogged</c> says.
    /// </summary>
    /// <remarks>
    /// <c>userLogged</c> is not taken for the access because the sandbox showed it is true when the OTA accepted nothing. On 13/11/2026 a connection
    /// was created through the API on the test structure 520897, with exactly the body CasaZen sends (<c>OctorateApiClient.ConnectionCreateBody</c>)
    /// and a <c>hotelId</c> that does not exist on Booking.com: Octorate answered 200 with <c>status: "WAITING"</c>, <c>userLogged: true</c> and an empty
    /// <c>lastPortalAcceptLog</c>, although Booking.com never approved anything. Only <c>ENABLED</c> says the channel took the connection.
    /// </remarks>
    public bool IsAuthorized => string.Equals(Status?.Trim(), EnabledStatus, StringComparison.OrdinalIgnoreCase);
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
