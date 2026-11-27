using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
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
/// the flag goes on. The qualification of a segment of the self-hosted verification (E70/F02/US01/T02) is behind both identity flags: there is no segment to qualify
/// before the self-hosted flow is on.</para>
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
    /// <c>biometrics</c> entry of <c>/api/health/ready</c> is made of. Since E73/F01/US02/T03 (NF-195) also <c>retention</c>: the evidence of the reviewers past its time and the
    /// kept pictures of documents nobody uses any more (the first should always be 0, the second while one of the two flags of the self-hosted verification is on: the hourly purge deletes them), and the last run of the purge: counts and a time only.
    /// </summary>
    /// <response code="200">The state of the set and of each model, and of the retention.</response>
    [HttpGet("status")]
    [ProducesResponseType(typeof(BiometricsStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<BiometricsStatusDto>> GetStatus([FromServices] IdentityRetentionService retention, CancellationToken cancellationToken)
    {
        var models = statusReader.Read();
        return Ok(BiometricsStatusDto.From(models, await retention.StatusAsync(cancellationToken)));
    }

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

    /// <summary>
    /// Records the qualification of a segment of the self-hosted identity verification of an organization (E70/F02/US01/T02, NF-194, ADR-013-R19), after the offline
    /// qualification: <c>{ policyVersion, policyHash, weightsHash, reportSha256, requiredAssurance }</c>. The version and the two hashes must be the ones in force and the
    /// policy calibrated, or 422 <c>identity_segment_invalid</c>: a qualification is bound to what was qualified, and a new policy or new weights make it void by
    /// themselves. The mode does not change (the organization sets <c>automatic</c>). 404 <c>not_found</c> for a segment that is not of the organization. Only here, for the
    /// platform administrators. Behind <see cref="FeatureFlags.CheckInIdentityVerification"/> and <see cref="FeatureFlags.CheckInIdentitySelfHosted"/>.
    /// </summary>
    /// <response code="200">The segment, qualified.</response>
    [HttpPut("{orgId:guid}/segments/{segmentId:guid}/qualification")]
    [FeatureGate(FeatureFlags.CheckInIdentityVerification)]
    [FeatureGate(FeatureFlags.CheckInIdentitySelfHosted)]
    [ProducesResponseType(typeof(IdentitySegmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<IdentitySegmentDto>> QualifySegment(
        Guid orgId,
        Guid segmentId,
        [FromBody] QualifyIdentitySegmentDto body,
        [FromServices] IIdentitySegmentService segments,
        CancellationToken cancellationToken)
    {
        var staffUserId = User.GetUserId();
        if (string.IsNullOrWhiteSpace(staffUserId))
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var view = await segments.QualifyAsync(
            orgId,
            segmentId,
            body.PolicyVersion,
            body.PolicyHash,
            body.WeightsHash,
            body.ReportSha256,
            body.RequiredAssurance,
            staffUserId,
            cancellationToken);
        return view is null
            ? this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NotFound")
            : Ok(IdentitySegmentDto.From(view));
    }

    /// <summary>
    /// Revokes the qualification of a segment of an organization: an automatic segment returns to observation at once (E70/F02/US01/T02). 404 <c>not_found</c> for a segment
    /// that is not of the organization. Behind the same flags as the qualification.
    /// </summary>
    /// <response code="200">The segment, no longer qualified.</response>
    [HttpDelete("{orgId:guid}/segments/{segmentId:guid}/qualification")]
    [FeatureGate(FeatureFlags.CheckInIdentityVerification)]
    [FeatureGate(FeatureFlags.CheckInIdentitySelfHosted)]
    [ProducesResponseType(typeof(IdentitySegmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IdentitySegmentDto>> RevokeSegmentQualification(
        Guid orgId,
        Guid segmentId,
        [FromServices] IIdentitySegmentService segments,
        CancellationToken cancellationToken)
    {
        var staffUserId = User.GetUserId();
        if (string.IsNullOrWhiteSpace(staffUserId))
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var view = await segments.RevokeQualificationAsync(orgId, segmentId, staffUserId, cancellationToken);
        return view is null
            ? this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NotFound")
            : Ok(IdentitySegmentDto.From(view));
    }
}
