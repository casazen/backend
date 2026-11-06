using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Controllers;

/// <summary>
/// Who issues the fiscal documents of the organization and with which data (FIS-02, NF-82, PO-05, E06/F02/US02/T02): the setting the wizard
/// «Come emetti i documenti?» reads and saves, and the preview of who would issue the documents of a booking. It is about money: besides
/// <c>property.read</c> it needs <c>payment.read</c> (as <see cref="FiscalController"/>), the save adds <c>property.write</c>. No new permission keys.
/// </summary>
/// <remarks>
/// The setting belongs to the whole organization, not to some of its properties: a caller who reaches only some of them (a collaborator «Solo
/// alcuni») may read it and may not change it (403 <c>fiscal_settings_need_whole_org</c>, as the payments of the tourist tax). The preview is about
/// one booking and authorizes its row like the other booking endpoints (TN-3): another org's booking is 404, one the caller sees and may not read
/// is 403.
/// </remarks>
[ApiController]
[Route("api/fiscal/documents")]
[Authorize(Policy = "RequireContext:short-rent:property.read")]
[Authorize(Policy = CasazenPolicies.PaymentRead)]
public class FiscalDocumentSettingsController(
    IDocumentIssuerService issuer,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IFeatureFlags featureFlags,
    IOptions<FiscalDocumentRulesOptions> options,
    ISdiEInvoiceProvider sdiProvider,
    ILogger<FiscalDocumentSettingsController> logger) : ControllerBase
{
    /// <summary>
    /// The setting of the organization: <c>mode</c> (absent until it chooses), <c>regime</c>, the data of the issuer, <c>confirmedAt</c>,
    /// <c>casazenIssuanceEnabled</c> (the flag), <c>octorateIssuesAllBookings</c> (D34), <c>octorateConnected</c>, <c>sdi</c>
    /// (<c>providerConfigured</c>, <c>providerName</c>), the <c>missingFields</c> the mode CasaZen would still need and the <c>rule</c> of the regime.
    /// </summary>
    [HttpGet("settings")]
    [ProducesResponseType(typeof(FiscalDocumentSettingsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<FiscalDocumentSettingsDto>> GetSettings(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return Unauthorized();

        return Ok(ToDto(await issuer.GetSettingsAsync(orgId.Value, cancellationToken)));
    }

    /// <summary>
    /// Saves the answers of the wizard as a whole and answers the setting as saved. 422 <c>issuer_field_invalid</c> (with the <c>field</c>),
    /// <c>document_issuance_not_available</c> (the mode CasaZen while the flag is off), <c>issuer_profile_incomplete</c> (with the <c>fields</c> that
    /// are missing) or <c>octorate_not_connected</c>; 403 <c>fiscal_settings_need_whole_org</c> for a caller who reaches only some of the properties.
    /// </summary>
    [HttpPut("settings")]
    [Authorize(Policy = "RequireContext:short-rent:property.write")]
    [ProducesResponseType(typeof(FiscalDocumentSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<FiscalDocumentSettingsDto>> PutSettings(
        [FromBody] SaveFiscalDocumentSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return Unauthorized();

        // The setting is the organization's: a caller who reaches only some of its properties cannot decide who issues for all of them.
        var scope = await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        if (scope is not { IsOrgWide: true })
        {
            logger.LogWarning("User {UserId} may not change the issuer setting of org {OrgId}: its scope is not the whole org", User.GetUserId(), orgId);
            return this.ApiProblem(
                StatusCodes.Status403Forbidden,
                DocumentIssuerErrorCodes.NeedsWholeOrg,
                DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.NeedsWholeOrg));
        }

        var saved = await issuer.SaveSettingsAsync(orgId.Value, request.ToCommand(), User.GetUserId(), cancellationToken);
        return Ok(ToDto(saved));
    }

    /// <summary>
    /// Who would issue the documents of the booking and why: <c>issuer</c> (<c>None</c>, <c>Octorate</c> or <c>CasaZen</c>) and <c>reason</c>, a stable
    /// code (<c>issuer_octorate</c>, <c>issuer_casazen</c>, <c>issuer_nobody</c>, <c>booking_not_direct</c>, <c>issuer_locked_octorate</c>,
    /// <c>issuance_off</c>). It changes nothing. 404 for a booking of another org, 403 for one the caller may not read.
    /// </summary>
    [HttpGet("issuer-preview")]
    [ProducesResponseType(typeof(IssuerPreviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IssuerPreviewDto>> IssuerPreview([FromQuery] Guid bookingId, CancellationToken cancellationToken)
    {
        var resource = await hostResources.ForBookingAsync(bookingId, cancellationToken);
        if (resource is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, "booking_not_found", "BookingNotFound");

        if (!await authorizationService.IsAuthorizedAsync(User, resource, BookingOperations.Read))
        {
            logger.LogWarning("User {UserId} denied booking.read on the issuer preview of booking {BookingId}", User.GetUserId(), bookingId);
            return Forbid();
        }

        var decision = await issuer.ResolveIssuerAsync(bookingId, cancellationToken);
        return Ok(new IssuerPreviewDto(bookingId, decision.Kind.ToString(), decision.Reason));
    }

    private FiscalDocumentSettingsDto ToDto(IssuerSettingsView view) =>
        FiscalDocumentSettingsDto.From(
            view,
            featureFlags.IsEnabled(FeatureFlags.FiscalDocumentIssuance),
            options.Value.OctorateIssuesAllBookings,
            sdiProvider.IsConfigured,
            sdiProvider.Name);
}
