using System.Text;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The things a portal asks of a person to be connected, as the stable codes the activation wizard translates (the same codes as the detail card of
/// the marketplace): they come from the flags of the portal in the daily cache, never from a name written in the code.
/// </summary>
public static class MarketplaceActivationRequirements
{
    /// <summary>The identifier of the property on the portal (<c>requiresHotelId</c>).</summary>
    public const string HotelId = "hotelId";

    /// <summary>The username and password of the portal (<c>requiresPassword</c>).</summary>
    public const string Credentials = "credentials";

    /// <summary>The access given by the person on the channel manager's own panel (<c>requiresTokenAuthentication</c>).</summary>
    public const string PanelAccess = "panelAccess";
}

/// <summary>
/// The stable codes of the activation of a channel or a tool (E09/F02/US01/T02, NF-56). The first six are the ones of the brief; each is a
/// <c>DomainRuleException</c> (422), a <c>DomainConflictException</c> (409) or a <c>NotFoundException</c> (404) with a message in
/// <c>SharedResources.resx</c>, Italian and English. The last two are not answers: they are the codes kept in
/// <c>MarketplaceActivation.LastErrorCode</c> when the connection is found not to work.
/// </summary>
public static class MarketplaceActivationErrorCodes
{
    /// <summary>409: the Octorate connector is off, or the org has no connected account in this environment.</summary>
    public const string OctorateNotConnected = "marketplace_octorate_not_connected";

    /// <summary>422: the property has no confirmed listing on the channel manager (the mapping of the connect wizard).</summary>
    public const string PropertyNotLinked = "marketplace_property_not_linked";

    /// <summary>404: the code is not a channel (or a tool) of the catalog, or the catalog does not offer it.</summary>
    public const string PortalUnknown = "marketplace_portal_unknown";

    /// <summary>422: Octorate does not let a connection to the portal be created through its API.</summary>
    public const string PortalNotCreatable = "marketplace_portal_not_creatable";

    /// <summary>422: the portal needs the identifier of the property, or the username and password, and they are missing.</summary>
    public const string CredentialsRequired = "marketplace_credentials_required";

    /// <summary>409: the property already has a connection to the channel on the channel manager that CasaZen did not make.</summary>
    public const string ConnectionExists = "marketplace_connection_exists";

    /// <summary>404: the activation is not one of the org inside the caller's scope.</summary>
    public const string ActivationNotFound = "marketplace_activation_not_found";

    /// <summary>409: the activation has no connection on the channel manager (never made, or removed there): start it again.</summary>
    public const string ConnectionMissing = "marketplace_connection_missing";

    /// <summary>409: the channel needs the access given on the channel manager's panel, and the person has not given it yet.</summary>
    public const string PanelAccessPending = "marketplace_panel_access_pending";

    /// <summary>422: no room of the channel was chosen (an empty choice is never «all of them»), or none is mapped yet when the activation asks for it.</summary>
    public const string MappingRequired = "marketplace_mapping_required";

    /// <summary>422: a room chosen is not a room of the channel, or a product chosen is not a product of the property's listing.</summary>
    public const string MappingInvalid = "marketplace_mapping_invalid";

    /// <summary>Kept on the activation: after «mark connected» the channel manager does not show the connection as working.</summary>
    public const string ConnectionNotEnabled = "connection_not_enabled";

    /// <summary>Kept on the activation: after «mark connected» the channel manager no longer lists the connection.</summary>
    public const string ConnectionLost = "connection_missing";
}

/// <summary>
/// What the activation of a channel or a tool needs for one property, and where it stands (<c>GET api/marketplace/activations/plan</c>). It carries
/// no address of the channel manager's panel: the access on the panel is the guided step <c>channelAccess</c> of the activation epic.
/// </summary>
/// <param name="Kind"><c>Channel</c> or <c>Tool</c>.</param>
/// <param name="PortalCode">The code of the portal in the catalog.</param>
/// <param name="Name">The name of the portal as the catalog has it.</param>
/// <param name="PropertyId">The property the plan is for.</param>
/// <param name="Requirements">What the portal asks of the person, in the order of the wizard: <see cref="MarketplaceActivationRequirements"/>.</param>
/// <param name="Creatable">False when Octorate does not let a connection to the portal be created through its API: the wizard cannot start.</param>
/// <param name="PropertyLinked">True when the property has a confirmed listing on the channel manager: without it the wizard first leads to the connect wizard.</param>
/// <param name="ActivationId">The activation of this property and portal that already exists, to resume it; <c>null</c> when there is none.</param>
/// <param name="ActivationStatus">Its status; <c>null</c> when there is none.</param>
public sealed record MarketplaceActivationPlan(
    MarketplaceActivationKind Kind,
    string PortalCode,
    string Name,
    Guid PropertyId,
    IReadOnlyList<string> Requirements,
    bool Creatable,
    bool PropertyLinked,
    Guid? ActivationId,
    MarketplaceActivationStatus? ActivationStatus);

/// <summary>
/// The start of an activation: the portal, the property and the access data the portal asked for. The username and password are request-only: they
/// go to the channel manager in one call and are never stored, never logged and never part of an answer; <see cref="object.ToString"/> hides them.
/// </summary>
/// <param name="Kind"><c>Channel</c> for a sales channel (an <c>OTA</c> portal), <c>Tool</c> for a connected tool (an <c>EXTERNAL_TOOL</c> portal).</param>
/// <param name="PortalCode">The code of the portal in the catalog.</param>
/// <param name="PropertyId">The property to connect.</param>
/// <param name="HotelId">The identifier of the property on the portal, when the portal asks for it.</param>
/// <param name="Username">The username on the portal, when the portal asks for it.</param>
/// <param name="Password">The password on the portal, when the portal asks for it.</param>
public sealed record MarketplaceActivationStart(
    MarketplaceActivationKind Kind,
    string PortalCode,
    Guid PropertyId,
    string? HotelId,
    string? Username,
    string? Password)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder
            .Append("Kind = ").Append(Kind)
            .Append(", PortalCode = ").Append(PortalCode)
            .Append(", PropertyId = ").Append(PropertyId)
            .Append(", Credentials = (hidden)");
        return true;
    }
}

