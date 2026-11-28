namespace Casazen.Core.Services;

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

    public required string Currency { get; init; }

    public string? TimeZone { get; init; }

    public required string ContactEmail { get; init; }

    public required string ContactPhone { get; init; }

    public required string ContactFirstName { get; init; }

    public required string ContactLastName { get; init; }

    public required string AlternativeEmail { get; init; }

    public string? NetworkName { get; init; }

    public string? BillingNetworkName { get; init; }

    public bool AcceptDuplicateContactEmail { get; init; }

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

public sealed record OctorateRoomCreate
{
    public required string Name { get; init; }

    public required IReadOnlyDictionary<string, string> RateNames { get; init; }

    public IReadOnlyDictionary<string, string> Descriptions { get; init; } = new Dictionary<string, string>();

    public int Adults { get; init; }

    public int Bedrooms { get; init; }

    public int Beds { get; init; }

    public int Bathrooms { get; init; }

    public int? AreaSqm { get; init; }
}

public sealed record OctorateDescriptions
{
    public IReadOnlyDictionary<string, string> Description { get; init; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> HouseRules { get; init; } = new Dictionary<string, string>();

    public string? Directions { get; init; }

    public bool IsEmpty => Description.Count == 0 && HouseRules.Count == 0 && string.IsNullOrWhiteSpace(Directions);
}

public sealed record OctorateContentBed(string Type, int Quantity);

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

public sealed record OctorateChannelPush(string? ProcessId);

public enum OctoratePhotoQueueState
{
    Waiting = 0,

    Done = 1,

    Error = 2,
}

public sealed record OctoratePhotoQueueEntry(string Id, OctoratePhotoQueueState State, string? PhotoName, string? Reason);

public enum OctorateProcessState
{
    Pending = 0,

    Done = 1,

    Failed = 2,

    Unknown = 3,
}

/// <summary>The result of a publication process (<c>GET /rest/v1/content/products/{acc}/process/{processId}</c>).</summary>
/// <param name="State">Where it stands.</param>
/// <param name="Reason">Why it failed, when it did (one line, never a body).</param>
public sealed record OctorateContentProcess(OctorateProcessState State, string? Reason);
