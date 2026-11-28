using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// Starts an import of existing OTA inventory (<c>POST api/octorate/existing-inventory/batches</c>, E02/F01/US03): the channels the person
/// uses and the properties it is about. Without <see cref="PropertyIds"/> and <see cref="Group"/> it is the whole inventory (the caller must
/// reach the whole org); one of them narrows it; both is a 422, and so is an empty <see cref="PropertyIds"/>.
/// </summary>
public sealed class CreateExistingInventoryBatchRequest
{
    [Required(ErrorMessage = "ExistingInventoryChannelsRequired")]
    public List<string>? Channels { get; set; }

    public List<Guid>? PropertyIds { get; set; }

    [MaxLength(100, ErrorMessage = "ExistingInventoryScopeInvalid")]
    public string? Group { get; set; }
}

public sealed class RunExistingInventoryRequest
{
    /// <summary>
    /// The Octorate accommodation ids of the listings whose last operation has an unknown result (<c>d6_result_unknown</c>) and that the person
    /// checked in the panel and wants asked again. Such a listing is never asked again by itself.
    /// </summary>
    public List<string>? RetryUnknown { get; set; }
}

public sealed class ContinueExistingInventoryRequest
{
    public List<string>? LeaveForLater { get; set; }

    /// <summary>
    /// The Octorate accommodation ids of the listings whose last operation has an unknown result (<c>d6_result_unknown</c>) and that the person
    /// checked in the panel and wants asked again. Such a listing is never asked again by itself.
    /// </summary>
    public List<string>? RetryUnknown { get; set; }
}

/// <summary>
/// «Collega da CasaZen» (<c>POST .../{id}/channels/{code}/connect</c>): the accommodations to connect to the channel, each with the id of the property
/// on the portal. The service checks them (1 to 50, each once, an id of 1 to 64 letters, digits, dots, underscores or hyphens, an accommodation of
/// the batch) and answers 422 with a stable code, never a validation error of the framework.
/// </summary>
public sealed class ConnectExistingInventoryChannelRequest
{
    public List<ConnectExistingInventoryAccommodationRequest?>? Accommodations { get; set; }
}

public sealed class ConnectExistingInventoryAccommodationRequest
{
    public string? AccommodationId { get; set; }

    public string? HotelId { get; set; }
}

public sealed class InventoryIntentsRequest
{
    [Required(ErrorMessage = "InventoryIntentAssignmentsInvalid")]
    public List<InventoryIntentAssignmentRequest>? Assignments { get; set; }
}

public sealed class InventoryIntentAssignmentRequest
{
    public Guid? PropertyId { get; set; }

    [Required(ErrorMessage = "InventoryScenarioInvalid")]
    [MaxLength(30, ErrorMessage = "InventoryScenarioInvalid")]
    public string? Scenario { get; set; }

    [MaxLength(100, ErrorMessage = "InventoryGroupRequired")]
    public string? Group { get; set; }
}

/// <summary>
/// An import batch (<c>GET api/octorate/existing-inventory/batches/{id}</c>): progress, counts, stable error codes and safe ids; never a
/// credential, an access address once used, or a payload of Octorate. Each channel carries <c>connectedAccommodationIds</c>: the Octorate
/// accommodations «Collega da CasaZen» connected to it for the batch, or found already connected (an empty list when none).
/// </summary>
public sealed record ExistingInventoryBatchDto(
    Guid Id,
    string Scenario,
    OctorateInventoryScopeKind ScopeKind,
    string? Group,
    IReadOnlyList<Guid> PropertyIds,
    OctorateInventoryImportStatus Status,
    string? ErrorCode,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? CompletedAt,
    IReadOnlyList<ExistingInventoryChannelView> Channels)
{
    public static ExistingInventoryBatchDto From(ExistingInventoryBatchView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new ExistingInventoryBatchDto(
            view.Id,
            OctorateInventoryScenarioCodes.OnOtas,
            view.ScopeKind,
            view.Group,
            view.PropertyIds,
            view.Status,
            view.ErrorCode,
            view.CreatedAt,
            view.UpdatedAt,
            view.CompletedAt,
            view.Channels);
    }
}

/// <summary>
/// What the wizard needs to open the Octorate panel for a channel (<c>POST .../channels/{code}/access/start</c>): the only response that
/// carries the address of the panel. Credentials are never asked for here.
/// </summary>
/// <param name="ChannelCode">The channel.</param>
/// <param name="AlreadyComplete">The access was already given: nothing to open.</param>
/// <param name="HandoffMode"><c>redirectCallback</c> or <c>managedWindow</c>; <c>null</c> when already complete.</param>
/// <param name="PanelUrl">The address of the panel: https, with no organization, property or person data; <c>null</c> when already complete.</param>
public sealed record ChannelAccessStartDto(string ChannelCode, bool AlreadyComplete, string? HandoffMode, string? PanelUrl)
{
    public const string RedirectCallbackCode = "redirectCallback";

    public const string ManagedWindowCode = "managedWindow";

    public static ChannelAccessStartDto From(ChannelAccessStartResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new ChannelAccessStartDto(
            result.ChannelCode,
            result.AlreadyComplete,
            result.HandoffMode switch
            {
                PanelHandoffMode.RedirectCallback => RedirectCallbackCode,
                PanelHandoffMode.ManagedWindow => ManagedWindowCode,
                _ => null,
            },
            result.PanelUrl);
    }
}

/// <summary>An inventory intent (<c>PUT</c> and <c>GET api/octorate/inventory-intents</c>).</summary>
/// <param name="PropertyId">The CasaZen property; <c>null</c> for a group that has no property yet.</param>
/// <param name="Scenario"><c>onOctorate</c>, <c>onOtas</c> or <c>newInventory</c>.</param>
/// <param name="Group">The named group of the mixed portfolio, when there is one.</param>
/// <param name="UpdatedAt">When it was declared or changed (UTC).</param>
public sealed record InventoryIntentDto(Guid? PropertyId, string Scenario, string? Group, DateTime UpdatedAt)
{
    public static InventoryIntentDto From(InventoryIntentView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new InventoryIntentDto(view.PropertyId, OctorateInventoryScenarioCodes.CodeOf(view.Scenario), view.Group, view.UpdatedAt);
    }
}
