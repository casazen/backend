using System.Text;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The change of the settings of an activation and its deactivation (E09/F03/US01/T02, NF-58, MKT-05): the settings of a channel or a tool, the preview of
/// what a deactivation does, the deactivation itself, and the change request of a function of the channel manager's license. Every method checks the org, the
/// reach of the caller and the property the same way, and the answer for an activation that is not the caller's is always the same 404.
/// </summary>
/// <remarks>
/// <para><b>The reach of the caller.</b> The activation must be one of the org (<c>ArgumentException</c> for the scope of another org: fail closed) whose property is
/// inside the caller's <see cref="HostScope"/>. A row with no property (a partner declared for the whole org) belongs to the whole org and is reached by a caller who
/// reaches the whole org only. Another org's activation, one outside the scope, one of a property that was deleted and one that does not exist are the same 404
/// <c>marketplace_activation_not_found</c>. The activations that go through Octorate (a channel, a tool, a function of the license) do not exist for anybody while
/// <c>Features:OctorateConnector</c> is off, so they answer the same 404; the partners are not behind the flag.</para>
/// <para><b>Four kinds, four ways.</b> A channel or a tool is changed and deleted on the channel manager (<c>PATCH</c> and <c>DELETE</c> of its connection); a
/// function of the license is never changed or switched off by CasaZen, which only records that the host asked its channel manager
/// (<see cref="RequestFunctionChangeAsync"/>); a partner is switched off on its own site and CasaZen records that the host did it
/// (<c>IMarketplacePartnerActivationService.UndeclareAsync</c>). Anything else is <c>marketplace_change_not_supported</c>.</para>
/// <para><b>Nothing happens without a yes.</b> <see cref="DeactivateAsync"/> deletes nothing and changes nothing unless <c>confirmed</c> is true; the check comes
/// before anything is read, so an unconfirmed request answers the same 422 for everybody and tells nothing about the activations of others.</para>
/// <para><b>Credentials.</b> The login details of a channel reach <see cref="UpdateSettingsAsync"/> inside <see cref="MarketplaceSettingsChange"/>, go to the channel
/// manager in one call and are forgotten: no column, no log line, no answer and no message holds them.</para>
/// </remarks>
public interface IMarketplaceChangeService
{
    /// <summary>
    /// Changes the settings of a channel or a tool on the channel manager: the identifier of the property on the channel and the login details, only those the portal
    /// asks for (<c>PATCH /rest/v1/connection/{acc}/{connection}</c>, only the members that are set). The login details are passed through and never stored or logged;
    /// <c>UpdatedAt</c> is set. The connector must be on and the org connected, and the property must have its confirmed listing.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException"><c>marketplace_activation_not_found</c>.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
    /// <c>marketplace_change_not_supported</c> (not a channel or a tool, one that was turned off, or a portal with nothing to change), <c>marketplace_settings_required</c>
    /// (nothing that the portal asks for was given: an empty change is never «everything»), <c>marketplace_property_not_linked</c>.
    /// </exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException">
    /// <c>marketplace_octorate_not_connected</c>, <c>marketplace_connection_missing</c> (the activation has no connection, or the channel manager no longer has it).
    /// </exception>
    Task<MarketplaceActivationOutcome> UpdateSettingsAsync(
        Guid orgId,
        HostScope scope,
        Guid activationId,
        MarketplaceSettingsChange settings,
        string userId,
        CancellationToken ct = default);

    /// <summary>
    /// What a deactivation does, in plain sentences from the resource files in <paramref name="language"/> (<c>it</c> or <c>en</c>), with the name of the channel, the tool,
    /// the function or the partner put in, and whether it is guided. It reads only: nothing is asked of Octorate and nothing is written.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException"><c>marketplace_activation_not_found</c>.</exception>
    Task<MarketplaceDeactivationPreview> PreviewDeactivationAsync(
        Guid orgId,
        HostScope scope,
        Guid activationId,
        string language = MarketplaceLanguages.Italian,
        CancellationToken ct = default);

    /// <summary>
    /// Turns an activation off. <paramref name="confirmed"/> must be true: otherwise <c>marketplace_confirmation_required</c> and nothing happens (no connection is
    /// deleted, no status changes). A channel or a tool: the connection is deleted on the channel manager (a connection it no longer has is already what was asked)
    /// and the row becomes <see cref="MarketplaceActivationStatus.Deactivated"/>, kept as history without its connection and with its open «To do» item closed. A
    /// partner: the row becomes Deactivated once the host declares it did it on the partner's site. A function of the license is not deactivated by CasaZen
    /// (<c>marketplace_change_not_supported</c>: see <see cref="RequestFunctionChangeAsync"/>). Idempotent: a row that is already Deactivated is returned as it is.
    /// A failure of the channel manager is raised as it is and changes nothing.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException"><c>marketplace_confirmation_required</c>, <c>marketplace_change_not_supported</c>, <c>marketplace_property_not_linked</c>.</exception>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException"><c>marketplace_activation_not_found</c>.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException"><c>marketplace_octorate_not_connected</c> (a channel or a tool needs the connection to the channel manager to delete its own).</exception>
    Task<MarketplaceActivationOutcome> DeactivateAsync(
        Guid orgId,
        HostScope scope,
        Guid activationId,
        bool confirmed,
        string userId,
        CancellationToken ct = default);

