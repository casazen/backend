using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Compliance;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.Controllers;

/// <summary>
/// The controlled passage from CasaZen to the area of Octorate that holds an Alloggiati or ISTAT outcome (CHK-15, E03/F05/US01): the host
/// asks to «Verifica nel tuo Cassetto Compliance» a property, a stay or a period, and CasaZen answers with the one allowlisted address to
/// open in the same tab, the target the dialog shows and the guide to follow in the panel.
/// </summary>
/// <remarks>
/// <para><b>What it is not.</b> It never signs in to Octorate, never reads an outcome and has no completion or result endpoint: starting a
/// handoff creates no proof, changes no compliance light and marks nothing compliant. The only trace is one line of the activity log
/// (<c>OctorateComplianceHandoffStarted</c>: org, person, property, topic, optional stay and period, the instant, the handoff id). The
/// client navigates in the current tab and the host returns with the browser's back or a normal CasaZen navigation: no callback is assumed.</para>
/// <para><b>Policy and reach.</b> <see cref="CasazenPolicies.BookingRead"/>, and the property and the stay are read in the caller's org and
/// in the reach of its <c>HostScope</c> (AM-03): a property or a stay of another org, one the caller does not reach, or a stay that is not
/// the property's, all answer the same 404. The address is never taken from the request.</para>
/// <para>Behind <c>Features:OctorateConnector</c>: 404 while it is off, before authentication.</para>
/// </remarks>
[ApiController]
[Route("api/compliance/octorate-handoffs")]
[FeatureGate(FeatureFlags.OctorateConnector)]
[Authorize(Policy = CasazenPolicies.BookingRead)]
public sealed class OctorateComplianceHandoffController(
    IOctorateComplianceHandoffService handoffs,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    IStringLocalizer<SharedResources> localizer) : ControllerBase
{
    /// <summary>Prepares the handoff and records that it started. Nothing is read from Octorate and no compliance state changes.</summary>
    /// <response code="200">The address to open, the target and the guide.</response>
    /// <response code="400">A required field is missing or the topic is not one of the two.</response>
    /// <response code="403">The caller has no org context.</response>
    /// <response code="404"><c>octorate_handoff_target_not_found</c>: the property or the booking is not the caller's, is out of its reach, or the booking is not the property's.</response>
    /// <response code="409"><c>octorate_handoff_property_not_mapped</c>: the property is not connected to Octorate; <c>octorate_handoff_destination_unavailable</c>: no destination is configured.</response>
    /// <response code="422"><c>octorate_handoff_return_path_invalid</c>, <c>octorate_handoff_period_invalid</c>.</response>
    [HttpPost]
    [ProducesResponseType(typeof(OctorateComplianceHandoffResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OctorateComplianceHandoffResponse>> Start(
        [FromBody] OctorateComplianceHandoffRequest request,
        CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { Length: > 0 } userId
            || await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken) is not { } orgId
            || await hostScopeResolver.ResolveHostScopeAsync(User, orgId, cancellationToken) is not { } scope)
        {
            return this.ApiProblem(StatusCodes.Status403Forbidden, "no_org_context", "NoOrganizationContext");
        }

        var handoff = await handoffs.StartAsync(
            scope,
            userId,
            new OctorateHandoffRequest(
                request.Topic!.Value,
                request.PropertyId!.Value,
                request.BookingId,
                request.Period,
                request.ReturnPath),
            cancellationToken);

        // A prepared handoff belongs to the caller who asked: neither a cache nor a shared copy keeps it.
        Response.Headers.CacheControl = "private, no-store";
        return Ok(OctorateComplianceHandoffResponse.From(handoff, localizer));
    }
}
