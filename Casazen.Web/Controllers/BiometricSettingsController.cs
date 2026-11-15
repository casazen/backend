using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The biometric settings of the caller's own organization (E22/F01/US01/T03, NF-24, NF-50, ADR-013-R13): whether the face check is on, the two thresholds of
/// the similarity and the attempts. Only the organization's administrators (<see cref="CasazenPolicies.OrgBillingAdmin"/>, like the rest of the org's
/// settings) read and change them, and only the caller's own org: the route has no organization, it is the one of the caller's context, so there is no
/// way to name another. Every change keeps the thresholds <b>provisional</b>; a calibration is written only by a platform administrator
/// (<see cref="AdminBiometricsController.MarkCalibrated"/>). Behind <see cref="FeatureFlags.CheckInIdentityVerification"/>, like the rest of the identification.
/// </summary>
[ApiController]
[Route("api/settings/biometrics")]
[FeatureGate(FeatureFlags.CheckInIdentityVerification)]
[Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
public sealed class BiometricSettingsController(
    IBiometricSettingsService settings,
    IOrgContextResolver orgContextResolver) : ControllerBase
{
    /// <summary>The settings in force: the row of the organization, or today's defaults (<c>isDefault</c>) while it has none.</summary>
    /// <response code="200">The settings and whether they are provisional.</response>
    [HttpGet]
    [ProducesResponseType(typeof(BiometricSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BiometricSettingsDto>> Get(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

        return Ok(BiometricSettingsDto.From(await settings.GetAsync(orgId.Value, cancellationToken)));
    }

    /// <summary>
    /// Saves the settings: a member left out keeps its value. 422 <c>biometric_thresholds_out_of_range</c> (escalate under 0.50 or accept over 0.95),
    /// <c>biometric_thresholds_order_invalid</c> (escalate not lower than accept) or <c>biometric_attempts_out_of_range</c> (not 1 to 5), and nothing is saved.
    /// A change leaves the thresholds provisional and writes a line in the activity log; the same values again write nothing.
    /// </summary>
    /// <response code="200">The settings in force after the change.</response>
    [HttpPut]
    [ProducesResponseType(typeof(BiometricSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BiometricSettingsDto>> Put([FromBody] UpdateBiometricSettingsDto body, CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

        var view = await settings.UpdateAsync(
            orgId.Value,
            new BiometricSettingsUpdate(body.Enabled, body.AcceptThreshold, body.EscalateThreshold, body.MaxAttempts),
            User.GetUserId(),
            cancellationToken);
        return Ok(BiometricSettingsDto.From(view));
    }
}
