using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// <c>GET api/marketplace/activations/plan</c> (E09/F02/US01/T02, NF-56): what the activation of a channel or a tool needs for one property.
/// <c>requirements</c> are the codes <c>hotelId</c> (the identifier of the property on the channel), <c>credentials</c> (username and password) and
/// <c>panelAccess</c> (the access given on the channel manager's panel), in the order of the wizard. <c>propertyLinked</c> false leads to the connect
/// wizard first; <c>activationId</c> resumes an activation that exists. No address of the panel: that is the guided step <c>channelAccess</c>.
/// </summary>
public sealed record MarketplaceActivationPlanDto(
    MarketplaceActivationKind Kind,
    string Code,
    string Name,
    Guid PropertyId,
    IReadOnlyList<string> Requirements,
    bool Creatable,
    bool PropertyLinked,
    Guid? ActivationId,
    MarketplaceActivationStatus? ActivationStatus)
{
    public static MarketplaceActivationPlanDto From(MarketplaceActivationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new MarketplaceActivationPlanDto(
            plan.Kind,
            plan.PortalCode,
            plan.Name,
            plan.PropertyId,
            plan.Requirements,
            plan.Creatable,
            plan.PropertyLinked,
            plan.ActivationId,
            plan.ActivationStatus);
    }
}

/// <summary>
/// Starts the activation (<c>POST api/marketplace/activations</c>). <c>hotelId</c>, <c>username</c> and <c>password</c> are request-only: the channel
/// manager gets them in one call, CasaZen stores none of them, logs none of them and no answer carries them. Only what the portal asks for is used.
/// </summary>
public sealed class StartMarketplaceActivationRequest
{
    /// <summary><c>Channel</c> or <c>Tool</c>.</summary>
    [Required(ErrorMessage = "MarketplaceActivationKindRequired")]
    public MarketplaceActivationKind? Kind { get; set; }

    /// <summary>The code of the portal in the catalog.</summary>
    [Required(ErrorMessage = "MarketplacePortalUnknown")]
    [MaxLength(100, ErrorMessage = "MarketplacePortalUnknown")]
    public string? Code { get; set; }

    /// <summary>The property to connect.</summary>
    [Required(ErrorMessage = "OctorateMappingPropertyRequired")]
    public Guid? PropertyId { get; set; }

    /// <summary>The identifier of the property on the portal, when the portal asks for it.</summary>
    [MaxLength(100, ErrorMessage = "MarketplaceCredentialsTooLong")]
    public string? HotelId { get; set; }

    /// <summary>The username on the portal, when the portal asks for it.</summary>
    [MaxLength(200, ErrorMessage = "MarketplaceCredentialsTooLong")]
    public string? Username { get; set; }

    /// <summary>The password on the portal, when the portal asks for it.</summary>
    [MaxLength(200, ErrorMessage = "MarketplaceCredentialsTooLong")]
    public string? Password { get; set; }

    /// <summary>The start the service takes; the validation attributes made sure that the kind, the code and the property are there.</summary>
    public MarketplaceActivationStart ToStart() =>
        new(Kind ?? default, Code ?? string.Empty, PropertyId ?? Guid.Empty, HotelId, Username, Password);
}

/// <summary>
/// Where an activation stands (<c>GET api/marketplace/activations/{id}</c> and the answer of every step). <c>status</c> is the one of the card;
/// <c>connectionFound</c> and <c>connectionStatus</c> are what the channel manager said when the state was read; <c>panelAccessDone</c> is true once
/// the person gave the access on the channel; <c>lastErrorCode</c> is a code the client turns into simple words. No credential, ever.
/// </summary>
public sealed record MarketplaceActivationStateDto(
    Guid ActivationId,
    MarketplaceActivationKind Kind,
    string Code,
    Guid PropertyId,
    MarketplaceActivationStatus Status,
    bool ConnectionFound,
    string? ConnectionStatus,
    bool PanelAccessDone,
    DateTime? LastSyncedAt,
    string? LastErrorCode,
    DateTime? ConnectionLastUpdate)
{
    public static MarketplaceActivationStateDto From(MarketplaceActivationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new MarketplaceActivationStateDto(
            state.ActivationId,
            state.Kind,
            state.PortalCode,
            state.PropertyId,
            state.Status,
            state.ConnectionFound,
            state.ConnectionStatus,
            state.PanelAccessDone,
            state.LastSyncedAt,
            state.LastErrorCode,
            state.ConnectionLastUpdate);
    }
}

/// <summary>
/// <c>GET api/marketplace/activations/{id}/mapping</c>: the rooms of the listing on the channel with what each is mapped to and the proposal of
/// CasaZen (<c>proposedProductId</c>, the product with the same name; <c>null</c> when there is none: the person chooses), and the products of the
/// property's listing. A room with <c>manageable</c> false is shown for information and is never mapped.
/// </summary>
public sealed record MarketplaceActivationMappingDto(
    Guid ActivationId,
    string? PropertyProductId,
    IReadOnlyList<MarketplaceChannelRoomDto> ChannelRooms,
    IReadOnlyList<MarketplaceProductDto> Products)
{
    public static MarketplaceActivationMappingDto From(MarketplaceActivationMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return new MarketplaceActivationMappingDto(
            mapping.ActivationId,
            mapping.PropertyProductId,
            [
                .. mapping.ChannelRooms.Select(room => new MarketplaceChannelRoomDto(
                    room.ChannelRoomId,
                    room.Name,
                    room.RateName,
                    room.Manageable,
                    room.MappedProductId,
                    room.ProposedProductId)),
            ],
            [.. mapping.Products.Select(product => new MarketplaceProductDto(product.ProductId, product.Name))]);
    }
}

/// <summary>A room of the listing on the channel.</summary>
public sealed record MarketplaceChannelRoomDto(
    string ChannelRoomId,
    string Name,
    string? RateName,
    bool Manageable,
    string? MappedProductId,
    string? ProposedProductId);

/// <summary>A product (room) of the property's listing on the channel manager.</summary>
public sealed record MarketplaceProductDto(string ProductId, string? Name);

/// <summary>
/// Saves the mapping (<c>PUT api/marketplace/activations/{id}/mapping</c>): the rooms of the channel the person chose, each with the product of the
/// listing. An empty or missing list is a 422, never «all the rooms».
/// </summary>
public sealed class SaveMarketplaceMappingRequest
{
    /// <summary>The choices: one room of the channel and the product it is mapped to.</summary>
    public List<MarketplaceRoomChoiceRequest>? Mappings { get; set; }
}

/// <summary>One room of the channel and the product of the listing it is mapped to.</summary>
public sealed class MarketplaceRoomChoiceRequest
{
    /// <summary>The id of the room on the channel, as the mapping read gave it.</summary>
    [Required(ErrorMessage = "MarketplaceMappingInvalid")]
    [MaxLength(64, ErrorMessage = "MarketplaceMappingInvalid")]
    public string ChannelRoomId { get; set; } = string.Empty;

    /// <summary>The product (room) of the property's listing.</summary>
    [Required(ErrorMessage = "MarketplaceMappingInvalid")]
    [MaxLength(64, ErrorMessage = "MarketplaceMappingInvalid")]
    public string ProductId { get; set; } = string.Empty;
}
