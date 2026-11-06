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
/// The connection of the caller's org to its Octorate account (ADR-023, section 9.1, E02/F01/US02/T01): read it, start the consent,
/// complete it with the code of the callback, remove it. The host gives the consent on the Octorate site; CasaZen never sees an
/// Octorate username or password.
/// </summary>
/// <remarks>
/// <para><b>Policies.</b> <see cref="CasazenPolicies.OtaRead"/> to read, <see cref="CasazenPolicies.OtaWrite"/> to change (short-rent
/// <c>ota.read</c> / <c>ota.write</c>: ADR-023-R10 names a <c>ManageIntegrations</c> policy that does not exist, and none is added).
/// One consent covers every property of the org on Octorate, so connecting and disconnecting also need the caller to reach the whole
/// org (its <see cref="HostScope"/> is org-wide): a collaborator «Solo alcuni» cannot connect the org's account.</para>
/// <para>Behind <c>Features:OctorateConnector</c>: 404 while it is off, before authentication. No token, code or state is ever
/// logged or returned; the consent address of <c>connect/start</c> carries the state, as OAuth requires.</para>
/// </remarks>
[ApiController]
[Route("api/octorate")]
[FeatureGate(FeatureFlags.OctorateConnector)]
[Authorize(Policy = CasazenPolicies.OtaRead)]
public sealed class OctorateController(
    IOctorateConnectService connectService,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>Where the org's connection stands, with the quota Octorate reports while it is connected.</summary>
    /// <response code="200">The connection (Disconnected when the org never connected).</response>
    /// <response code="403">The caller has no org.</response>
    [HttpGet("connection")]
    [ProducesResponseType(typeof(OctorateConnectionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<OctorateConnectionResponse>> GetConnection(CancellationToken cancellationToken)
    {
        if (await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken) is not { } orgId)
            return NoOrg();

        Response.Headers.CacheControl = "private, no-store";
        return Ok(OctorateConnectionResponse.From(await connectService.GetStatusAsync(orgId, cancellationToken)));
    }

    /// <summary>The address of the Octorate consent page, with a new single-use state bound to the org, the caller and the callback.</summary>
    /// <response code="200">The consent address: the client sends the host there.</response>
    /// <response code="403">No org, or the caller does not reach the whole org.</response>
    [HttpPost("connect/start")]
    [Authorize(Policy = CasazenPolicies.OtaWrite)]
    [ProducesResponseType(typeof(OctorateConnectStartResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<OctorateConnectStartResponse>> Start(CancellationToken cancellationToken)
    {
        var (orgId, userId, refusal) = await OrgWideCallerAsync(cancellationToken);
        if (refusal is not null)
            return refusal;

        Response.Headers.CacheControl = "no-store";
        return Ok(new OctorateConnectStartResponse(await connectService.StartAsync(orgId, userId, cancellationToken)));
    }

    /// <summary>Completes the consent with the code and the state the callback received.</summary>
    /// <response code="200">Connected: how many properties the consent covers.</response>
    /// <response code="400"><c>octorate_state_invalid</c>: the state is unknown, used, expired or of another org, user or callback.</response>
    /// <response code="403">No org, or the caller does not reach the whole org.</response>
    /// <response code="409"><c>octorate_wrong_environment</c>: the credentials are of the other environment; the connection stays disconnected.</response>
    /// <response code="502"><c>octorate_exchange_failed</c>: Octorate did not exchange the code (start the consent again).</response>
    [HttpPost("connect/complete")]
    [Authorize(Policy = CasazenPolicies.OtaWrite)]
    [ProducesResponseType(typeof(OctorateConnectCompleteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<OctorateConnectCompleteResponse>> Complete(
        [FromBody] OctorateConnectCompleteRequest request,
        CancellationToken cancellationToken)
    {
        var (orgId, userId, refusal) = await OrgWideCallerAsync(cancellationToken);
        if (refusal is not null)
            return refusal;

        var result = await connectService.CompleteAsync(orgId, userId, request.Code, request.State, cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return Ok(new OctorateConnectCompleteResponse(result.Status, result.Environment, result.PropertyCount));
    }

    /// <summary>Disconnects the org: no call is made for it until it connects again. Mappings and imported data are kept.</summary>
    /// <response code="204">Disconnected (or never connected).</response>
    /// <response code="403">No org, or the caller does not reach the whole org.</response>
    [HttpDelete("connection")]
    [Authorize(Policy = CasazenPolicies.OtaWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Disconnect(CancellationToken cancellationToken)
    {
        var (orgId, _, refusal) = await OrgWideCallerAsync(cancellationToken);
        if (refusal is not null)
            return refusal;

        await connectService.DisconnectAsync(orgId, cancellationToken);
        return NoContent();
    }

    /// <summary>The caller's org and id when it reaches the whole org; otherwise the 403 to answer.</summary>
    private async Task<(Guid OrgId, string UserId, ObjectResult? Refusal)> OrgWideCallerAsync(CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { Length: > 0 } userId
            || await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken) is not { } orgId)
        {
            return (Guid.Empty, string.Empty, NoOrg());
        }

        var scope = await hostScopeResolver.ResolveHostScopeAsync(User, orgId, cancellationToken);
        if (scope is not { IsOrgWide: true })
        {
            return (Guid.Empty, string.Empty, this.ApiProblem(
                StatusCodes.Status403Forbidden, "octorate_connection_needs_whole_org", "OctorateConnectionNeedsWholeOrg"));
        }

        return (orgId, userId, null);
    }

    private ObjectResult NoOrg() =>
        this.ApiProblem(StatusCodes.Status403Forbidden, "no_org_context", "NoOrganizationContext");
}
