namespace Casazen.Core.Services;

/// <summary>
/// The accommodation CasaZen asks Octorate to create (<c>POST /rest/v2/accommodation</c>, E02/F03/US01/T06, PO-19): the data of the property and of the
/// contact of its organization. It is personal data: <see cref="ToString"/> prints nothing of it and no caller logs it.
/// </summary>
public sealed record OctoratePropertyCreate
{
    public required string Name { get; init; }

    public required string Address { get; init; }

    public required string City { get; init; }

    public required string ZipCode { get; init; }

    /// <summary>The country, ISO alpha-2 (<c>IT</c>): Octorate refuses <c>isoCountry</c> alone, so it is always sent as both.</summary>
    public required string Country { get; init; }

    public required decimal Latitude { get; init; }

    public required decimal Longitude { get; init; }

    /// <summary>The currency of the stays of the property (<c>StayCurrency.ForProperty</c>).</summary>
    public required string Currency { get; init; }

    public string? TimeZone { get; init; }

    /// <summary>The e-mail of the contact of the organization.</summary>
    public required string ContactEmail { get; init; }

    public required string ContactPhone { get; init; }

    /// <summary>First name of the referent: the person who publishes.</summary>
    public required string ContactFirstName { get; init; }

    public required string ContactLastName { get; init; }

    /// <summary>
    /// The CasaZen address Octorate sends the credentials of the user it creates for the property to (<c>welcome_alternative_email</c>, PO-19): the
    /// owner gets nothing from Octorate.
    /// </summary>
    public required string AlternativeEmail { get; init; }

    /// <summary>The network of the organization's existing connection (<c>network_name</c>), when it has one; <c>null</c> = none.</summary>
    public string? NetworkName { get; init; }

    /// <summary>The first hour of check-in (0-23); <c>null</c> = not set.</summary>
    public int? CheckinStart { get; init; }

    public int? CheckinEnd { get; init; }

    public int? Checkout { get; init; }

    /// <summary>Never prints the personal data of the request.</summary>
    public override string ToString() => nameof(OctoratePropertyCreate);
}

/// <summary>
/// What Octorate answered to the creation of an accommodation: its id and the refresh token, which comes <b>once</b> and is the only way to act
/// for the new accommodation (the answer carries no access token). The token is never printed.
/// </summary>
/// <param name="AccommodationId">The id of the new accommodation (<c>codice</c>).</param>
/// <param name="RefreshToken">The refresh token (<c>refresh_token</c>).</param>
public sealed record OctoratePropertyCreated(string AccommodationId, string RefreshToken)
{
    public override string ToString() =>
        $"{nameof(OctoratePropertyCreated)} {{ AccommodationId = {AccommodationId}, RefreshToken = *** }}";
}

/// <summary>A field Octorate refused (<c>machine_name</c> and its message): shown by the wizard on the step that owns the field.</summary>
/// <param name="Field">The <c>machine_name</c> of the field.</param>
/// <param name="Message">What Octorate says about it; <c>null</c> when it says nothing.</param>
public sealed record OctorateFieldError(string Field, string? Message);

/// <summary>
/// The unit (room) of a listing that CasaZen creates once on a new accommodation (<c>POST /rest/v3/roomrates/{acc}</c>, E02/F03/US01/T02): the names
/// and the description in every language of the listing, and the counters of the property.
/// </summary>
public sealed record OctorateRoomCreate
{
    /// <summary>The name in Italian, the default language of the property.</summary>
    public required string Name { get; init; }

    /// <summary>The names in every language of the listing, by the code of the channel manager (<c>IT</c>, <c>EN</c>).</summary>
    public required IReadOnlyDictionary<string, string> RateNames { get; init; }

    public IReadOnlyDictionary<string, string> Descriptions { get; init; } = new Dictionary<string, string>();

    public int Adults { get; init; }

    public int Bedrooms { get; init; }

    public int Beds { get; init; }

    public int Bathrooms { get; init; }

    public int? AreaSqm { get; init; }
}

