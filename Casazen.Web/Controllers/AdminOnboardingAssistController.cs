using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Validation;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Onboarding;
using Casazen.Web.Infrastructure;
using Casazen.Web.Mapping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The staff queue of the «Do it for me» requests (UX-15, NF-15, E10/F01/US03/T01): CasaZen support reads the requests of every org and takes,
/// completes or closes them. Every update writes the line <c>OnboardingAssistUpdated</c> in the log of the org <b>of the request</b>, with the staff
/// user as the actor (AC-NF15-3).
/// </summary>
/// <remarks>
/// <para><b>Policy.</b> <see cref="CasazenPolicies.AdminOnly"/>, the platform administrators; no host policy opens these routes.</para>
/// <para><b>What staff sees.</b> Ids, context codes, the property and its comune, the status and the note: never the draft, never a credential (the
/// Questura credentials stay write-only: what staff cannot see is asked of the host through its "To do"), never the person who asked.</para>
/// <para>Behind <c>Features:OctorateOnboarding</c>: 404 while it is off, before authentication.</para>
/// </remarks>
[ApiController]
[Route("api/admin/onboarding/assist-requests")]
[FeatureGate(FeatureFlags.OctorateOnboarding)]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
public sealed class AdminOnboardingAssistController(IOnboardingAssistService assist) : ControllerBase
{
    /// <summary>The queue, a page at a time, in SQL: open requests first and the oldest first.</summary>
    /// <param name="status">Narrows to one status: <c>Requested</c>, <c>InProgress</c>, <c>Done</c> or <c>Cancelled</c>; all when it is left out.</param>
    /// <param name="page">From 1.</param>
    /// <param name="pageSize">At most 100; 25 when it is left out.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <response code="200">A page of requests.</response>
    /// <response code="422"><c>assist_status_invalid</c>: the status is not one of the four.</response>
    [HttpGet]
    [PrivateAnswer]
    [ProducesResponseType(typeof(PagedResultDto<AdminAssistRequestDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PagedResultDto<AdminAssistRequestDto>>> List(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = OnboardingAssistLimits.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        OnboardingAssistStatus? filter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!EnumNames.TryParseDefined<OnboardingAssistStatus>(status, out var parsed))
                return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, OnboardingAssistCodes.StatusInvalid, "AssistStatusInvalid");

            filter = parsed;
        }

        return Ok((await assist.ListQueueAsync(filter, page, pageSize, cancellationToken)).ToDto());
    }

    /// <summary>
    /// Takes (<c>inProgress</c>), completes (<c>done</c>) or closes (<c>cancelled</c>) a request, with an optional note. <c>done</c> on a panel
    /// context confirms the matching panel step with the staff user as who completed it and closes the host's "To do".
    /// </summary>
    /// <response code="200">The request as staff reads it.</response>
    /// <response code="404"><c>assist_request_not_found</c>.</response>
    /// <response code="409"><c>assist_request_closed</c>: the request is done or cancelled already.</response>
    /// <response code="422"><c>assist_status_invalid</c>, <c>assist_note_invalid</c> (over 500 characters, or an e-mail address in it).</response>
    [HttpPut("{id:guid}")]
    [PrivateAnswer]
    [ProducesResponseType(typeof(AdminAssistRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AdminAssistRequestDto>> Update(
        Guid id,
        [FromBody] UpdateAssistRequestDto? body,
        CancellationToken cancellationToken)
    {
        var staffUserId = User.GetUserId();
        if (string.IsNullOrWhiteSpace(staffUserId))
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        if (body?.Status is not { } status)
            return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, OnboardingAssistCodes.StatusInvalid, "AssistStatusInvalid");

        return Ok((await assist.UpdateAsync(staffUserId, id, status, body.Note, cancellationToken)).ToDto());
    }
}
