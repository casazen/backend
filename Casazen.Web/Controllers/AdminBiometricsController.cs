using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The identity verification models, for the platform administrators (E22/F01/US01/T01): which model file is missing and why, before and after
/// the flag goes on. Runbook: <c>docs/runbooks/biometric-models.md</c>.
/// </summary>
/// <remarks>
/// <para><b>Policy.</b> <see cref="CasazenPolicies.AdminOnly"/>, the platform administrators; no host policy opens these routes, and the owner of an
/// organization does not pass it.</para>
/// <para>Not behind <c>Features:CheckInIdentityVerification</c>: it is the tool that says whether the models are ready <b>before</b> the flag goes on.</para>
/// </remarks>
[ApiController]
[Route("api/admin/biometrics")]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
public sealed class AdminBiometricsController(IBiometricModelStatusReader statusReader) : ControllerBase
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
}
