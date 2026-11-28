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
    MarketplaceActivationStatus? ActivationStatus,
    string? VerifiedHotelId = null,
    bool ManagedByStaff = false)
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
            plan.ActivationStatus,
            plan.VerifiedHotelId,
            plan.ManagedByStaff);
    }
}

public sealed class StartMarketplaceActivationRequest
{
    [Required(ErrorMessage = "MarketplaceActivationKindRequired")]
    public MarketplaceActivationKind? Kind { get; set; }

    [Required(ErrorMessage = "MarketplacePortalUnknown")]
    [MaxLength(100, ErrorMessage = "MarketplacePortalUnknown")]
    public string? Code { get; set; }

    [Required(ErrorMessage = "OctorateMappingPropertyRequired")]
    public Guid? PropertyId { get; set; }

    [MaxLength(100, ErrorMessage = "MarketplaceCredentialsTooLong")]
    public string? HotelId { get; set; }

    [MaxLength(200, ErrorMessage = "MarketplaceCredentialsTooLong")]
    public string? Username { get; set; }

    [MaxLength(200, ErrorMessage = "MarketplaceCredentialsTooLong")]
    public string? Password { get; set; }

    public MarketplaceActivationStart ToStart() =>
        new(Kind ?? default, Code ?? string.Empty, PropertyId ?? Guid.Empty, HotelId, Username, Password);
}

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

public sealed record MarketplaceChannelRoomDto(
    string ChannelRoomId,
    string Name,
    string? RateName,
    bool Manageable,
    string? MappedProductId,
    string? ProposedProductId);

public sealed record MarketplaceProductDto(string ProductId, string? Name);

public sealed class SaveMarketplaceMappingRequest
{
    public List<MarketplaceRoomChoiceRequest>? Mappings { get; set; }
}

public sealed class MarketplaceRoomChoiceRequest
{
    [Required(ErrorMessage = "MarketplaceMappingInvalid")]
    [MaxLength(64, ErrorMessage = "MarketplaceMappingInvalid")]
    public string ChannelRoomId { get; set; } = string.Empty;

    [Required(ErrorMessage = "MarketplaceMappingInvalid")]
    [MaxLength(64, ErrorMessage = "MarketplaceMappingInvalid")]
    public string ProductId { get; set; } = string.Empty;
}
