using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The application's configuration as Octorate reports it (<c>GET /rest/v1/api/configuration</c>).
/// </summary>
/// <param name="Environment">Which environment the credentials belong to: <c>SANDBOX</c> or <c>PRODUCTION</c>.</param>
/// <param name="QuotaLimit">The quota of calls of the application (it depends on the category Octorate assigned).</param>
/// <param name="QuotaStatus">
/// The units of the quota USED as Octorate counts them, per node, in the window of about five minutes (sandbox S25, <c>docs/requirements/octorate-helix.md</c> §10.5; the
/// specification calls it «the calls left», which the sandbox contradicts). What is left is <paramref name="QuotaLimit"/> less this, never the other way round.
/// </param>
/// <param name="Category">The application category Octorate assigned (read-only for CasaZen), empty when none.</param>
public sealed record OctorateConfiguration(string? Environment, int? QuotaLimit, int? QuotaStatus, string? Category);

public sealed record OctorateProperty
{
    public required string Id { get; init; }

    public string? Name { get; init; }

    public string? Address { get; init; }

    public string? City { get; init; }

    public string? PostalCode { get; init; }

    public string? Country { get; init; }

    public decimal? Latitude { get; init; }

    public decimal? Longitude { get; init; }

    public string? Category { get; init; }

    public string? NetworkId { get; init; }

    public string? BillingNetworkName { get; init; }

    public bool? NetworkAccessEnabled { get; init; }

    public bool? SandboxProperty { get; init; }
}

/// <summary>
/// The police account (Alloggiati Web) the host connected on the Octorate panel for one accommodation, as the API exposes it
/// (<c>GET /rest/v1/accommodation/{acc}?fields=policeAccount</c>, Q-002): only the three members CasaZen reads. The Alloggiati code and the
/// web service key the same object holds are never read. A member Octorate does not send is <c>null</c> and nothing is assumed about it:
/// an accommodation created through the API has no police account at all (decisions D17 and D31), and Octorate never defined
/// <c>valid</c> alone as «Activated».
/// </summary>
public sealed record OctoratePoliceAccount
{
    public bool? Valid { get; init; }

    public bool? Autorun { get; init; }

    public DateTime? LastCron { get; init; }
}

/// <summary>
/// What CasaZen could read of the police account of one accommodation to verify the guided step of the police connection
/// (<c>IOctorateApiClient.GetPoliceAccountStatusAsync</c>, E10/F01/US03/T01, D17, D31). <see cref="Readable"/> is true only when Octorate
/// exposed <c>valid</c>; it is false, and the other members are <c>null</c>, when the field is absent (an accommodation created through the
/// API has no police account: S25), when the read was refused or when Octorate does not know the accommodation. A caller never reads a
/// refusal as «not valid»: not readable is «CasaZen cannot tell».
/// </summary>
/// <param name="Readable">True when <c>valid</c> was read.</param>
/// <param name="Valid">Whether the connection to the police portal is valid; <c>null</c> when not readable.</param>
/// <param name="Autorun">Whether Octorate sends the daily communication; <c>null</c> when not exposed.</param>
/// <param name="LastCron">When Octorate's daily job last ran (UTC); <c>null</c> when not exposed.</param>
public sealed record OctoratePoliceAccountStatus(bool Readable, bool? Valid, bool? Autorun, DateTime? LastCron)
{
    public static OctoratePoliceAccountStatus NotReadable { get; } = new(false, null, null, null);
}

public sealed record OctorateRoom
{
    public required string Id { get; init; }

    public string? Name { get; init; }

    public int? Adults { get; init; }

    public int? Children { get; init; }

    public int? Bedrooms { get; init; }

    public int? Bathrooms { get; init; }

    public int? Beds { get; init; }

    public int? AreaSqm { get; init; }

    public string? DerivedFromId { get; init; }

    public bool IsRoom => DerivedFromId is null;

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

public static class OctoratePortalCategories
{
    public const string Ota = "OTA";

    public const string ExternalTool = "EXTERNAL_TOOL";
}

/// <summary>
/// A portal or tool of the channel manager's catalog (<c>GET /rest/v1/meta/portals</c>, a partner-level call): the data the marketplace
/// caches once a day and the channel costs report reads for the way each portal collects its commission. Only the members CasaZen uses; a
/// flag Octorate does not send is <c>false</c>, never guessed true, except <see cref="CommissionsInvoiced"/>, where "not said" is
/// <c>null</c> and is not the same as no.
/// </summary>
public sealed record OctoratePortal
{
    public const int CodeMaxLength = 100;

    public required string PortalCode { get; init; }

    public required string Name { get; init; }

