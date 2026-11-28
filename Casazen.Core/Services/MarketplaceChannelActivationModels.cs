using System.Text;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public static class MarketplaceActivationRequirements
{
    public const string HotelId = "hotelId";

    public const string Credentials = "credentials";

    public const string PanelAccess = "panelAccess";
}

public static class MarketplaceActivationErrorCodes
{
    public const string OctorateNotConnected = "marketplace_octorate_not_connected";

    public const string PropertyNotLinked = "marketplace_property_not_linked";

    public const string PortalUnknown = "marketplace_portal_unknown";

    public const string PortalNotCreatable = "marketplace_portal_not_creatable";

    public const string CredentialsRequired = "marketplace_credentials_required";

    public const string ConnectionExists = "marketplace_connection_exists";

    public const string ActivationNotFound = "marketplace_activation_not_found";

    public const string ConnectionMissing = "marketplace_connection_missing";

    public const string PanelAccessPending = "marketplace_panel_access_pending";

    public const string MappingRequired = "marketplace_mapping_required";

    public const string MappingInvalid = "marketplace_mapping_invalid";

    public const string ConnectionNotEnabled = "connection_not_enabled";

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
    MarketplaceActivationStatus? ActivationStatus,
    string? VerifiedHotelId = null,
    bool ManagedByStaff = false);

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
