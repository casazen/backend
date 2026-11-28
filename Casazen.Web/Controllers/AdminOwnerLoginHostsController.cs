using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Validation;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The platform administrators' side of the owner sign-in on the agencies' own domains (E40/F01/US02/T01, NF-141, site integration plan §5.7). The Auth0 tenant
/// belongs to CasaZen: while <c>Auth0HostSync</c> is off a platform administrator adds the three URLs of a verified domain to the SPA application of the
/// environment by hand (runbook <c>docs/runbooks/auth0.md</c> § 13) and records it here; the agency reads «in attesa di abilitazione da parte di CasaZen» until
/// then. Nothing here calls Auth0. 404 while <c>WhiteLabelHost</c> is off, before authentication.
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
[FeatureGate(FeatureFlags.WhiteLabelHost)]
public class AdminOwnerLoginHostsController(IOrgDomainService orgDomainService) : ControllerBase
{
    public const string StateInvalidCode = "owner_login_state_invalid";

    [HttpGet("owner-login-hosts")]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerLoginHostDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<OwnerLoginHostDto>>> List(
        [FromQuery] string? state,
        CancellationToken cancellationToken)
    {
        var wanted = OwnerLoginHostState.Pending;
        if (!string.IsNullOrWhiteSpace(state) && !EnumNames.TryParseDefined(state, out wanted))
            return this.ApiProblem(StatusCodes.Status400BadRequest, StateInvalidCode, "OwnerLoginStateInvalid");

        var queue = await orgDomainService.ListOwnerLoginHostsAsync(wanted, cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return Ok(queue
            .Select(item => new OwnerLoginHostDto
            {
                OrgId = item.OrgId,
                DisplayName = item.DisplayName,
                Domain = item.Domain,
                State = item.State,
                DomainVerified = item.DomainVerified,
            })
            .ToList());
    }

    [HttpPut("orgs/{orgId:guid}/owner-login-state")]
    [ProducesResponseType(typeof(OwnerLoginStateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerLoginStateDto>> SetState(
        Guid orgId,
        [FromBody] OwnerLoginStateDto request,
        CancellationToken cancellationToken)
    {
        var result = await orgDomainService.SetOwnerLoginStateAsync(orgId, request.State!.Value, cancellationToken);
        return result.Outcome switch
        {
            SetOwnerLoginStateOutcome.Success => Ok(new OwnerLoginStateDto { State = result.State }),
            SetOwnerLoginStateOutcome.NotFound => NotFound(),
            _ => this.ApiProblem(
                StatusCodes.Status422UnprocessableEntity, OrgDomainController.DomainNotVerifiedCode, "OrgDomainNotVerified"),
        };
    }
}
