using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// The body of <c>PATCH api/marketplace/activations/{id}</c> (E09/F03/US01/T02, NF-58): the settings of a channel or a tool the host changes. Only what the portal asks for is
/// used (the identifier of the property on the channel, the username and the password) and a member that is not given is not sent. <c>username</c> and <c>password</c> are
/// request-only: the channel manager gets them in one call, CasaZen stores none of them, logs none of them and no answer carries them.
/// </summary>
public sealed class UpdateMarketplaceSettingsRequest
{
    /// <summary>The new identifier of the property on the portal.</summary>
    [MaxLength(100, ErrorMessage = "MarketplaceCredentialsTooLong")]
    public string? HotelId { get; set; }

    /// <summary>The new username on the portal.</summary>
    [MaxLength(200, ErrorMessage = "MarketplaceCredentialsTooLong")]
    public string? Username { get; set; }

    /// <summary>The new password on the portal.</summary>
    [MaxLength(200, ErrorMessage = "MarketplaceCredentialsTooLong")]
    public string? Password { get; set; }

    /// <summary>The change the service takes.</summary>
    public MarketplaceSettingsChange ToChange() => new(HotelId, Username, Password);

    /// <summary>The request prints without its login details: a log of the object or an exception that names it would leak them.</summary>
    public override string ToString() => "UpdateMarketplaceSettingsRequest { Credentials = (hidden) }";
}

/// <summary>
/// The body of <c>POST api/marketplace/activations/{id}/deactivate</c>: <c>confirmed</c> must be true. Without it (or without a body) the answer is 422
/// <c>marketplace_confirmation_required</c>, nothing is deleted and nothing changes.
/// </summary>
public sealed class DeactivateMarketplaceActivationRequest
{
    /// <summary>The host read what the deactivation does and said yes.</summary>
    public bool Confirmed { get; set; }
}

/// <summary>
/// The body of <c>POST api/marketplace/activations/{id}/change-request</c>: the host's own words for the request to its channel manager (at most 500 characters). CasaZen does
/// not keep them: the request is the date.
/// </summary>
public sealed class MarketplaceFunctionChangeRequest
{
    /// <summary>
    /// The host's note for the request. No length attribute on purpose: a note longer than <see cref="MarketplaceChangeLimits.MaxNoteLength"/> is the rule of the service
    /// (422 <c>marketplace_change_note_invalid</c>, checked before the row is read), not a 400 <c>validation_error</c> of the model validation, which would answer before it.
    /// </summary>
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

/// <summary>
/// The answer of a change, a deactivation or a change request: the activation as it stands now. <c>status</c> is <c>Deactivated</c> after a deactivation; <c>changeRequestedAt</c> is
/// set after the change request of a function (until the license answers it). No login detail, ever.
/// </summary>
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
