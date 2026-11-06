using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;

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

/// <summary>
/// The kinds of list of <c>GET /rest/v1/reservation/{acc}</c> (<c>type</c>). The import and the reconciliation read only
/// <see cref="ModifiedBetween"/>; what each of the others selects is not settled by the sandbox yet.
/// </summary>
public enum OctorateReservationListType
{
    /// <summary><c>MODIFIED_BETWEEN</c>: the reservations changed between the two dates.</summary>
    ModifiedBetween = 1,

    /// <summary><c>CREATETIME</c>.</summary>
    CreateTime = 2,

    /// <summary><c>CANCELLATION</c>.</summary>
    Cancellation = 3,

    /// <summary><c>STAY</c>.</summary>
    Stay = 4,
}

/// <summary>
/// One page of the list of reservations of an accommodation (<c>GET /rest/v1/reservation/{acc}</c>, E02/F02/US01/T01): the dates are
/// Rome dates, the page starts at 0 and has at most <see cref="MaxPageSize"/> reservations, there are at most <see cref="MaxPage"/> + 1 pages.
/// </summary>
/// <param name="Type">What the dates select.</param>
/// <param name="From">First day (Europe/Rome), included.</param>
/// <param name="To">Last day (Europe/Rome).</param>
/// <param name="Page">The page, from 0.</param>
/// <param name="PageSize">Reservations in a page, 1 to <see cref="MaxPageSize"/>.</param>
public sealed record OctorateReservationQuery(
    OctorateReservationListType Type,
    DateOnly From,
    DateOnly To,
    int Page = 0,
    int PageSize = OctorateReservationQuery.MaxPageSize)
{
    /// <summary>The most reservations Octorate puts in a page.</summary>
    public const int MaxPageSize = 200;

    /// <summary>The last page Octorate serves.</summary>
    public const int MaxPage = 1000;
}

/// <summary>
/// A line of the price breakdown of a reservation (<c>priceBreakdown</c> of the single read): its <c>type</c> (<c>ROOM_NET</c>, <c>VAT</c>,
/// <c>COMMISSION</c>, <c>TOURIST_TAX</c>, ...) and the amount when the line has one.
/// </summary>
public sealed record OctoratePriceComponent(string Type, decimal? Amount);

/// <summary>
/// A reservation as Octorate describes it (<c>GET /rest/v1/reservation/{acc}</c> and <c>.../{id}</c>, <c>ApiReservationRespDTO</c>, E02/F02/US01/T01):
/// only the members the import uses. The list gives the identification, the dates, the state and <see cref="UpdatedAt"/>; the single
/// read adds <see cref="TotalPaid"/> and <see cref="PriceBreakdown"/>. The contact values of the guest are not in the answer (only flags),
/// so <see cref="GuestEmail"/> and <see cref="GuestPhone"/> are empty unless the answer carries them. Never logged.
/// </summary>
public sealed record OctorateReservation
{
    /// <summary>The type of the lines of the price breakdown that are the commission of the channel.</summary>
    public const string CommissionType = "COMMISSION";

    /// <summary>The reservation id (<c>id</c>), the key of the import.</summary>
    public required string Id { get; init; }

    /// <summary>The group of reservations it belongs to (<c>refer</c>: several rooms of one booking).</summary>
    public string? Refer { get; init; }

    /// <summary>The channel (portal) code (<c>channelId</c>): 142 Booking.com, 274 Airbnb, 235 Vrbo, 4 Expedia, 270 the booking engine of the agency.</summary>
    public string? ChannelId { get; init; }

    /// <summary>The name Octorate gives the channel (<c>channelName</c>).</summary>
    public string? ChannelName { get; init; }

    /// <summary>The reference of the booking on the channel (<c>channelRefer</c>).</summary>
    public string? ChannelRefer { get; init; }

    /// <summary>The id of the booking on the OTA (<c>otaId</c>).</summary>
    public string? OtaId { get; init; }

    /// <summary><c>WAITING</c>, <c>CONFIRMED</c> or <c>CANCELLED</c>; <c>null</c> for a value that is none of them.</summary>
    public OctorateBookingStatus? Status { get; init; }

    /// <summary>The unit (room) the reservation is on (<c>product</c>).</summary>
    public string? Product { get; init; }

    /// <summary>The PMS room of the unit (<c>pmsProduct</c>).</summary>
    public string? PmsProduct { get; init; }

    /// <summary>Check-in as Octorate wrote it (<c>checkin</c>): an instant in UTC, or a date.</summary>
    public string? CheckIn { get; init; }

    /// <summary>Check-out as Octorate wrote it (<c>checkout</c>): an instant in UTC, or a date.</summary>
    public string? CheckOut { get; init; }

    /// <summary>When the reservation last changed on Octorate (<c>updateTime</c>, UTC); the order of the versions of a reservation.</summary>
    public DateTime? UpdatedAt { get; init; }

    /// <summary>When the reservation was cancelled (<c>cancelledTime</c>, UTC), when it was.</summary>
    public DateTime? CancelledAt { get; init; }

    /// <summary>All the guests (<c>totalGuest</c>).</summary>
    public int? TotalGuests { get; init; }

    public int? Adults { get; init; }

    public int? Children { get; init; }

    public int? Infants { get; init; }

    public string? FirstName { get; init; }

    public string? LastName { get; init; }

    /// <summary>The e-mail of the guest, only when the answer carries one (it normally does not).</summary>
    public string? GuestEmail { get; init; }

    /// <summary>The phone of the guest, only when the answer carries one (it normally does not).</summary>
    public string? GuestPhone { get; init; }

    /// <summary>What the guest wrote with the booking, when Octorate gives it. Never the private notes of the host.</summary>
    public string? Notes { get; init; }

    /// <summary>The currency of the reservation (<c>currency</c>), when Octorate says it.</summary>
    public string? Currency { get; init; }

    public decimal? RoomGross { get; init; }

    public decimal? TotalGross { get; init; }

    public decimal? TouristTax { get; init; }

    /// <summary>What was paid (<c>totalPaid</c>, single read only).</summary>
    public decimal? TotalPaid { get; init; }

    /// <summary><c>UNPAID</c>, <c>PARTIALLY_PAID</c>, <c>PAID_DEPOSIT</c> or <c>PAID</c>.</summary>
    public string? PaymentStatus { get; init; }

    /// <summary>The price breakdown (single read only).</summary>
    public IReadOnlyList<OctoratePriceComponent> PriceBreakdown { get; init; } = [];

    /// <summary>
    /// The sum of the <see cref="CommissionType"/> lines that have an amount; <c>null</c> when there is none. The only source of the
    /// commission: the reservation has no top-level commission, net or balance (sandbox and OpenAPI, 2026-11-04).
    /// </summary>
    public decimal? Commission
    {
        get
        {
            var lines = PriceBreakdown
                .Where(l => l.Amount is not null && string.Equals(l.Type, CommissionType, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return lines.Count == 0 ? null : lines.Sum(l => l.Amount!.Value);
        }
    }
}

/// <summary>The states CasaZen sends with the check-in status operation (<c>PUT /rest/v1/checkin/{acc}/{id}?status=</c>).</summary>
public enum OctorateCheckInStatus
{
    /// <summary><c>CHECKIN_END</c>: the guest arrived.</summary>
    CheckInEnd = 1,

    /// <summary><c>CHECKOUT_END</c>: the guest left.</summary>
    CheckOutEnd = 2,

    /// <summary><c>NO_SHOW</c>: the guest never came.</summary>
    NoShow = 3,
}
