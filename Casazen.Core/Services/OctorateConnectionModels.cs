using System.Text;

namespace Casazen.Core.Services;

/// <summary>
/// One connection of an accommodation to a channel or a tool, as <c>GET /rest/v1/connection</c> gives it (E09/F02/US01/T01, NF-56, NF-57): what
/// the activation wizard and the health panel need, and nothing else. The portal username and password a connection holds are never asked for
/// (the client names the members it wants), so they cannot reach CasaZen.
/// </summary>
/// <remarks>
/// The names of the members are the ones of the OpenAPI of 2026-10-31 (S13). The sandbox has no connection to any real channel, so the shape of
/// <c>lastPortalAcceptLog</c> (where <see cref="LastUpdate"/> and <see cref="LastError"/> come from) is not proven: a member the client does not
/// understand is <c>null</c>, never a guess.
/// </remarks>
/// <param name="ConnectionId">The connection id (a number, as Octorate gives it).</param>
/// <param name="PortalCode">The portal (channel or tool) the connection is to: the <c>PortalCode</c> of the marketplace catalog (<c>channelId</c>).</param>
/// <param name="Status">The state as Octorate gives it (<c>DISABLED</c>, <c>WAITING</c>, <c>ENABLED</c>); <c>null</c> when not said.</param>
/// <param name="LastUpdate">
/// The latest moment Octorate gives for the connection (UTC): the moment of the last log of the channel, else when the person logged in; <c>null</c> when it
/// gives none.
/// </param>
/// <param name="LastError">
/// One line of the channel's own words when its last log reports a failure; <c>null</c> when it reports none or when the log cannot be read.
/// Text of the channel: shown only after it is turned into simple words, and never logged.
/// </param>
/// <param name="UserLogged">Octorate's flag that the person gave the access on the channel; <c>null</c> when not said.</param>
/// <param name="LoginAt">When the person logged in on the channel (UTC); <c>null</c> when not said.</param>
/// <param name="UpdateCalendar">Whether Octorate sends calendar, prices and availability to the channel; <c>null</c> when not said.</param>
/// <param name="PullReservations">Whether Octorate pulls the reservations of the channel; <c>null</c> when not said.</param>
public sealed record OctorateConnectionInfo(
    string ConnectionId,
    string PortalCode,
    string? Status,
    DateTime? LastUpdate,
    string? LastError,
    bool? UserLogged = null,
    DateTime? LoginAt = null,
    bool? UpdateCalendar = null,
    bool? PullReservations = null)
{
    /// <summary>The <c>status</c> of a connection that works.</summary>
    public const string EnabledStatus = OctorateConnectionSummary.EnabledStatus;

    /// <summary>Octorate shows the connection as working: its status is <c>ENABLED</c>. Nothing else is taken for connected.</summary>
    public bool IsConnected => string.Equals(Status?.Trim(), EnabledStatus, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The person gave the access on the channel: Octorate says the person logged in, or the connection already works. A connection that is only
    /// waiting, disabled or silent is not an access given.
    /// </summary>
    public bool PanelAccessDone => UserLogged == true || IsConnected;

    /// <summary>
    /// The pulls of the connection are on: Octorate sends calendar, prices and availability to the channel and pulls its reservations (both reported
    /// <c>true</c>; a member Octorate does not say is not «on»). A connection is created with both off and the activation wizard switches them on after
    /// «mark connected», once a room is mapped (D6): <c>ENABLED</c> shows the access given, the pulls show the activation finished.
    /// </summary>
    public bool PullsAreOn => UpdateCalendar == true && PullReservations == true;
}

/// <summary>
/// What it takes to create a connection (<c>POST /rest/v1/connection/{acc}</c>). The portal username and password are used for that one call: they
/// are not stored, not logged and not part of any message, and <see cref="object.ToString"/> hides them.
/// </summary>
/// <param name="PortalCode">The portal code of the catalog: the number Octorate calls <c>channelId</c>.</param>
/// <param name="HotelId">The identifier of the property on the portal, when the portal asks for it; <c>null</c> otherwise.</param>
/// <param name="Username">The username on the portal, when the portal asks for it; <c>null</c> otherwise.</param>
/// <param name="Password">The password on the portal, when the portal asks for it; <c>null</c> otherwise.</param>
public sealed record OctorateConnectionCreate(string PortalCode, string? HotelId, string? Username, string? Password)
{
    /// <summary>The record prints its portal and never its credentials (a log of the object or an exception that names it would leak them).</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("PortalCode = ").Append(PortalCode).Append(", Credentials = (hidden)");
        return true;
    }
}

/// <summary>
/// A partial change of a connection (<c>PATCH /rest/v1/connection/{acc}/{connection}</c>): only the members that are set are sent, the others stay
/// as they are on Octorate. The settings of the synchronization and the portal access data; the credentials are never printed by
/// <see cref="object.ToString"/>.
/// </summary>
public sealed record OctorateConnectionPatch
{
    /// <summary>Octorate sends calendar, prices and availability to the channel.</summary>
    public bool? UpdateCalendar { get; init; }

