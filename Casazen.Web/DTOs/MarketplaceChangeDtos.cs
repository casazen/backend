using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

public sealed class UpdateMarketplaceSettingsRequest
{
    [MaxLength(100, ErrorMessage = "MarketplaceCredentialsTooLong")]
    public string? HotelId { get; set; }

    [MaxLength(200, ErrorMessage = "MarketplaceCredentialsTooLong")]
    public string? Username { get; set; }

    [MaxLength(200, ErrorMessage = "MarketplaceCredentialsTooLong")]
    public string? Password { get; set; }

    public MarketplaceSettingsChange ToChange() => new(HotelId, Username, Password);

    public override string ToString() => "UpdateMarketplaceSettingsRequest { Credentials = (hidden) }";
}

public sealed class DeactivateMarketplaceActivationRequest
{
    public bool Confirmed { get; set; }
}

public sealed class MarketplaceFunctionChangeRequest
{
    public string? Note { get; set; }
}

/// <summary>
/// <c>GET api/marketplace/activations/{id}/deactivation-preview?lang=</c>: what a deactivation does, to be read before it is confirmed. <c>effects</c> are plain sentences in the
/// language asked with the name of the channel, tool, function or partner put in; <c>requiresConfirmation</c> is always true; <c>guided</c> is true for a partner (it is turned off
/// on its own site) and for a function of the license (it is asked of the channel manager): the page shows the steps, then asks the host to say it did them.
/// </summary>
public sealed record MarketplaceDeactivationPreviewDto(
    Guid ActivationId,
    MarketplaceActivationKind Kind,
    string Code,
    string Name,
    IReadOnlyList<string> Effects,
    bool RequiresConfirmation,
    bool Guided)
{
    public static MarketplaceDeactivationPreviewDto From(MarketplaceDeactivationPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return new MarketplaceDeactivationPreviewDto(
            preview.ActivationId,
            preview.Kind,
            preview.Code,
            preview.Name,
            preview.Effects,
            preview.RequiresConfirmation,
            preview.Guided);
    }
}

public sealed record MarketplaceActivationChangeDto(
    Guid ActivationId,
    MarketplaceActivationKind Kind,
    string Code,
    Guid? PropertyId,
    MarketplaceActivationStatus Status,
    DateTime? ChangeRequestedAt)
{
    public static MarketplaceActivationChangeDto From(MarketplaceActivationOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return new MarketplaceActivationChangeDto(
            outcome.ActivationId,
            outcome.Kind,
            outcome.Code,
            outcome.PropertyId,
            outcome.Status,
            outcome.ChangeRequestedAt);
    }
}