/// <summary>
/// The descriptions of a unit (<c>/rest/v1/content/products/{acc}/{pid}/descriptions</c>, proven on the sandbox on 2026-11-04): the long description
/// and the house rules as a text for each language, and the directions as one text. Only these members are sent: no field that the sandbox did not
/// accept is invented.
/// </summary>
public sealed record OctorateDescriptions
{
    public IReadOnlyDictionary<string, string> Description { get; init; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> HouseRules { get; init; } = new Dictionary<string, string>();

    public string? Directions { get; init; }

    /// <summary>True when nothing is written: there is nothing to send.</summary>
    public bool IsEmpty => Description.Count == 0 && HouseRules.Count == 0 && string.IsNullOrWhiteSpace(Directions);
}

/// <summary>A kind of bed in a room as the channel manager spells it (<c>DOUBLE</c>) and how many of them.</summary>
public sealed record OctorateContentBed(string Type, int Quantity);

/// <summary>
/// A room of the unit with its beds (<c>/rest/v1/content/products/{acc}/{pid}/rooms</c>, proven on the sandbox): its kind (<c>BEDROOM</c>), its number
/// in the unit, how many guests it sleeps and its beds.
/// </summary>
public sealed record OctorateContentRoom(string Type, int RoomNumber, int MaxGuests, IReadOnlyList<OctorateContentBed> Beds);

/// <summary>
/// The structured rules of the stay (<c>/rest/v1/content/products/{acc}/{pid}/reservation</c>): children, pets, smoking and the hours. A member that
/// is <c>null</c> is not sent, so a rule the host never declared is left as it is on the channel manager. The text of the rules goes in the
/// <c>houseRules</c> of the descriptions, never through the text helpers of the API (NF-37).
/// </summary>
public sealed record OctorateReservationRules
{
    public bool? ChildrenAllowed { get; init; }

    public bool? PetsAllowed { get; init; }

    public bool? SmokersAllowed { get; init; }

    public int? CheckinTimeStart { get; init; }

    public int? CheckinTimeEnd { get; init; }

    public int? CheckoutTime { get; init; }

    /// <summary>True when no member is set.</summary>
    public bool IsEmpty =>
        ChildrenAllowed is null && PetsAllowed is null && SmokersAllowed is null
        && CheckinTimeStart is null && CheckinTimeEnd is null && CheckoutTime is null;
}

/// <summary>
/// The cancellation policy of the property as it is sent (<c>/rest/v1/content/products/{acc}/{pid}/cancellation</c>): sent only when
/// <c>ListingChannels:SendCancellationPolicy</c> is on, because the members of this part were not proven on the sandbox.
/// </summary>
public sealed record OctorateCancellationPolicy(int? FullRefundHours, int? PartialRefundHours, decimal? PartialRefundPercent);

/// <summary>
/// Where the content of a unit is pushed: the portal (<c>booking_xml</c>), whether it has a push queue of its own
/// (<c>pushQueue/{portal}</c>: <c>booking_xml</c>, <c>airbnb_xml</c>, <c>homeaway</c>), and otherwise the connection the push goes through
/// (<c>/rest/v1/content/connections/{connection}/push</c>).
/// </summary>
public sealed record OctorateChannelTarget(string PortalCode, bool UsesQueue, string? ConnectionId);

/// <summary>The answer of a push of the content to a channel: the process whose result tells whether the channel took it (<c>null</c> when none came).</summary>
public sealed record OctorateChannelPush(string? ProcessId);

/// <summary>Where one entry of the photo queue of the channel manager stands.</summary>
public enum OctoratePhotoQueueState
{
    /// <summary>The channel manager has the address and downloads the photo (<c>WAIT</c>).</summary>
    Waiting = 0,

    /// <summary>The photo is on the channel manager (<c>DONE</c>).</summary>
    Done = 1,

    /// <summary>The channel manager could not take it (<c>ERROR</c>).</summary>
    Error = 2,
}

/// <summary>An entry of the photo queue of an accommodation (<c>POST/GET /rest/v1/accommodation/{acc}/photos</c>, <c>.../queue/{id}</c>).</summary>
/// <param name="Id">The entry of the queue, kept to read it again.</param>
/// <param name="State">Where it stands.</param>
/// <param name="PhotoName">The name the channel manager gave the photo once it has it, when the answer says it.</param>
/// <param name="Reason">Why it failed, when it did.</param>
public sealed record OctoratePhotoQueueEntry(string Id, OctoratePhotoQueueState State, string? PhotoName, string? Reason);

/// <summary>Where the publication of the content to a channel stands, as the process of the channel manager says it.</summary>
public enum OctorateProcessState
{
    /// <summary>Still going on (queued, running).</summary>
    Pending = 0,

    /// <summary>The channel took the content.</summary>
    Done = 1,

    /// <summary>The channel refused it, or the process failed.</summary>
    Failed = 2,

    /// <summary>The answer says something CasaZen does not understand: never read as a success.</summary>
    Unknown = 3,
}

/// <summary>The result of a publication process (<c>GET /rest/v1/content/products/{acc}/process/{processId}</c>).</summary>
/// <param name="State">Where it stands.</param>
/// <param name="Reason">Why it failed, when it did (one line, never a body).</param>
public sealed record OctorateContentProcess(OctorateProcessState State, string? Reason);