    public string? PortalCategory { get; init; }

    public bool Creatable { get; init; }

    public bool Enabled { get; init; }

    public bool Preferred { get; init; }

    public string? Website { get; init; }

    public string? Logo { get; init; }

    public bool RequiresHotelId { get; init; }

    public bool RequiresPassword { get; init; }

    public bool RequiresTokenAuthentication { get; init; }

    public bool FeatureContent { get; init; }

    public bool OctorateManageable { get; init; }

    public bool CommissionIncludedInPayments { get; init; }

    public bool? CommissionsInvoiced { get; init; }
}

public sealed record OctoratePropertyUpdate
{
    public string? Name { get; init; }

    public string? Address { get; init; }

    public string? City { get; init; }

    public string? ZipCode { get; init; }

    public decimal? Latitude { get; init; }

    public decimal? Longitude { get; init; }

    public int? CheckinStart { get; init; }

    public int? CheckinEnd { get; init; }

    public int? Checkout { get; init; }

    [JsonIgnore]
    public bool IsEmpty =>
        Name is null && Address is null && City is null && ZipCode is null && Latitude is null && Longitude is null
        && CheckinStart is null && CheckinEnd is null && Checkout is null;
}

public enum OctorateReservationListType
{
    ModifiedBetween = 1,

    CreateTime = 2,

    Cancellation = 3,

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
    public const int MaxPageSize = 200;

    public const int MaxPage = 1000;
}

/// <summary>
/// One page of the list of reservations (<c>GET /rest/v1/reservation/{acc}</c>): the reservations that could be read, and how many items the
/// page held. An item that is not a reservation CasaZen can read (no id, or one longer than the column) is not among
/// <see cref="Reservations"/> but is counted in <see cref="ItemCount"/>. So the end of the list is a page with no item at all, never a page that
/// only looks short (a server may serve fewer than the size asked for, and an unreadable item is still an item), and a page with an item that was
/// left out is not a complete read of the list.
/// </summary>
/// <param name="Reservations">The reservations of the page that could be read.</param>
/// <param name="ItemCount">The items the page held, readable or not.</param>
public sealed record OctorateReservationPage(IReadOnlyList<OctorateReservation> Reservations, int ItemCount)
{
    public static OctorateReservationPage Empty { get; } = new([], 0);

    public int Unreadable => ItemCount - Reservations.Count;

    public static OctorateReservationPage Of(params OctorateReservation[] reservations)
    {
        ArgumentNullException.ThrowIfNull(reservations);

        return new OctorateReservationPage(reservations, reservations.Length);
    }
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
    public const string CommissionType = "COMMISSION";

    public required string Id { get; init; }

    public string? Refer { get; init; }

    public string? ChannelId { get; init; }

    public string? ChannelName { get; init; }

    public string? ChannelRefer { get; init; }

    public string? OtaId { get; init; }

    public OctorateBookingStatus? Status { get; init; }

    public string? Product { get; init; }

    public string? PmsProduct { get; init; }

    public string? CheckIn { get; init; }

    public string? CheckOut { get; init; }

    public DateTime? UpdatedAt { get; init; }

    public DateTime? CancelledAt { get; init; }

    public int? TotalGuests { get; init; }

    public int? Adults { get; init; }

    public int? Children { get; init; }

    public int? Infants { get; init; }

    public string? FirstName { get; init; }

    public string? LastName { get; init; }

    public const int GuestIdMaxLength = 64;

    public string? GuestId { get; init; }

    public string? InvoiceHolderId { get; init; }

    public bool? GuestEmailAvailable { get; init; }

    public bool? GuestPhoneAvailable { get; init; }

    public string? GuestEmail { get; init; }

    public string? GuestPhone { get; init; }

    /// <summary>What the guest wrote with the booking, when Octorate gives it. Never the private notes of the host.</summary>
    public string? Notes { get; init; }

    public string? Currency { get; init; }

    public decimal? RoomGross { get; init; }

    public decimal? TotalGross { get; init; }

    public decimal? TouristTax { get; init; }

    public decimal? TotalPaid { get; init; }

    public string? PaymentStatus { get; init; }

    public const string CompanyCollectCompany = "COMPANY";

    public const string CompanyCollectHotel = "HOTEL";

    /// <summary>
    /// Who collects the payment (<c>companyCollect</c>, E08/F03/US03/T02, NF-77), upper case: <see cref="CompanyCollectCompany"/>, <see cref="CompanyCollectHotel"/>
    /// or <c>NONE</c> (the OpenAPI writes <c>NONE|COMPANY|HOTEl</c>). <c>null</c> when the answer carries none, which is what the sandbox samples do, and for
    /// a word that is none of the three: it is never guessed.
    /// </summary>
    public string? CompanyCollect { get; init; }