    /// <summary>
    /// Records that the host asked its channel manager for a change of a function of the license: <c>ChangeRequestedAt</c> is set and the status stays, so the card says
    /// «change requested on» until the reconciliation of the license shows a different state (it clears the request). One request is open at a time. CasaZen asks the
    /// channel manager nothing and never writes the license. <paramref name="note"/> is the host's own words for the request (at most
    /// <see cref="MarketplaceChangeLimits.MaxNoteLength"/> characters); CasaZen keeps no copy of it, because free text can hold personal data and the request is the date.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException"><c>marketplace_activation_not_found</c>.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException"><c>marketplace_change_not_supported</c> (not a function of the license, or one that was turned off), <c>marketplace_change_note_invalid</c>.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException"><c>marketplace_change_already_requested</c>.</exception>
    Task<MarketplaceActivationOutcome> RequestFunctionChangeAsync(
        Guid orgId,
        HostScope scope,
        Guid activationId,
        string? note,
        string userId,
        CancellationToken ct = default);
}

/// <summary>The stable codes of the change and the deactivation (E09/F03/US01/T02): the answers of the endpoints, each with a message in the resource files, Italian and English.</summary>
public static class MarketplaceChangeErrorCodes
{
    /// <summary>422: a deactivation without <c>confirmed = true</c>. Nothing was deleted and nothing changed.</summary>
    public const string ConfirmationRequired = "marketplace_confirmation_required";

    /// <summary>404: the activation is not one of the org inside the caller's scope.</summary>
    public const string ActivationNotFound = MarketplaceActivationErrorCodes.ActivationNotFound;

    /// <summary>422: this kind of activation cannot be changed that way (a settings change of a partner, a deactivation of a function of the license by CasaZen).</summary>
    public const string ChangeNotSupported = "marketplace_change_not_supported";

    /// <summary>409: a change of the function is already asked for and the license has not answered it yet.</summary>
    public const string ChangeAlreadyRequested = "marketplace_change_already_requested";

    /// <summary>422: nothing that the portal asks for was given to change; an empty change is never «everything».</summary>
    public const string SettingsRequired = "marketplace_settings_required";

    /// <summary>422: the note of a change request is longer than <see cref="MarketplaceChangeLimits.MaxNoteLength"/>.</summary>
    public const string NoteInvalid = "marketplace_change_note_invalid";
}

/// <summary>The limits of the change request.</summary>
public static class MarketplaceChangeLimits
{
    /// <summary>The longest note of a change request, in characters.</summary>
    public const int MaxNoteLength = 500;
}

/// <summary>
/// The settings of a channel or a tool the host changes: the identifier of the property on the channel and the login details. A member that is not given (or is blank) is not
/// sent. The username and password are request-only: they go to the channel manager in one call and are never stored, never logged and never part of an answer;
/// <see cref="object.ToString"/> hides them.
/// </summary>
/// <param name="HotelId">The new identifier of the property on the portal.</param>
/// <param name="Username">The new username on the portal.</param>
/// <param name="Password">The new password on the portal.</param>
public sealed record MarketplaceSettingsChange(string? HotelId, string? Username, string? Password)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Credentials = (hidden)");
        return true;
    }
}

/// <summary>What a change or a deactivation left the activation as, for the page to show: the row itself, never a login detail.</summary>
/// <param name="ActivationId">The activation.</param>
/// <param name="Kind">What it activates.</param>
/// <param name="Code">The portal code, the module code or the partner code.</param>
/// <param name="PropertyId">The property it is for; <c>null</c> for a row of the whole org.</param>
/// <param name="Status">Where it stands now: Deactivated after a deactivation, otherwise what it was.</param>
/// <param name="ChangeRequestedAt">When the host asked the channel manager for a change of a function (UTC), while the license has not answered; <c>null</c> otherwise.</param>
public sealed record MarketplaceActivationOutcome(
    Guid ActivationId,
    MarketplaceActivationKind Kind,
    string Code,
    Guid? PropertyId,
    MarketplaceActivationStatus Status,
    DateTime? ChangeRequestedAt);

/// <summary>
/// What a deactivation does, to be read before it is confirmed (<c>GET api/marketplace/activations/{id}/deactivation-preview</c>): <see cref="Effects"/> are plain sentences in the
/// language asked with the name put in; <see cref="RequiresConfirmation"/> is always true; <see cref="Guided"/> is true when CasaZen does not do it itself (a partner is turned off
/// on its own site, a function of the license is asked of the channel manager) and the page shows the steps, then asks the host to say it did them.
/// </summary>
/// <param name="ActivationId">The activation.</param>
/// <param name="Kind">What it activates.</param>
/// <param name="Code">The portal code, the module code or the partner code.</param>
/// <param name="Name">The name put in the sentences.</param>
/// <param name="Effects">The effects, one sentence each.</param>
/// <param name="RequiresConfirmation">Always true: <c>POST .../deactivate</c> needs <c>confirmed</c>.</param>
/// <param name="Guided">True for a partner and for a function of the license.</param>
public sealed record MarketplaceDeactivationPreview(
    Guid ActivationId,
    MarketplaceActivationKind Kind,
    string Code,
    string Name,
    IReadOnlyList<string> Effects,
    bool RequiresConfirmation,
    bool Guided);
