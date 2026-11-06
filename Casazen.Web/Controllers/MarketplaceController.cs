using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The Marketplace page and its Active tab (E09/F01/US01/T03, NF-54): one catalog of five groups and the list of what the org has activated.
/// The page reads CasaZen's own database and never waits for Octorate. Read with <see cref="CasazenPolicies.BookingRead"/>; the org comes from
/// the caller's membership and the scope from <see cref="IHostScopeResolver"/> (never from the request): a collaborator limited to some
/// properties sees the activations of those only, and a <c>propertyId</c> outside the scope answers 404, as for a property that does not exist.
/// </summary>
/// <remarks>
/// Not behind <c>Features:OctorateConnector</c> as a whole: the partners and the home services never depend on Octorate. While the flag is off, or
/// the org is not connected, the Octorate groups still answer, say so in <c>octorate</c> and cannot be activated.
/// </remarks>
[ApiController]
[Route("api/marketplace")]
[Authorize(Policy = CasazenPolicies.BookingRead)]
public sealed class MarketplaceController(
    IMarketplaceCatalogService catalog,
    IMarketplaceEntryDetailService details,
    IMarketplaceTexts texts,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>The five groups of the catalog, each card with its name, logo, benefit, cost, who invoices and status.</summary>
    /// <param name="propertyId">The property the statuses of the channels and tools are for; none = the whole scope of the caller.</param>
    /// <param name="lang"><c>it</c> or <c>en</c>; the language of the request when absent.</param>
    /// <param name="cancellationToken">Cancels the read when the client goes away.</param>
    /// <response code="200">The catalog.</response>
    /// <response code="403">The caller has no org.</response>
    /// <response code="404"><c>marketplace_property_not_found</c>: the property is not one of the org inside the caller's scope.</response>
    [HttpGet("catalog")]
    [ProducesResponseType(typeof(MarketplaceCatalogResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MarketplaceCatalogResponse>> GetCatalog(
        [FromQuery] Guid? propertyId, [FromQuery] string? lang, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        Response.Headers.CacheControl = "private, no-store";
        var result = await catalog.GetCatalogAsync(caller.OrgId, caller.Scope, propertyId, LanguageOf(lang), cancellationToken);
        return Ok(MarketplaceCatalogResponse.From(result));
    }

    /// <summary>The Active tab: what the org has activated (inside the caller's scope) and the suppliers the host already asked for a job.</summary>
    /// <param name="propertyId">Only that property's activations and the org-level ones; none = the whole scope of the caller.</param>
    /// <param name="lang"><c>it</c> or <c>en</c>, for the names of the licensed functions; the language of the request when absent.</param>
    /// <param name="cancellationToken">Cancels the read when the client goes away.</param>
    /// <response code="200">The integrations and the providers.</response>
    /// <response code="403">The caller has no org.</response>
    /// <response code="404"><c>marketplace_property_not_found</c>: the property is not one of the org inside the caller's scope.</response>
    [HttpGet("active")]
    [ProducesResponseType(typeof(MarketplaceActiveResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MarketplaceActiveResponse>> GetActive(
        [FromQuery] Guid? propertyId, [FromQuery] string? lang, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        Response.Headers.CacheControl = "private, no-store";
        var result = await catalog.GetActiveAsync(caller.OrgId, caller.Scope, propertyId, cancellationToken);
        return Ok(MarketplaceActiveResponse.From(result, texts, LanguageOf(lang)));
    }

    /// <summary>
    /// The detail card of an entry (MKT-02, NF-55): what it does and for whom, what is needed to activate it, cost and who invoices, the data
    /// shared, how to deactivate, the questions. The entries that go through Octorate (a channel, a tool, a function of the license) are behind
    /// <c>Features:OctorateConnector</c>: with it off they answer 404, and so does a code that does not exist, a partner that is hidden and one
    /// that the plan or the country of the org does not allow (one answer for all, so a code cannot be guessed).
    /// </summary>
    /// <param name="kind"><c>Channel</c>, <c>Tool</c>, <c>LicensedFunction</c> or <c>Partner</c> (the <c>kind</c> of a card, case is ignored).</param>
    /// <param name="code">The <c>code</c> of the card.</param>
    /// <param name="propertyId">The property the status of a channel or a tool is for; none = the whole scope of the caller.</param>
    /// <param name="lang"><c>it</c> or <c>en</c>; the language of the request when absent.</param>
    /// <param name="cancellationToken">Cancels the read when the client goes away.</param>
    /// <response code="200">The detail card.</response>
    /// <response code="403">The caller has no org.</response>
    /// <response code="404"><c>marketplace_entry_not_found</c>, or <c>marketplace_property_not_found</c> for a property outside the caller's scope.</response>
    [HttpGet("entries/{kind}/{code}")]
    [ProducesResponseType(typeof(MarketplaceEntryDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MarketplaceEntryDetailDto>> GetEntry(
        string kind, string code, [FromQuery] Guid? propertyId, [FromQuery] string? lang, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        if (!TryParseKind(kind, out var parsedKind))
            return this.ApiProblem(StatusCodes.Status404NotFound, "marketplace_entry_not_found", "MarketplaceEntryNotFound");

        Response.Headers.CacheControl = "private, no-store";
        var detail = await details.GetAsync(caller.OrgId, caller.Scope, parsedKind, code, propertyId, LanguageOf(lang), cancellationToken);
        return Ok(MarketplaceEntryDetailDto.From(detail));
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

    /// <summary>The kind named by its name (<c>channel</c>, <c>Channel</c>...), never by its number.</summary>
    private static bool TryParseKind(string kind, out MarketplaceActivationKind parsed) =>
        Enum.TryParse(kind, ignoreCase: true, out parsed) && Enum.IsDefined(parsed) && !int.TryParse(kind, out _);

    private ObjectResult NoOrg() => this.ApiProblem(StatusCodes.Status403Forbidden, "no_org_context", "NoOrganizationContext");

    private sealed record Caller(Guid OrgId, HostScope Scope);
}
