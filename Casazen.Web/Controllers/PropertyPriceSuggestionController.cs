using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Pricing;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// «Calcola con l'AI» of the step «Prezzi» of the wizard «Aggiungi immobile» (WIZ-AI-PRICE): the AI proposes the nightly rate of a
/// property, with the cleaning fee, the weekend surcharge and the minimum stay that go with it, from the saved data of the property, its
/// comune and the nightly rates of the other homes published in the same comune, only summed up. Short-rent only, like every short-stay
/// side of a property: <c>property.read</c> of the short-rent context for the controller and <c>property.write</c> for the action (the
/// one who proposes a price is the one who may set it), then the property is authorized as a <see cref="HostResource"/> (the caller's
/// org, a colleague's property only with a role that reaches it, TN-3). Rate limited per user and org with the other AI endpoints. It
/// reads and proposes: the property is not changed, the host does that with «Continua».
/// </summary>
[ApiController]
[Route("api/properties/{propertyId:guid}/price-suggestion")]
[Authorize(Policy = CasazenPolicies.PropertyRead)]
public class PropertyPriceSuggestionController(
    IPropertyService propertyService,
    IListingPriceSuggestionService suggestions,
    IAuthorizationService authorizationService,
    ILogger<PropertyPriceSuggestionController> logger) : ControllerBase
{
    /// <summary>
    /// The price for the property. Not an error when the AI cannot be used: the answer is <c>200</c> with <c>aiUsed</c> false and the
    /// <c>reason</c> (no provider, budget used up, no answer, an answer that is not a price), and with the price of the similar homes when
    /// there are enough of them; the host then types the price as before. The body is optional: without it the price is «in line with the area».
    /// </summary>
    /// <param name="propertyId">The property identifier.</param>
    /// <param name="request">Where the host wants the price against the comparable homes.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <response code="200">The suggestion, or why the AI is not behind it.</response>
    /// <response code="400">The positioning is not one of the values.</response>
    /// <response code="403">The caller may not change this property.</response>
    /// <response code="404">No property with this id in the caller's org.</response>
    /// <response code="429">Too many AI requests: wait for the time in <c>Retry-After</c>.</response>
    [HttpPost]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [AiRateLimit]
    [ProducesResponseType(typeof(PriceSuggestionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PriceSuggestionResponse>> Suggest(
        Guid propertyId,
        [FromBody] PriceSuggestionRequest? request,
        CancellationToken cancellationToken)
    {
        var (property, denied) = await AuthorizeAsync(propertyId, PropertyOperations.Write);
        if (denied is not null)
            return denied;

        var suggestion = await suggestions.SuggestAsync(
            property,
            request?.Positioning ?? PricePositioning.Market,
            LanguageOfTheRequest(),
            cancellationToken);
        return Ok(PriceSuggestionResponse.From(suggestion));
    }

    /// <summary>
    /// Whether an AI could answer now. The step of the wizard asks it before offering «Vuoi che calcoli io il prezzo con l'AI?», so a
    /// platform with no AI provider shows nothing new. A cheap read: no call to a provider, no permit of the AI rate limit.
    /// </summary>
    /// <param name="propertyId">The property identifier.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <response code="200">Whether the AI is available.</response>
    /// <response code="403">The caller may not read this property.</response>
    /// <response code="404">No property with this id in the caller's org.</response>
    [HttpGet]
    [ProducesResponseType(typeof(PriceSuggestionAvailabilityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PriceSuggestionAvailabilityResponse>> GetAvailability(
        Guid propertyId,
        CancellationToken cancellationToken)
    {
        var (_, denied) = await AuthorizeAsync(propertyId, PropertyOperations.Read);
        if (denied is not null)
            return denied;

        return Ok(new PriceSuggestionAvailabilityResponse { Available = await suggestions.IsAvailableAsync(cancellationToken) });
    }

    /// <summary>
    /// Loads the property (tenant-filtered: another org's property is 404) and authorizes <paramref name="operation"/> on it; a visible
    /// property the caller may not use is 403.
    /// </summary>
    private async Task<(Property Property, ActionResult? Denied)> AuthorizeAsync(Guid propertyId, HostOperationRequirement operation)
    {
        var property = await propertyService.GetPropertyAsync(propertyId);
        if (property is null)
            return (null!, NotFound());

        if (!await authorizationService.IsAuthorizedAsync(User, HostResource.ForProperty(property), operation))
        {
            logger.LogWarning(
                "User {UserId} denied {Permission} on the price suggestion of property {PropertyId}",
                User.GetUserId(), operation.PermissionKey, propertyId);
            return (property, Forbid());
        }

        return (property, null);
    }

    /// <summary>The language of the explanation: English when the request is in English, Italian otherwise.</summary>
    private static string LanguageOfTheRequest() =>
        string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "it";
}
