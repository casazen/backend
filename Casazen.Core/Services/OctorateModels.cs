using System.Text.Json.Serialization;

namespace Casazen.Core.Services;

/// <summary>
/// The application's configuration as Octorate reports it (<c>GET /rest/v1/api/configuration</c>).
/// </summary>
/// <param name="Environment">Which environment the credentials belong to: <c>SANDBOX</c> or <c>PRODUCTION</c>.</param>
/// <param name="QuotaLimit">The quota of calls of the application (it depends on the category Octorate assigned).</param>
/// <param name="QuotaStatus">What is left of the quota as Octorate counts it (not a simple countdown, seen in the sandbox).</param>
/// <param name="Category">The application category Octorate assigned (read-only for CasaZen), empty when none.</param>
public sealed record OctorateConfiguration(string? Environment, int? QuotaLimit, int? QuotaStatus, string? Category);

/// <summary>
/// An accommodation as Octorate describes it (<c>GET /rest/v1/accommodation</c>, <c>GET /rest/v1/accommodation/{acc}</c>): only the
/// members CasaZen uses. The contact person, e-mail, phone and tax codes Octorate also returns are not read.
/// </summary>
public sealed record OctorateProperty
{
    /// <summary>The accommodation id (<c>id</c>), the key of every other call about it.</summary>
    public required string Id { get; init; }

    public string? Name { get; init; }

    /// <summary>Street and number (<c>address</c>, else <c>location.address</c>).</summary>
    public string? Address { get; init; }

    public string? City { get; init; }

    /// <summary>The postal code (<c>zipCode</c>).</summary>
    public string? PostalCode { get; init; }

    /// <summary>The country code (<c>country</c>, ISO alpha-2).</summary>
    public string? Country { get; init; }

    /// <summary>The coordinates; <c>null</c> when Octorate has none (a 0,0 pair is read as none).</summary>
    public decimal? Latitude { get; init; }

    public decimal? Longitude { get; init; }

    /// <summary>The kind of accommodation (<c>propertyCategory</c>: <c>APARTMENT</c>, <c>HOUSE</c>, ...).</summary>
    public string? Category { get; init; }

    /// <summary>The network the accommodation is reached through (<c>networkInfo.accessNetwork</c>), when it is in one.</summary>
    public string? NetworkId { get; init; }

    /// <summary>The <c>sandboxProperty</c> flag of a test accommodation, when Octorate sends it.</summary>
    public bool? SandboxProperty { get; init; }

    /// <summary>
    /// Whether the connection to the police portal (Alloggiati Web) that the host set on the Octorate panel is valid
    /// (<c>policeAccount.valid</c>). <c>null</c> when the answer does not carry it: Octorate does not expose it for every accommodation (an
    /// accommodation created through the API has no <c>policeAccount</c>, decisions D17 and D31), and then nothing is assumed.
    /// </summary>
    public bool? PoliceAccountValid { get; init; }

    /// <summary>Whether Octorate sends the daily automatic communication for the accommodation (<c>policeAccount.autorun</c>); <c>null</c> when not exposed.</summary>
    public bool? PoliceAutorun { get; init; }

    /// <summary>When Octorate's daily job last ran for the accommodation (<c>policeAccount.lastCron</c>), in UTC; <c>null</c> when not exposed.</summary>
    public DateTime? PoliceLastCron { get; init; }
}

/// <summary>
/// A product of an accommodation (<c>GET /rest/v3/roomrates/{acc}</c>): a room (unit), or a rate derived from one
/// (<see cref="DerivedFromId"/>, Octorate's <c>derivedRule.parent</c>). The rooms, beds and guests the matcher and the import use.
/// </summary>
public sealed record OctorateRoom
{
    /// <summary>The product id (the <c>product</c> of the reservations and of the calendar).</summary>
    public required string Id { get; init; }

    public string? Name { get; init; }

    /// <summary>The adults the room takes (<c>adults</c>).</summary>
    public int? Adults { get; init; }

    /// <summary>The children the room takes besides the adults (<c>children</c>).</summary>
    public int? Children { get; init; }

    /// <summary>Bedrooms (<c>bedroomQuantity</c>).</summary>
    public int? Bedrooms { get; init; }

    /// <summary>Bathrooms (<c>bathroomQuantity</c>).</summary>
    public int? Bathrooms { get; init; }

    /// <summary>Beds (<c>bedQuantity</c>).</summary>
    public int? Beds { get; init; }

    /// <summary>Square meters (<c>squareMetersSize</c>).</summary>
    public int? AreaSqm { get; init; }

    /// <summary>The room this product is a rate of (<c>derivedRule.parent</c>); <c>null</c> for a room.</summary>
    public string? DerivedFromId { get; init; }

    /// <summary>True for a room (a unit), false for a rate derived from another product.</summary>
    public bool IsRoom => DerivedFromId is null;

    /// <summary>The guests the room takes, adults and children; <c>null</c> when Octorate says neither.</summary>
    public int? MaxGuests => Adults is null && Children is null ? null : (Adults ?? 0) + (Children ?? 0);
}

/// <summary>
/// A connection of an accommodation to a channel (<c>GET /rest/v1/connection?accommodation={acc}</c>): the identifier the listing
/// already has on that channel, exactly as Octorate gives it (decision D6). The portal credentials of a connection are never asked for.
/// </summary>
/// <param name="Id">The connection id on Octorate.</param>
/// <param name="ChannelId">The channel (portal) code: 142 Booking.com, 274 Airbnb, 235 Vrbo, ...</param>
/// <param name="ChannelName">The channel name as Octorate gives it.</param>
/// <param name="ExternalId">The id of the listing on the channel (<c>hotelId</c>).</param>
/// <param name="Status">The state of the connection as Octorate gives it.</param>
public sealed record OctorateChannelConnection(string? Id, string? ChannelId, string? ChannelName, string? ExternalId, string? Status);

/// <summary>
/// A partial change of an accommodation (<c>PATCH /rest/v2/accommodation/{acc}</c>): only the members that are set are sent, the
/// others stay as they are on Octorate.
/// </summary>
public sealed record OctoratePropertyUpdate
{
    public string? Name { get; init; }

    public string? Address { get; init; }

    public string? City { get; init; }

    public string? ZipCode { get; init; }

    public decimal? Latitude { get; init; }

    public decimal? Longitude { get; init; }

    /// <summary>The first hour of check-in (0-23).</summary>
    public int? CheckinStart { get; init; }

    /// <summary>The last hour of check-in (0-23).</summary>
    public int? CheckinEnd { get; init; }

    /// <summary>The hour of check-out (0-23).</summary>
    public int? Checkout { get; init; }

    /// <summary>True when no member is set: there is nothing to send.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        Name is null && Address is null && City is null && ZipCode is null && Latitude is null && Longitude is null
        && CheckinStart is null && CheckinEnd is null && Checkout is null;
}
