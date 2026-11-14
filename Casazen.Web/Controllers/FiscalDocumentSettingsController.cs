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
/// With which data CasaZen issues the fiscal documents of the organization (FIS-02, NF-82, NF-112, E06/F02/US02/T02, E11/F01/US01/T01): the setting the
/// wizard reads and saves, and the preview of who would issue the documents of a booking. It is about money: besides <c>property.read</c> it needs
/// <c>payment.read</c> (as <see cref="FiscalController"/>), the save adds <c>property.write</c>. No new permission keys.
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
    /// (<c>providerConfigured</c>, <c>providerName</c>), the <c>missingFields</c> the mode CasaZen would still need and the <c>rule</c> of the regime. The
    /// IBAN and the QR-IBAN come in full only for the caller who may change the setting (the whole organization and <c>property.write</c>); for any other
    /// they are masked (<c>ibanMasked</c>, <c>qrIbanMasked</c>).
    /// </summary>
    [HttpGet("settings")]
    [ProducesResponseType(typeof(FiscalDocumentSettingsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<FiscalDocumentSettingsDto>> GetSettings(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return Unauthorized();

        // A caller who may read the setting sees who the issuer is; the bank accounts of the organization (IBAN, QR-IBAN) in full only the one who may change
        // the setting (the whole organization and property.write): any other, a collaborator «Solo alcuni» or a read-only role included, gets them masked.
        var scope = await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        var bankAccountsInClear = scope is { IsOrgWide: true } && (await authorizationService.AuthorizeAsync(User, CasazenPolicies.PropertyWrite)).Succeeded;
        return Ok(ToDto(await issuer.GetSettingsAsync(orgId.Value, cancellationToken), bankAccountsInClear));
    }

    /// <summary>
    /// Saves the answers of the wizard as a whole and answers the setting as saved. 422 <c>issuer_mode_retired</c> (the mode Octorate or Accountant),
    /// <c>issuer_regime_country_mismatch</c>, <c>issuer_field_invalid</c> (with the <c>field</c>), <c>document_issuance_not_available</c> (the flag is
    /// off) or <c>issuer_profile_incomplete</c> (a confirmed wizard with <c>fields</c> missing); 403 <c>fiscal_settings_need_whole_org</c> for a caller
    /// who reaches only some of the properties. A refusal saves nothing.
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

        // The caller changes the setting (the whole organization and property.write): it gets the bank accounts back as it saved them.
        var saved = await issuer.SaveSettingsAsync(orgId.Value, request.ToCommand(), User.GetUserId(), cancellationToken);
        return Ok(ToDto(saved, bankAccountsInClear: true));
    }

    /// <summary>
    /// Who issues and with which rule: <c>issuer</c> (<c>CasaZen</c>, <c>None</c> while the issuance is off, <c>Octorate</c> for a booking a legacy row
    /// locked to it), <c>reason</c> (<c>issuer_casazen</c>, <c>issuance_off</c>, <c>issuer_locked_octorate</c>) and <c>rule</c>, the tax rule in force today
    /// for the profile of the issuer with the VAT of every line type (absent while none applies). Without <paramref name="bookingId"/> it is the preview of
    /// the organization (the summary of the wizard); with it, of that booking: 404 for a booking of another org, 403 for one the caller may not read. It
    /// changes nothing.
    /// </summary>
    [HttpGet("issuer-preview")]
    [ProducesResponseType(typeof(IssuerPreviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IssuerPreviewDto>> IssuerPreview([FromQuery] Guid? bookingId, CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return Unauthorized();

        IssuerDecision decision;
        if (bookingId is { } id)
        {
            var resource = await hostResources.ForBookingAsync(id, cancellationToken);
            if (resource is null)
                return this.ApiProblem(StatusCodes.Status404NotFound, "booking_not_found", "BookingNotFound");

            if (!await authorizationService.IsAuthorizedAsync(User, resource, BookingOperations.Read))
            {
                logger.LogWarning("User {UserId} denied booking.read on the issuer preview of booking {BookingId}", User.GetUserId(), id);
                return Forbid();
            }

            decision = await issuer.ResolveIssuerAsync(id, cancellationToken);
        }
        else
        {
            decision = featureFlags.IsEnabled(FeatureFlags.FiscalDocumentIssuance)
                ? new IssuerDecision(IssuerDecisionKind.CasaZen, IssuerReasons.IssuerCasazen)
                : new IssuerDecision(IssuerDecisionKind.None, IssuerReasons.IssuanceOff);
        }

        var settings = await issuer.GetSettingsAsync(orgId.Value, cancellationToken);
        return Ok(new IssuerPreviewDto(bookingId, decision.Kind.ToString(), decision.Reason, settings.Rule is null ? null : TaxRuleDto.From(settings.Rule)));
    }

    private FiscalDocumentSettingsDto ToDto(IssuerSettingsView view, bool bankAccountsInClear) =>
        FiscalDocumentSettingsDto.From(
            view,
            featureFlags.IsEnabled(FeatureFlags.FiscalDocumentIssuance),
            options.Value.OctorateIssuesAllBookings,
            sdiProvider.IsConfigured,
            sdiProvider.Name,
            bankAccountsInClear);
}
