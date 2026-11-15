using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Admin;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The identity verification, for the platform administrators: which model file is missing and why, before and after the flag goes on (E22/F01/US01/T01,
/// <c>docs/runbooks/biometric-models.md</c>), and the calibration of the thresholds of an organization (E22/F01/US01/T03).
/// </summary>
/// <remarks>
/// <para><b>Policy.</b> <see cref="CasazenPolicies.AdminOnly"/>, the platform administrators; no host policy opens these routes, and the owner of an
/// organization does not pass it. That is what makes a calibration CasaZen's to declare and not the organization's: the settings of the organization
/// (<c>api/settings/biometrics</c>) have no way to write it.</para>
/// <para>Not behind <c>Features:CheckInIdentityVerification</c>: they are the tools that say whether the models are ready, and that mark a calibration, <b>before</b>
/// the flag goes on.</para>
/// </remarks>
[ApiController]
[Route("api/admin/biometrics")]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
public sealed class AdminBiometricsController(
    IBiometricModelStatusReader statusReader,
    IBiometricSettingsService settings) : ControllerBase
{
    /// <summary>
    /// Per model: name, version, licence, state, failure code and whether its SHA-256 is pinned. No path, no hash, no address. The same reading the
    /// <c>biometrics</c> entry of <c>/api/health/ready</c> is made of.
    /// </summary>
    /// <response code="200">The state of the set and of each model.</response>
    [HttpGet("status")]
    [ProducesResponseType(typeof(BiometricsStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<BiometricsStatusDto> GetStatus() => Ok(BiometricsStatusDto.From(statusReader.Read()));

    /// <summary>
    /// Marks the thresholds of an organization as calibrated now, after a calibration on real data: <c>calibratedAt</c> is written and the outcomes stop being
    /// «provisional» (E22/F01/US01/T03). Only here: an organization changes its thresholds, never says they are calibrated, and its next change makes them
    /// provisional again. An organization that never saved its settings gets a row with today's defaults, calibrated. A line goes to the activity log of the
    /// organization with the staff user as the actor. 404 <c>not_found</c> when the organization does not exist.
    /// </summary>
    /// <response code="200">The settings in force, no longer provisional.</response>
    /// <response code="404">The organization does not exist.</response>
    [HttpPut("{orgId:guid}/calibrated")]
    [ProducesResponseType(typeof(BiometricSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BiometricSettingsDto>> MarkCalibrated(Guid orgId, CancellationToken cancellationToken)
    {
        var staffUserId = User.GetUserId();
        if (string.IsNullOrWhiteSpace(staffUserId))
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var view = await settings.MarkCalibratedAsync(orgId, staffUserId, cancellationToken);
        return view is null
            ? this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NotFound")
            : Ok(BiometricSettingsDto.From(view));
    }
}