    public bool? CommissionIncludedInPayments { get; init; }

    public IReadOnlyList<OctoratePriceComponent> PriceBreakdown { get; init; } = [];

    public IReadOnlyList<string> PaymentTransactions { get; init; } = [];

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

/// <summary>
/// One guest of a reservation to create or update on Octorate (E03/F01/US01/T03, NF-41): data fields only. There is no member that can carry an
/// image, a stream or bytes, so no document photo can ever be sent (AC-NF41-4 holds at compile time). The values are the ones of the Alloggiati
/// record CasaZen keeps (<c>StayGuest</c>): the police code of the document type, the birth and residence places as names (Octorate accepts a
/// complete guest only with names it recognizes). Never logged.
/// </summary>
/// <remarks>
/// The member names of the body are the ones of the guest example of Octorate the sandbox accepted on 2026-11-04 (<c>familyName</c>,
/// <c>givenName</c>, <c>accommodatedType</c>, <c>birthDate</c>, <c>birthCountry</c>, <c>birthCity</c>, <c>residenceCountry</c>, <c>city</c>,
/// <c>citizenship</c>, <c>documentType</c>, <c>documentCode</c>, <c>documentIssuePlace</c>, <c>documentIssueDate</c>, <c>documentExpire</c>,
/// <c>sex</c>, <c>draft</c>); the tax members (<c>cityTaxPrice</c>, <c>cityTaxExemption</c>, <c>excludeCityTax</c>) were accepted and not read
/// back (Q-004).
/// </remarks>
public sealed record OctorateCheckInGuestUpsert
{
    public required int Position { get; init; }

    public string? OctorateGuestId { get; init; }

    /// <summary>
    /// <c>false</c> only when the channel manager is the sender of the property (Octorate transmits to the police); <c>true</c> otherwise. The
    /// sandbox ignores it (the guest is read back with <c>draft = false</c>).
    /// </summary>
    public required bool Draft { get; init; }

    /// <summary>The kind of guest of the Alloggiati record as Octorate names it (<c>SINGLE_GUEST</c>, ...).</summary>
    public required string AccommodatedType { get; init; }

    public required string FamilyName { get; init; }
    public required string GivenName { get; init; }

    public string? Sex { get; init; }

    public DateOnly? BirthDate { get; init; }

    /// <summary><c>IT</c> for a guest born in Italy, otherwise the state of birth as entered.</summary>
    public string? BirthCountry { get; init; }

    public string? BirthCity { get; init; }

    /// <summary><c>IT</c> for an Italian citizen, otherwise the citizenship as entered.</summary>
    public string? Citizenship { get; init; }

    /// <summary><c>IT</c> for a guest living in Italy, otherwise the state of residence as entered.</summary>
    public string? ResidenceCountry { get; init; }

    public string? ResidenceCity { get; init; }

    public string? DocumentType { get; init; }

    public string? DocumentCode { get; init; }

    public string? DocumentIssuePlace { get; init; }
    public DateOnly? DocumentIssueDate { get; init; }
    public DateOnly? DocumentExpire { get; init; }

    public DateOnly? CheckIn { get; init; }
    public DateOnly? CheckOut { get; init; }

    public decimal? CityTaxPrice { get; init; }

    public string? CityTaxExemption { get; init; }

