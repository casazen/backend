using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Assistant;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The drafts of the assistant (E05/F05/US02/T01, NF-66, NF-67): a person asks for a draft of the last message of a conversation now, and a host tries the assistant on an example.
/// Behind <c>Features:MessageAssistant</c>: 404 while it is off, before authentication. Every answer is private and not cached.
/// </summary>
/// <remarks>
/// <para><b>A draft now</b> needs <see cref="CasazenPolicies.BookingWrite"/> and the row check of the conversation (the caller's <see cref="HostScope"/> in SQL: a conversation of another
/// org, or out of the reach of a collaborator «Solo alcuni», is the 404 of a conversation that does not exist). The draft goes to the approval queue, never to the guest.</para>
/// <para><b>The simulator</b> needs <see cref="CasazenPolicies.PropertyWrite"/> and the row check of the property (<see cref="PropertyOperations.Write"/>), and is limited per person
/// (<see cref="RateLimitPolicies.AssistantSimulate"/>, 20 an hour) on top of the limits of the AI endpoints (<see cref="AiRateLimitAttribute"/>). It writes and sends nothing.</para>
/// </remarks>
[ApiController]
[Route("api/assistant")]
[FeatureGate(FeatureFlags.MessageAssistant)]
[PrivateAnswer]
public sealed class AssistantDraftsController(
    IAssistantDraftService drafts,
    IPropertyService propertyService,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    IAuthorizationService authorizationService) : ControllerBase
{
    /// <summary>
    /// A draft now of the last message of the guest of the conversation (the key the hub gave: <c>ota:{id}</c> or <c>direct:{bookingId}</c>): the age of the message and an answer already
    /// given are not looked at, nothing is sent, and a draft of the conversation that waits in the queue is superseded by the new one. The answer is the decision.
    /// </summary>
    /// <response code="200">The decision: <c>Drafted</c> (with the item of the queue), <c>Escalated</c> or <c>NotHandled</c> (with its <c>reason</c>).</response>
    /// <response code="400"><c>thread_key_invalid</c>.</response>
    /// <response code="404"><c>thread_not_found</c>: not in the caller's org or reach.</response>
    /// <response code="409"><c>assistant_no_guest_message</c>: the guest has written nothing.</response>
    /// <response code="429"><c>rate_limited</c>: the limits of the AI endpoints.</response>
    [HttpPost("threads/{threadKey}/draft")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [AiRateLimit]
    [ProducesResponseType(typeof(AssistantDraftOutcomeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AssistantDraftOutcomeDto>> Draft(string threadKey, CancellationToken cancellationToken)
    {
        if (!MessageThreadKey.TryParse(threadKey, out var key))
            return this.ApiProblem(StatusCodes.Status400BadRequest, MessageThreadKey.InvalidCode, "ThreadKeyInvalid");

        if (User.GetUserId() is not { Length: > 0 } userId
            || await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken) is not { } orgId)
        {
            return NoOrg();
        }

        var scope = await hostScopeResolver.ResolveHostScopeAsync(User, orgId, cancellationToken);
        if (scope is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        return Ok(AssistantDraftOutcomeDto.From(await drafts.DraftNowAsync(scope, key, userId, cancellationToken)));
    }

    /// <summary>
    /// Tries the assistant on an example text with the current settings of a property (the switch of the organization may still be off): the situation, the urgency, the confidence, the
    /// draft, the facts used and what the gate would decide. Writes no log, no item of the queue and no «To do», sends nothing; it spends the AI budget like any call.
    /// </summary>
    /// <response code="200">The simulation (<c>decision</c> <c>NotHandled</c> with its <c>reason</c> when no AI provider is in effect).</response>
    /// <response code="404"><c>property_not_found</c>: not in the caller's org or reach.</response>
    /// <response code="422"><c>assistant_simulation_invalid</c>: the text is empty or longer than 2,000 characters, or the language is not two letters.</response>
    /// <response code="429"><c>rate_limited</c>.</response>
    [HttpPost("simulate")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [EnableRateLimiting(RateLimitPolicies.AssistantSimulate)]
    [AiRateLimit]
    [RequestSizeLimit(16384)]
    [ProducesResponseType(typeof(AssistantSimulationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AssistantSimulationDto>> Simulate([FromBody] SimulateAssistantRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (User.GetUserId() is not { Length: > 0 } || await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken) is not { } orgId)
            return NoOrg();

        if (!await AssistantAccess.CanUsePropertyAsync(propertyService, authorizationService, User, orgId, request.PropertyId, PropertyOperations.Write))
            return this.ApiProblem(StatusCodes.Status404NotFound, "property_not_found", "PropertyNotFound");

        var simulation = await drafts.SimulateAsync(orgId, request.PropertyId, request.Text, request.Language, cancellationToken);
        return Ok(AssistantSimulationDto.From(simulation));
    }

    private ObjectResult NoOrg() => this.ApiProblem(StatusCodes.Status403Forbidden, "no_org_context", "NoOrganizationContext");
}
