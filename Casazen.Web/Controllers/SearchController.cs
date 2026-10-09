using System.Globalization;
using Casazen.Core.Features;
using Casazen.Core.Search;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Search;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The global search of the palette, server side (UI-13a, F13 of the redesign): <c>GET /api/search?q=&amp;limit=</c> finds, among the
/// objects of the caller, the ones whose words begin with the words of the term. Behind <see cref="FeatureFlags.GlobalSearch"/>: off,
/// it answers 404 like a route that does not exist, before anything is read. Runbook: <c>docs/runbooks/global-search.md</c>.
/// </summary>
/// <remarks>
/// <para>TN-3. There is no single policy for it: the palette searches every area of the caller, and a group is there only when the
/// caller holds the permission of that group in its context (<see cref="ISearchAccessResolver"/>, with the policies of the endpoints
/// that list the same objects). The action is therefore listed among those that need only a signed-in user, with that reason
/// (<c>EndpointAuthorizationArchitectureTests</c>). What the caller reaches is decided as everywhere (<see cref="Casazen.Core.Authorization.IHostScopeResolver"/>):
/// the org of the caller, and for a collaborator limited to some properties those only, in SQL.</para>
/// <para>Rate limited per user (<see cref="RateLimitPolicies.GlobalSearch"/>). The term is personal data when it is a name: it is not
/// logged, and the answer is never cached (<c>Cache-Control: private, no-store</c>).</para>
/// </remarks>
[ApiController]
[Route("api/search")]
[Authorize(Policy = CasazenPolicies.Authenticated)]
[FeatureGate(FeatureFlags.GlobalSearch)]
[EnableRateLimiting(RateLimitPolicies.GlobalSearch)]
public class SearchController(
    ISearchAccessResolver accessResolver,
    IGlobalSearchService searchService) : ControllerBase
{
    /// <summary>
    /// Results grouped by kind. <c>q</c> is the term (2 to 64 characters; case and accents do not matter; each word is looked for
    /// at the beginning of the words of a name, a city, a code); <c>limit</c> is the most results per group (default 5, at most 20).
    /// </summary>
    /// <response code="200">The groups with results, possibly none.</response>
    /// <response code="400"><c>search_query_too_short</c> or <c>search_query_too_long</c>.</response>
    /// <response code="404">The flag is off.</response>
    /// <response code="429"><c>rate_limited</c>.</response>
    [HttpGet]
    [ProducesResponseType(typeof(GlobalSearchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<GlobalSearchResponse>> Search(
        [FromQuery] string? q,
        [FromQuery] int? limit,
        CancellationToken cancellationToken)
    {
        var term = q?.Trim() ?? string.Empty;
        if (term.Length < SearchText.MinQueryLength)
        {
            return this.ApiProblem(
                StatusCodes.Status400BadRequest,
                SearchErrorCodes.QueryTooShort,
                SearchErrorCodes.QueryTooShortMessageKey,
                SearchText.MinQueryLength);
        }

        if (term.Length > SearchText.MaxQueryLength)
        {
            return this.ApiProblem(
                StatusCodes.Status400BadRequest,
                SearchErrorCodes.QueryTooLong,
                SearchErrorCodes.QueryTooLongMessageKey,
                SearchText.MaxQueryLength);
        }

        // The answer holds names of people: never kept by a cache or the browser history.
        Response.Headers.CacheControl = "private, no-store";

        var query = SearchText.Parse(term);
        if (query.IsEmpty)
            return Ok(GlobalSearchResponse.From(GlobalSearchResult.Empty));

        var access = await accessResolver.ResolveAsync(User, cancellationToken);
        if (access.IsEmpty)
            return Ok(GlobalSearchResponse.From(GlobalSearchResult.Empty));

        var perGroup = Math.Clamp(limit ?? SearchLimits.DefaultLimit, 1, SearchLimits.MaxLimit);
        var result = await searchService.SearchAsync(
            new GlobalSearchRequest(query, perGroup, access.Host, access.SupplierOrgId, CultureInfo.CurrentUICulture),
            cancellationToken);

        return Ok(GlobalSearchResponse.From(result));
    }
}