    /// <summary>Octorate pulls the reservations of the channel.</summary>
    public bool? PullReservations { get; init; }

    /// <summary>Octorate pulls only the reservations of the rooms that are mapped.</summary>
    public bool? PullMappedOnly { get; init; }

    /// <summary>Octorate sends the content of the listing (texts, photos) to the channel.</summary>
    public bool? Content { get; init; }

    /// <summary>Octorate e-mails the account when the connection fails.</summary>
    public bool? MailOnError { get; init; }

    /// <summary>The share of the availability that is sent to the channel (0 to 100).</summary>
    public int? AvailabilityPercentage { get; init; }

    /// <summary>The price correction applied to the channel, as a percentage.</summary>
    public decimal? CorrectionRatio { get; init; }

    /// <summary>The price correction is a fixed amount, not a percentage.</summary>
    public bool? CorrectionFixed { get; init; }

    /// <summary>The corrected price is rounded.</summary>
    public bool? CorrectionRound { get; init; }

    /// <summary>The identifier of the property on the portal.</summary>
    public string? HotelId { get; init; }

    /// <summary>The username on the portal.</summary>
    public string? Username { get; init; }

    /// <summary>The password on the portal.</summary>
    public string? Password { get; init; }

    /// <summary>True when no member is set: there is nothing to send.</summary>
    public bool IsEmpty =>
        UpdateCalendar is null && PullReservations is null && PullMappedOnly is null && Content is null && MailOnError is null
        && AvailabilityPercentage is null && CorrectionRatio is null && CorrectionFixed is null && CorrectionRound is null
        && HotelId is null && Username is null && Password is null;

    /// <summary>The record prints the settings and never the credentials.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder
            .Append("UpdateCalendar = ").Append(UpdateCalendar)
            .Append(", PullReservations = ").Append(PullReservations)
            .Append(", PullMappedOnly = ").Append(PullMappedOnly)
            .Append(", Content = ").Append(Content)
            .Append(", MailOnError = ").Append(MailOnError)
            .Append(", Credentials = (hidden)");
        return true;
    }
}

/// <summary>
/// The operations a connection runs (<c>POST /rest/v1/connection/{action}/{acc}/{connection}</c>, E09/F02/US01/T01). None of them creates or
/// publishes a listing on the channel. Documented by Octorate, not run on a real channel (the sandbox has none).
/// </summary>
public enum OctorateConnectionAction
{
    /// <summary><c>IMPORT_ROOMS</c>: Octorate reads the rooms of the existing listing on the channel, ready to be mapped.</summary>
    ImportRooms = 1,

    /// <summary><c>IMPORT_RESERVATIONS</c>: Octorate reads the future reservations of the channel.</summary>
    ImportReservations = 2,

    /// <summary><c>IMPORT_CALENDAR</c>: Octorate reads the calendar of the channel.</summary>
    ImportCalendar = 3,

    /// <summary><c>MARK_CONNECTED</c>: tells Octorate, and the channel when needed, that the connection is ready.</summary>
    MarkConnected = 4,

    /// <summary><c>COMMIT_MAPPING_AS_COMPLETED</c>: tells Octorate the mapping of the rooms is complete.</summary>
    CommitMappingAsCompleted = 5,
}

/// <summary>
/// A room (an external product: room and rate) of the listing on the channel, as <c>GET /rest/v1/connection/external/{acc}/{connection}</c> lists
/// it, with the Octorate product it is mapped to when there is one (<c>GET /rest/v1/connection/mapping/{acc}/{connection}</c>).
/// </summary>
/// <param name="ChannelRoomId">The id Octorate gives the external product: the <c>externalId</c> of the mapping.</param>
/// <param name="Name">The name of the room on the channel.</param>
/// <param name="MappedProductId">The Octorate product the room is mapped to; <c>null</c> while it is not mapped.</param>
/// <param name="RateName">The name of the rate on the channel, when the product has one.</param>
/// <param name="Manageable">False for a room Octorate cannot manage: it is shown for information and never mapped.</param>
public sealed record OctorateChannelRoom(
    string ChannelRoomId,
    string Name,
    string? MappedProductId,
    string? RateName = null,
    bool Manageable = true);

/// <summary>The mapping of a room of the channel to an Octorate product (<c>POST /rest/v1/connection/mapping/{acc}/{connection}</c>).</summary>
/// <param name="ProductId">The Octorate product (room) id.</param>
/// <param name="ChannelRoomId">The id of the room on the channel as Octorate gives it (<see cref="OctorateChannelRoom.ChannelRoomId"/>).</param>
public sealed record OctorateRoomMapping(string ProductId, string ChannelRoomId);
