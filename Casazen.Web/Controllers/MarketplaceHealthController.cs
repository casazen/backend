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
/// The health of the active integrations (E09/F03/US01/T01, NF-57, MKT-04, OCT-14): per property and channel, tool or function of the channel manager's license, the status,
/// the last sync, the error in simple words and the quota warning. Behind <c>Features:OctorateConnector</c>: 404 while it is off, before authentication.
/// </summary>
/// <remarks>
/// <para><b>Policy.</b> <see cref="CasazenPolicies.OtaRead"/>. The org comes from the caller's membership and the scope from <see cref="IHostScopeResolver"/>, never from the
/// request: a collaborator limited to some properties reads the rows of those only, and a property outside the scope answers 404, as one that does not exist.</para>
/// <para><b>A read, not a call.</b> The page is answered from CasaZen's own rows and the quota of the last sync (the job <c>marketplace-connection-sync</c>, every 30 minutes);
/// nothing is asked of Octorate while it loads. Every answer is <c>private, no-store</c>.</para>
/// </remarks>
[ApiController]
[Route("api/marketplace/health")]
[FeatureGate(FeatureFlags.OctorateConnector)]
[Authorize(Policy = CasazenPolicies.OtaRead)]
public sealed class MarketplaceHealthController(
    IMarketplaceHealthService health,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>One row for each channel, tool or function of the license of the caller's properties that is not turned off, with its status, last sync, problem and quota.</summary>
    /// <param name="propertyId">Only this property's rows; none = every property of the caller's scope.</param>
    /// <param name="lang"><c>it</c> or <c>en</c>, for the names and the messages; the language of the request when absent.</param>
    /// <param name="cancellationToken">Cancels the read when the client goes away.</param>
    /// <response code="200">The health.</response>
    /// <response code="403">The caller has no org.</response>
    /// <response code="404"><c>marketplace_property_not_found</c>: the property is not one of the org inside the caller's scope.</response>
    [HttpGet]
    [ProducesResponseType(typeof(MarketplaceHealthDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MarketplaceHealthDto>> Get(
        [FromQuery] Guid? propertyId, [FromQuery] string? lang, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        NoStore();
        var result = await health.GetHealthAsync(caller.OrgId, caller.Scope, propertyId, LanguageOf(lang), cancellationToken);
        return Ok(MarketplaceHealthDto.From(result));
    }

    /// <summary>The caller's org and scope; <c>null</c> when it has no org or reaches nothing in it.</summary>
    private async Task<Caller?> CallerAsync(CancellationToken cancellationToken)
    {
        if (await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken) is not { } orgId
            || await hostScopeResolver.ResolveHostScopeAsync(User, orgId, cancellationToken) is not { } scope)
        {
            return null;
        }

        return new Caller(orgId, scope);
    }

    /// <summary><c>it</c> or <c>en</c>: the query when it names one, otherwise the language of the request.</summary>
    private static string LanguageOf(string? lang) =>
        MarketplaceLanguages.Normalize(string.IsNullOrWhiteSpace(lang) ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : lang);

    private void NoStore() => Response.Headers.CacheControl = "private, no-store";

    private ObjectResult NoOrg() => this.ApiProblem(StatusCodes.Status403Forbidden, "no_org_context", "NoOrganizationContext");

    private sealed record Caller(Guid OrgId, HostScope Scope);
}
