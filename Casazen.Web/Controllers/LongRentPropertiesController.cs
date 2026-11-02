using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Core.Validation;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The properties of the long-term area with what the plain list of properties does not say (LR-07a): the APE and the cadastral data the
/// lease needs, who holds the property and from when it is free again, the lease that is coming, and the change of mode that is
/// programmed. Reading needs <c>property.read</c> in the long-rent context and is restricted in SQL to the caller's org and, unless
/// org-wide, to the properties they reach (<see cref="HostScope"/>, the scope <c>GET api/properties</c> uses). What comes from the leases (the
/// state of the property, the lease that holds it or is coming) is added only for who may read the leases (<c>lease.read</c>), otherwise it is
/// <c>null</c>: nothing is guessed. No tenant name, no amount and no other datum of a person is part of the answer.
/// </summary>
[ApiController]
[Route("api/long-rent/properties")]
[Authorize(Policy = CasazenPolicies.LongRentPropertyRead)]
public class LongRentPropertiesController(
    ILongRentPropertyService properties,
    IAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>
    /// The active properties in long-term mode of the caller's reach, ordered by name, one row each (<see cref="LongRentPropertyRow"/>).
    /// <c>view</c> is <c>All</c> (default), <c>Rented</c> (a lease holds them) or <c>Free</c> (none does, a lease on its way included; 400
    /// <c>validation_error</c> for any other value); <c>q</c>, a text looked for, without regard to case, in the name, address, city and unit
    /// (at most 100 characters). For a caller who may not read the leases the state is <c>null</c>, and <c>Rented</c> and <c>Free</c> hold nothing.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<LongRentPropertyRow>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<LongRentPropertyRow>>> GetAll(
        [FromQuery] string? view = null,
        [FromQuery] string? q = null,
        CancellationToken cancellationToken = default)
    {
        var listView = LongRentPropertyView.All;
        if (!string.IsNullOrWhiteSpace(view) && !EnumNames.TryParseDefined(view, out listView))
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "LongRentPropertyViewUnknown");

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null || await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken) is not { } scope)
            return Unauthorized();

        var mayReadLeases = (await authorizationService.AuthorizeAsync(User, CasazenPolicies.LeaseRead)).Succeeded;
        Response.Headers.CacheControl = "private, no-store";
        return Ok(await properties.GetPropertiesAsync(scope, new LongRentPropertyQuery(listView, q), mayReadLeases, cancellationToken));
    }
}
