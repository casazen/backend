using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The functions of the channel manager's license for a property (E09/F02/US02/T01, NF-56, MKT-03, PO-12, D21): which modules are on, the plan and the room
/// limit, the guided request of a module that is off, and the check of the license. Behind <c>Features:OctorateConnector</c>: 404 while it is off,
/// before authentication.
/// </summary>
/// <remarks>
/// <para><b>Policies.</b> <see cref="CasazenPolicies.OtaRead"/> for the read, <see cref="CasazenPolicies.OtaWrite"/> for the check and the request. The org
/// comes from the caller's membership and the scope from <see cref="IHostScopeResolver"/>, never from the request: a collaborator limited to some
/// properties reaches the licenses of those only, and a property outside the scope answers 404, as one that does not exist.</para>
/// <para><b>CasaZen never writes the license.</b> It is read-only for the partner: the request only records that the host asked the channel manager,
/// and the function turns Active when the license shows it. Every answer is <c>private, no-store</c>.</para>
/// </remarks>
[ApiController]
[Route("api/marketplace/functions")]
[FeatureGate(FeatureFlags.OctorateConnector)]
[Authorize(Policy = CasazenPolicies.OtaRead)]
public sealed class MarketplaceFunctionsController(
    IMarketplaceLicensedFunctionService functions,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>The twelve functions of the license of the property with their state, the plan and the room limit of the last read. Nothing is asked of the channel manager.</summary>
    /// <param name="propertyId">The property the license is for; a license belongs to a property.</param>
    /// <param name="lang"><c>it</c> or <c>en</c>, for the names and the request guidance; the language of the request when absent.</param>
    /// <param name="cancellationToken">Cancels the read when the client goes away.</param>
    /// <response code="200">The list.</response>
    /// <response code="403">The caller has no org.</response>
    /// <response code="404"><c>marketplace_property_not_found</c>: the property is not one of the org inside the caller's scope.</response>
    /// <response code="409"><c>marketplace_octorate_not_connected</c>.</response>
    /// <response code="422"><c>marketplace_property_not_linked</c>: the property has no confirmed listing on the channel manager.</response>
    [HttpGet]
    [ProducesResponseType(typeof(MarketplaceFunctionListDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MarketplaceFunctionListDto>> List(
        [FromQuery] Guid propertyId, [FromQuery] string? lang, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        NoStore();
        var list = await functions.ListAsync(caller.OrgId, caller.Scope, propertyId, LanguageOf(lang), cancellationToken);
        return Ok(MarketplaceFunctionListDto.From(list));
    }

    /// <summary>Reads the license of the property from the channel manager now and brings the requests in line with it, then answers the list.</summary>
    /// <param name="request">The property.</param>
    /// <param name="lang"><c>it</c> or <c>en</c>, for the names and the request guidance; the language of the request when absent.</param>
    /// <param name="cancellationToken">Cancels the read when the client goes away.</param>
    /// <response code="200">The list, after the check.</response>
    /// <response code="403">The caller has no org.</response>
    /// <response code="404"><c>marketplace_property_not_found</c>.</response>
    /// <response code="409"><c>marketplace_octorate_not_connected</c>.</response>
    /// <response code="422"><c>marketplace_property_not_linked</c>.</response>
    /// <response code="502">The channel manager failed or did not answer (<c>octorate_unavailable</c>, ...).</response>
    [HttpPost("refresh")]
    [Authorize(Policy = CasazenPolicies.OtaWrite)]
    [ProducesResponseType(typeof(MarketplaceFunctionListDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MarketplaceFunctionListDto>> Refresh(
        [FromBody] MarketplaceFunctionPropertyRequest request, [FromQuery] string? lang, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        NoStore();
        var list = await functions.RefreshAsync(caller.OrgId, caller.Scope, request.PropertyId ?? Guid.Empty, LanguageOf(lang), cancellationToken);
        return Ok(MarketplaceFunctionListDto.From(list));
    }

    /// <summary>
    /// Records that the host asked the channel manager for the function: status Requested, declared by the host, until the license shows it. Idempotent:
    /// asking again returns the request that exists. CasaZen asks the channel manager nothing and writes nothing to the license.
    /// </summary>
    /// <param name="moduleCode">The module: <c>chat</c>, <c>invoice</c>, <c>fiscalPrinter</c>, <c>smartOffer</c>, <c>octoDaily</c>, <c>rms</c>, <c>businessIntelligence</c>, <c>bookingEngine</c>, <c>octoSite</c>, <c>webConcierge</c>, <c>metaSearch</c> or <c>rateChecker</c>.</param>
    /// <param name="request">The property.</param>
    /// <param name="lang"><c>it</c> or <c>en</c>, for the name and the request guidance; the language of the request when absent.</param>
    /// <param name="cancellationToken">Cancels the request when the client goes away.</param>
    /// <response code="200">The function, with its request.</response>
    /// <response code="403">The caller has no org.</response>
    /// <response code="404"><c>marketplace_function_unknown</c>, <c>marketplace_property_not_found</c>.</response>
    /// <response code="409"><c>marketplace_function_already_active</c> (the license shows the module on), <c>marketplace_octorate_not_connected</c>.</response>
    /// <response code="422"><c>marketplace_property_not_linked</c>.</response>
    /// <response code="502">The channel manager failed or did not answer (<c>octorate_unavailable</c>, ...): nothing is recorded, unless the same request is already under way (answered as it is, 200).</response>
    [HttpPost("{moduleCode}/request")]
    [Authorize(Policy = CasazenPolicies.OtaWrite)]
    [ProducesResponseType(typeof(MarketplaceFunctionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MarketplaceFunctionDto>> RequestFunction(
        string moduleCode, [FromBody] MarketplaceFunctionPropertyRequest request, [FromQuery] string? lang, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        NoStore();
        var entry = await functions.RequestAsync(
            caller.OrgId,
            caller.Scope,
            request.PropertyId ?? Guid.Empty,
            moduleCode,
            caller.UserId,
            LanguageOf(lang),
            cancellationToken);
        return Ok(MarketplaceFunctionDto.From(entry));
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
