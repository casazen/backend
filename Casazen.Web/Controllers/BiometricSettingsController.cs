using Casazen.Core.Features;
using Casazen.Core.Identity;
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
/// <para>
/// The segments of the self-hosted verification (E70/F02/US01/T02, <c>segments</c>) are behind <see cref="FeatureFlags.CheckInIdentitySelfHosted"/> too. The one id a
/// route names is the segment's (<c>segments/{id}</c>), a segment of the caller's org: another org's is a 404. The organization never qualifies a segment: only a platform
/// administrator does (<see cref="AdminBiometricsController.QualifySegment"/>).
/// </para>
/// </summary>
[ApiController]
[Route("api/settings/biometrics")]
[FeatureGate(FeatureFlags.CheckInIdentityVerification)]
[Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
public sealed class BiometricSettingsController(
    IBiometricSettingsService settings,
    IOrgContextResolver orgContextResolver) : ControllerBase
{
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

    [HttpGet("segments")]
    [FeatureGate(FeatureFlags.CheckInIdentitySelfHosted)]
    [ProducesResponseType(typeof(IdentitySegmentsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IdentitySegmentsDto>> GetSegments([FromServices] IIdentitySegmentService segments, CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

        return Ok(IdentitySegmentsDto.From(await segments.ListAsync(orgId.Value, cancellationToken)));
    }

    [HttpPost("segments")]
    [FeatureGate(FeatureFlags.CheckInIdentitySelfHosted)]
    [ProducesResponseType(typeof(IdentitySegmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<IdentitySegmentDto>> CreateSegment(
        [FromBody] CreateIdentitySegmentDto body,
        [FromServices] IIdentitySegmentService segments,
        CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

        var view = await segments.CreateAsync(orgId.Value, body.PropertyId, body.FormatId, body.Channel, User.GetUserId(), cancellationToken);
        return Ok(IdentitySegmentDto.From(view));
    }

    /// <summary>
    /// Sets the mode of a segment of the organization: <c>{ mode, reason? }</c>. <c>automatic</c> only when the segment is qualified for the policy and the weights in force
    /// (422 <c>identity_segment_not_qualified</c>); <c>suspended</c> always, with a reason of at most 200 characters (422 <c>identity_segment_invalid</c>); <c>observation</c>
    /// always. A segment of another organization is 404 <c>not_found</c>, like one that does not exist. A line goes to the activity log of the organization.
    /// </summary>
    /// <response code="200">The segment after the change.</response>
    [HttpPut("segments/{id:guid}")]
    [FeatureGate(FeatureFlags.CheckInIdentitySelfHosted)]
    [ProducesResponseType(typeof(IdentitySegmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<IdentitySegmentDto>> UpdateSegment(
        Guid id,
        [FromBody] UpdateIdentitySegmentDto body,
        [FromServices] IIdentitySegmentService segments,
        CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

        if (!IdentitySegmentModeCodes.TryParse(body.Mode, out var mode))
            return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, IdentityApiErrors.IdentitySegmentInvalid, "IdentitySegmentInvalid");

        var view = await segments.SetModeAsync(orgId.Value, id, mode, body.Reason, User.GetUserId(), cancellationToken);
        return view is null
            ? this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NotFound")
            : Ok(IdentitySegmentDto.From(view));
    }
}