/// <summary>
/// Where an activation stands after a step or a read. <see cref="Status"/> is the one of the card (what CasaZen stored); the rest is what the
/// channel manager said when the state was read. A read changes nothing.
/// </summary>
/// <param name="ActivationId">The activation.</param>
/// <param name="Kind"><c>Channel</c> or <c>Tool</c>.</param>
/// <param name="PortalCode">The portal.</param>
/// <param name="PropertyId">The property.</param>
/// <param name="Status">Pending until the channel manager shows the connection working; Active then; To fix when it did not or the channel failed.</param>
/// <param name="ConnectionFound">True when the channel manager lists the connection of the activation.</param>
/// <param name="ConnectionStatus">The status of the connection as the channel manager gives it (<c>WAITING</c>, <c>ENABLED</c>...); <c>null</c> when not found.</param>
/// <param name="PanelAccessDone">True when the person gave the access on the channel (so a wizard that sent the person to the panel can go on).</param>
/// <param name="LastSyncedAt">When CasaZen last read the state from the channel manager in a step of the activation (UTC).</param>
/// <param name="LastErrorCode">The code of the last problem found, shown in simple words by the client; <c>null</c> when there is none.</param>
/// <param name="ConnectionLastUpdate">When the channel last answered about the connection, as the channel manager says it (UTC); <c>null</c> when not said.</param>
public sealed record MarketplaceActivationState(
    Guid ActivationId,
    MarketplaceActivationKind Kind,
    string PortalCode,
    Guid PropertyId,
    MarketplaceActivationStatus Status,
    bool ConnectionFound,
    string? ConnectionStatus,
    bool PanelAccessDone,
    DateTime? LastSyncedAt,
    string? LastErrorCode,
    DateTime? ConnectionLastUpdate);

/// <summary>A room of the channel the person chose to map to a product of the listing.</summary>
/// <param name="ChannelRoomId">The id of the room on the channel as the mapping read gives it.</param>
/// <param name="ProductId">The product (room) of the property's listing on the channel manager.</param>
public sealed record MarketplaceRoomChoice(string ChannelRoomId, string ProductId);

/// <summary>A room of the listing on the channel, with what it is mapped to and the proposal of CasaZen.</summary>
/// <param name="ChannelRoomId">The id of the room on the channel as the channel manager gives it.</param>
/// <param name="Name">The name of the room on the channel.</param>
/// <param name="RateName">The name of its rate, when it has one.</param>
/// <param name="Manageable">False for a room the channel manager cannot manage: shown for information, never mapped.</param>
/// <param name="MappedProductId">The product it is mapped to now; <c>null</c> while it is not mapped.</param>
/// <param name="ProposedProductId">The product whose name is the same (normalised); <c>null</c> when none is, or when the name is not unique: the person chooses.</param>
public sealed record MarketplaceChannelRoom(
    string ChannelRoomId,
    string Name,
    string? RateName,
    bool Manageable,
    string? MappedProductId,
    string? ProposedProductId);

/// <summary>A product (room) of the property's listing on the channel manager.</summary>
/// <param name="ProductId">The product id.</param>
/// <param name="Name">Its name, when the channel manager gives one.</param>
public sealed record MarketplaceProduct(string ProductId, string? Name);

/// <summary>The rooms of the channel and the products of the listing, for the mapping step.</summary>
/// <param name="ActivationId">The activation.</param>
/// <param name="PropertyProductId">The product the property is confirmed to, when the connect wizard set one; <c>null</c> otherwise.</param>
/// <param name="ChannelRooms">The rooms of the listing on the channel.</param>
/// <param name="Products">The products of the property's listing on the channel manager.</param>
public sealed record MarketplaceActivationMapping(
    Guid ActivationId,
    string? PropertyProductId,
    IReadOnlyList<MarketplaceChannelRoom> ChannelRooms,
    IReadOnlyList<MarketplaceProduct> Products);
