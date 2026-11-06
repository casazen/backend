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
    /// <summary>The portal codes of the OTA channels, as the catalog of the channel manager gives them.</summary>
    [Required(ErrorMessage = "ExistingInventoryChannelsRequired")]
    public List<string>? Channels { get; set; }

    /// <summary>
    /// The CasaZen properties the import is about, each of the org and within the caller's reach. Leave it out for the whole inventory: an empty
    /// list is a selection of nothing and is refused (422), never taken for all of them.
    /// </summary>
    public List<Guid>? PropertyIds { get; set; }

    /// <summary>The named group of the mixed portfolio.</summary>
    [MaxLength(100, ErrorMessage = "ExistingInventoryScopeInvalid")]
    public string? Group { get; set; }
}

/// <summary>Imports the rooms (<c>POST .../{id}/run</c>); the body is optional.</summary>
public sealed class RunExistingInventoryRequest
{
    /// <summary>
    /// The Octorate accommodation ids of the listings whose last operation has an unknown result (<c>d6_result_unknown</c>) and that the person
    /// checked in the panel and wants asked again. Such a listing is never asked again by itself.
    /// </summary>
    public List<string>? RetryUnknown { get; set; }
}

/// <summary>Goes on with the confirmed listings (<c>POST .../{id}/continue</c>); the accommodation ids named are left for later.</summary>
public sealed class ContinueExistingInventoryRequest
{
    /// <summary>The Octorate accommodation ids of the listings the person leaves for later: they stay write-blocked and are not marked connected.</summary>
    public List<string>? LeaveForLater { get; set; }

    /// <summary>
    /// The Octorate accommodation ids of the listings whose last operation has an unknown result (<c>d6_result_unknown</c>) and that the person
    /// checked in the panel and wants asked again. Such a listing is never asked again by itself.
    /// </summary>
    public List<string>? RetryUnknown { get; set; }
}

/// <summary>The scenarios of a mixed portfolio (<c>PUT api/octorate/inventory-intents</c>, E02/F01/US03).</summary>
public sealed class InventoryIntentsRequest
{
    [Required(ErrorMessage = "InventoryIntentAssignmentsInvalid")]
    public List<InventoryIntentAssignmentRequest>? Assignments { get; set; }
}

/// <summary>One assignment: a property (or, with none, a named group with no property yet) and the scenario the person declared.</summary>
public sealed class InventoryIntentAssignmentRequest
{
    /// <summary>The CasaZen property; <c>null</c> for a group that has no property yet.</summary>
    public Guid? PropertyId { get; set; }

    /// <summary><c>onOctorate</c>, <c>onOtas</c> or <c>newInventory</c>.</summary>
    [Required(ErrorMessage = "InventoryScenarioInvalid")]
    [MaxLength(30, ErrorMessage = "InventoryScenarioInvalid")]
    public string? Scenario { get; set; }

    /// <summary>The named group of the mixed portfolio; required when there is no property.</summary>
    [MaxLength(100, ErrorMessage = "InventoryGroupRequired")]
    public string? Group { get; set; }
}

/// <summary>
/// An import batch (<c>GET api/octorate/existing-inventory/batches/{id}</c>): progress, counts, stable error codes and safe ids; never a
/// credential, an access address once used, or a payload of Octorate.
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
    /// <summary>The code of <see cref="PanelHandoffMode.RedirectCallback"/> in the API.</summary>
    public const string RedirectCallbackCode = "redirectCallback";

    /// <summary>The code of <see cref="PanelHandoffMode.ManagedWindow"/> in the API.</summary>
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