    public bool? ExcludeCityTax { get; init; }
}

/// <summary>The outcome of one guest of <see cref="IOctorateApiClient.UpsertCheckInGuestsAsync"/>.</summary>
/// <param name="Position">The position of the guest in the stay.</param>
/// <param name="OctorateGuestId">The id of the guest on Octorate (the one created, or the one updated); null when it failed.</param>
/// <param name="Created">True when the guest was created by this call.</param>
/// <param name="Validated">What Octorate answered for <c>validated</c>; null when the answer does not say.</param>
/// <param name="ErrorCode">A stable code when the guest failed (<c>octorate_auth</c>, <c>octorate_guest_rejected</c>, ...); null on success.</param>
public sealed record OctorateCheckInGuestResult(int Position, string? OctorateGuestId, bool Created, bool? Validated, string? ErrorCode)
{
    public bool Succeeded => ErrorCode is null && OctorateGuestId is not null;
}

/// <summary>The arrival of a reservation as Octorate reads it back after <c>CHECKIN_END</c>.</summary>
/// <param name="EffectiveCheckin">When the arrival was registered (<c>effectiveCheckin</c>, UTC); null when Octorate has none.</param>
/// <param name="ArrivalTime">The expected arrival time of the reservation (<c>arrivalTime</c>, at most 10 characters); null when none.</param>
public sealed record OctorateReservationArrival(DateTime? EffectiveCheckin, string? ArrivalTime);

/// <summary>
/// The contact data of one guest of the guest registry (<c>POST /rest/v1/guests/{acc}/{guestId}/contactInfo</c>, E07/F06/US02/T02): the e-mail
/// address and the phone number, as Octorate holds them, each <c>null</c> when it has none (an Airbnb guest has none). <b>Sensitive</b>: it is
/// never logged, and <see cref="ToString"/> says nothing of it, so a log line that formats the record cannot leak it.
/// </summary>
/// <param name="Email">The e-mail address; for a booking made through a portal it is a forward address.</param>
/// <param name="Phone">The phone number.</param>
/// <param name="Recognized">
/// False when the answer was not understood: it was not an object, or it had none of the members that hold an address or a number (another
/// wrapper, other names). Then <paramref name="Email"/> and <paramref name="Phone"/> are <c>null</c> because nothing could be read, not because
/// Octorate holds nothing: the caller must not take it for "the guest has no contact". An answer that has the members and leaves them empty is
/// recognized.
/// </param>
public sealed record OctorateGuestContactInfo(string? Email, string? Phone, bool Recognized = true)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Email) && string.IsNullOrWhiteSpace(Phone);

    public override string ToString() => nameof(OctorateGuestContactInfo) + " { contact data not shown }";
}

public enum OctorateCheckInStatus
{
    CheckInEnd = 1,

    CheckOutEnd = 2,

    NoShow = 3,
}

/// <summary>
/// One day of one product of the calendar of an accommodation (<c>GET /rest/v1/calendar/{acc}</c>, E02/F02/US01/T03): the availability, the
/// price and the restrictions Octorate holds for that day. The calendar read is used for the closed nights; the amount of the price is
/// only used to tell a day that was never opened from a day that is sold out (<see cref="IsClosed"/>).
/// </summary>
/// <param name="ProductId">The product (room) the day is about.</param>
/// <param name="Date">The day (calendar date of the accommodation).</param>
/// <param name="Availability">Units available (<c>availability</c>); <c>null</c> when the answer does not say.</param>
/// <param name="Price">The price of the night (<c>price</c>); <c>null</c> when the answer does not say.</param>
/// <param name="StopSell">The day is closed for sale (<c>stopSells</c>).</param>
/// <param name="MinStay">Minimum nights (<c>minstay</c>).</param>
/// <param name="MaxStay">Maximum nights (<c>maxstay</c>).</param>
/// <param name="CloseToArrival">No arrival on that day (<c>closeToArrival</c>).</param>
/// <param name="CloseToDeparture">No departure on that day (<c>closeToDeparture</c>).</param>
public sealed record OctorateCalendarDay(
    string ProductId,
    DateOnly Date,
    int? Availability,
    decimal? Price,
    bool? StopSell,
    int? MinStay,
    int? MaxStay,
    bool? CloseToArrival,
    bool? CloseToDeparture)
{
    /// <summary>
    /// The night is not for sale: stopped, or no unit available on a day that is on sale. A day with no unit and no price is a day Octorate
    /// holds but nobody opened (a structure whose calendar was never filled, or cannot be written for lack of a licence, shows availability 0
    /// and price 0 on every day, sandbox runs of 2026-11-04): no channel can sell it, so there is nothing to protect and it is not closed. A day
    /// that does not say whether it has units or is stopped is not closed. The restrictions of an open day (minimum stay, no arrival, no
    /// departure) close nothing.
    /// </summary>
    public bool IsClosed => StopSell == true || (Availability is <= 0 && Price is not <= 0);

    /// <summary>
    /// The answer says something about the sale of the day: how many units are available, or whether the day is stopped. A day with neither
    /// (only its date, or its date and a price) is not a reading of the calendar: it cannot tell an open night from a closed one, so it is no proof
    /// that the night is open and no proof that Octorate runs the room.
    /// </summary>
    public bool HasSignal => Availability is not null || StopSell is not null;

    /// <summary>
    /// The day was opened in Octorate. An open day needs an explicit signal (<see cref="HasSignal"/>); with one, it is opened unless it has no
    /// unit, no price and is not stopped, which is how a calendar never filled shows (or one that cannot be written for lack of a licence). A
    /// calendar with no opened day at all is not a live calendar of the room: it says nothing about the nights, so the calendar sync reads
    /// nothing from it and does not take it as proof that Octorate has taken over from the iCal feeds.
    /// </summary>
    public bool IsOpened => HasSignal && !(Availability is <= 0 && Price is <= 0 && StopSell != true);
}
