using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.ServiceRequests;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// «Descrivi di cosa hai bisogno» of the guided request of a short-rent host (WIZ-AI-REQUEST): the host tells in a few words what the job is
/// and the AI proposes the kind of service, how urgent it is, a sentence for the supplier and who to ask (<see cref="IServiceRequestAssistService"/>).
/// Same permission as creating the request (<c>servicerequest.write</c> of the short-rent context), then the property is authorized as a
/// <see cref="HostResource"/> (the caller's org, a colleague's property only with a role that reaches it, TN-3). Every call counts towards the AI
/// limits of the user and of the org. It reads and proposes: nothing is created, the host sends the request with the wizard.
/// </summary>
[ApiController]
[Route("api/service-requests/assist")]
[Authorize(Policy = CasazenPolicies.ServiceRequestWrite)]
public class ServiceRequestAssistController(
    IServiceRequestAssistService assist,
    IPropertyService properties,
    IAuthorizationService authorizationService,
    ILogger<ServiceRequestAssistController> logger) : ControllerBase
{
    /// <summary>
    /// What the AI makes of the description: the category (a code of <c>GET api/service-categories</c>), the urgency, a sentence for the supplier,
    /// one for the host, and the channel: <c>casazen</c> whenever an active CasaZen supplier covers the comune of the property for that category,
    /// <c>external</c> otherwise. Not an error when the AI cannot be used: the answer is <c>200</c> with <c>aiUsed</c> false and the <c>reason</c>
    /// (no provider, budget used up, no answer, an answer that is not a service, a job it cannot tell), and the host chooses as before.
    /// </summary>
    /// <response code="200">The suggestion, or why there is none.</response>
    /// <response code="400"><c>validation_error</c>: no property, or a description that is empty, shorter than 8 or longer than 600 characters.</response>
    /// <response code="403">The caller may not ask for interventions of this property.</response>
    /// <response code="404">No property with this id in the caller's org.</response>
    /// <response code="429"><c>rate_limited</c>: too many AI requests of the user or of the org in the last hour.</response>
    [HttpPost]
    [AiRateLimit]
    [ProducesResponseType(typeof(ServiceRequestAssistResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ServiceRequestAssistResponse>> Assist(
        [FromBody] ServiceRequestAssistRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Text.Trim().Length < ServiceRequestAssistLimits.MinTextLength)
        {
            return this.ApiProblem(
                StatusCodes.Status400BadRequest,
                ProblemCodes.ValidationError,
                "ServiceRequestAssistTextTooShort",
                ServiceRequestAssistLimits.MinTextLength);
        }

        var property = await properties.GetPropertyRecordAsync(request.PropertyId);
        if (property is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, "property_not_found", "PropertyNotFound");

        if (!await authorizationService.IsAuthorizedAsync(User, HostResource.ForProperty(property), ServiceRequestOperations.Write))
        {
            logger.LogWarning("User {UserId} denied a request assist for property {PropertyId}", User.GetUserId(), request.PropertyId);
            return Forbid();
        }

        var suggestion = await assist.AssistAsync(property, request.Text, LanguageOfTheRequest(), cancellationToken);
        return Ok(ServiceRequestAssistResponse.From(suggestion));
    }

    /// <summary>
    /// Whether an AI could answer now. The wizard asks it before it offers the box, so a platform with no AI provider shows nothing new. A cheap
    /// read: no call to a provider, no permit of the AI rate limit.
    /// </summary>
    /// <response code="200">Whether the AI is available.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ServiceRequestAssistAvailabilityResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ServiceRequestAssistAvailabilityResponse>> GetAvailability(CancellationToken cancellationToken) =>
        Ok(new ServiceRequestAssistAvailabilityResponse { Available = await assist.IsAvailableAsync(cancellationToken) });

    /// <summary>The language of the sentence for the host: English when the request is in English, Italian otherwise.</summary>
    private static string LanguageOfTheRequest() =>
        string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "it";
}
