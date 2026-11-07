using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The hand-off wizard of a Partner Hub partner (E09/F02/US03/T01, NF-56, MKT-03, S18, Q-006): the brief the host takes to the partner's own site and the
/// declaration that it activated the partner there. The partners never depend on Octorate, so the controller is not behind
/// <c>Features:OctorateConnector</c>; nothing is sent to the partner and nothing is asked of Octorate.
/// </summary>
/// <remarks>
/// <para><b>Policies.</b> <see cref="CasazenPolicies.BookingRead"/> for the brief, <see cref="CasazenPolicies.OtaWrite"/> for the declaration. The org comes
/// from the caller's membership and the scope from <see cref="IHostScopeResolver"/>, never from the request: a property outside the scope answers 404, as
/// one that does not exist, and a declaration for the whole org (no property) is an org-level decision that a caller limited to some properties does not
/// make (403 <c>marketplace_partner_needs_whole_org</c>). A partner the org may not see answers 404 <c>marketplace_entry_not_found</c>, the same as a code
/// nobody has.</para>
/// <para>Every answer is <c>private, no-store</c>.</para>
/// </remarks>
[ApiController]
[Route("api/marketplace/partners")]
public sealed class MarketplacePartnersController(
    IMarketplacePartnerActivationService partners,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>
    /// What the host takes to the partner's site: the partner's name and address, the checklist of what to choose and what to authorise there, and, with a
    /// property, the fixed list of the property's data the partner is likely to ask for (name, address, city, postal code, bedrooms, bathrooms, maximum
    /// guests, and the CIN only when it is valid). Never a guest, a booking, a price or a document.
    /// </summary>
    /// <param name="code">The partner's code in the catalog.</param>
    /// <param name="propertyId">The property the data is for; none = no property data.</param>
    /// <param name="lang"><c>it</c> or <c>en</c>, for the checklist; the language of the request when absent.</param>
    /// <param name="cancellationToken">Cancels the read when the client goes away.</param>
    /// <response code="200">The brief.</response>
    /// <response code="403">The caller has no org.</response>
    /// <response code="404"><c>marketplace_entry_not_found</c> (unknown, hidden, or not for the org's plan or country), <c>marketplace_property_not_found</c> (not the org's, or outside the caller's scope).</response>
    [HttpGet("{code}/brief")]
    [Authorize(Policy = CasazenPolicies.BookingRead)]
    [ProducesResponseType(typeof(MarketplacePartnerBriefDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MarketplacePartnerBriefDto>> GetBrief(
        string code, [FromQuery] Guid? propertyId, [FromQuery] string? lang, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        NoStore();
        var brief = await partners.GetBriefAsync(caller.OrgId, caller.Scope, code, propertyId, LanguageOf(lang), cancellationToken);
        return Ok(MarketplacePartnerBriefDto.From(brief));
    }

    /// <summary>
    /// Records that the host activated the partner on the partner's own site, on the day it gives: the activation is Active and declared (verified only
    /// where an Octorate connection of the same property shows it). Idempotent: declaring again returns the same activation, unchanged.
    /// </summary>
    /// <param name="code">The partner's code in the catalog.</param>
    /// <param name="request">The property (none = the whole org) and the day.</param>
    /// <param name="cancellationToken">Cancels the request when the client goes away.</param>
    /// <response code="200">The activation.</response>
    /// <response code="403">The caller has no org, or declares for the whole org and is limited to some properties (<c>marketplace_partner_needs_whole_org</c>).</response>
    /// <response code="404"><c>marketplace_entry_not_found</c>, <c>marketplace_property_not_found</c>.</response>
    /// <response code="422"><c>marketplace_declared_date_invalid</c>: the day is missing or after today.</response>
    [HttpPost("{code}/declare")]
    [Authorize(Policy = CasazenPolicies.OtaWrite)]
    [ProducesResponseType(typeof(MarketplacePartnerActivationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MarketplacePartnerActivationDto>> Declare(
        string code, [FromBody] DeclareMarketplacePartnerRequest request, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        // A day that is not there is not «today»: the host says when it did it.
        if (request.DeclaredOn is not { } declaredOn)
            throw new DomainRuleException(MarketplacePartnerErrorCodes.DeclaredDateInvalid, "MarketplaceDeclaredDateInvalid");

        NoStore();
        var activation = await partners.DeclareAsync(
            caller.OrgId, caller.Scope, code, request.PropertyId, declaredOn, caller.UserId, cancellationToken);
        return Ok(MarketplacePartnerActivationDto.From(activation));
    }

    /// <summary>The caller's id, org and scope; <c>null</c> when it has no org or reaches nothing in it.</summary>
    private async Task<Caller?> CallerAsync(CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { Length: > 0 } userId
            || await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken) is not { } orgId
            || await hostScopeResolver.ResolveHostScopeAsync(User, orgId, cancellationToken) is not { } scope)
        {
            return null;
        }

        return new Caller(userId, orgId, scope);
    }

    /// <summary><c>it</c> or <c>en</c>: the query when it names one, otherwise the language of the request.</summary>
    private static string LanguageOf(string? lang) =>
        MarketplaceLanguages.Normalize(string.IsNullOrWhiteSpace(lang) ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : lang);

    private void NoStore() => Response.Headers.CacheControl = "private, no-store";

    private ObjectResult NoOrg() => this.ApiProblem(StatusCodes.Status403Forbidden, "no_org_context", "NoOrganizationContext");

    private sealed record Caller(string UserId, Guid OrgId, HostScope Scope);
}
