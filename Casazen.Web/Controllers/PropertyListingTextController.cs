using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// «Suggerisci un testo» of the wizard «Aggiungi immobile» (step «Foto e annuncio»): the AI writes the headline and the description of the
/// listing from the photos, the comune and the data of the property (<see cref="IPropertyListingTextService"/>). Nothing is saved here: the
/// host reads the text, changes it and saves it with the step. Short-rent only, like every short-stay side of a property: the permission is
/// <c>property.write</c> and the row is authorized like the photos (the property of the caller's org, a colleague's one only with a role that
/// reaches it, TN-3). Every call counts towards the AI limits of the user and of the org (<see cref="AiRateLimitAttribute"/>).
/// </summary>
[ApiController]
[Route("api/properties/{id:guid}/listing-text")]
[Authorize(Policy = CasazenPolicies.PropertyWrite)]
public class PropertyListingTextController(
    IPropertyListingTextService listingText,
    IPropertyService properties,
    IAuthorizationService hostAuthorization,
    ILogger<PropertyListingTextController> logger) : ControllerBase
{
    /// <summary>Six reduced photos in base64 and the JSON around them (<see cref="AiPhotoLimits.RequestOverheadBytes"/>).</summary>
    private const long MaxRequestBytes =
        ListingTextLimits.MaxPhotos * (long)AiPhotoLimits.MaxPhotoBytes * 4 / 3 + AiPhotoLimits.RequestOverheadBytes;

    /// <summary>
    /// A headline (at most 60 characters) and a description (at most 2,000, in paragraphs) for the listing, with what they were written from
    /// (<c>basedOn</c>: the photos the AI read, the comune, the listings of the same comune compared, the amenities). The body carries up to
    /// six photos of the gallery reduced by the web app (base64, cover first) and <c>regenerate</c> for a new text when one was already written
    /// for the same data and photos. When no AI can write it, <c>aiUsed</c> is false with the <c>reason</c> (<c>ai_not_configured</c>,
    /// <c>ai_budget_exhausted</c>, <c>ai_unavailable</c>, <c>ai_invalid_answer</c>) and the client composes the text from the data.
    /// </summary>
    /// <response code="200">The suggestion, or why there is none.</response>
    /// <response code="400"><c>validation_error</c>: the body is not a list of photos.</response>
    /// <response code="403">The caller may not change this property.</response>
    /// <response code="404">No property with this id in the caller's org.</response>
    /// <response code="422"><c>ai_photos_invalid</c>: more than six photos, or one that is not a reduced JPEG, PNG or WebP image.</response>
    /// <response code="429"><c>rate_limited</c>: too many AI requests of the user or of the org in the last hour.</response>
    [HttpPost("suggestion")]
    [AiRateLimit]
    [RequestSizeLimit(MaxRequestBytes)]
    [ProducesResponseType(typeof(ListingTextSuggestion), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ListingTextSuggestion>> Suggest(
        Guid id,
        [FromBody] ListingTextSuggestionRequest request,
        CancellationToken cancellationToken)
    {
        var (property, denied) = await AuthorizeAsync(id);
        if (denied is not null)
            return denied;

        var photos = AiPhotos.Decode(request.Photos, ListingTextLimits.MaxPhotos);
        var suggestion = await listingText.SuggestAsync(property!, photos, request.Regenerate, cancellationToken);
        return Ok(suggestion);
    }

    private async Task<(Property? Property, ActionResult? Denied)> AuthorizeAsync(Guid id)
    {
        var userId = User.FindFirst("sub")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
            return (null, Unauthorized());

        var property = await properties.GetPropertyRecordAsync(id);
        if (property is null)
            return (null, NotFound());

        if (!await hostAuthorization.IsAuthorizedAsync(User, HostResource.ForProperty(property), PropertyOperations.Write))
        {
            logger.LogWarning("User {UserId} denied a listing text suggestion for property {PropertyId}", userId, id);
            return (null, Forbid());
        }

        return (property, null);
    }
}

/// <summary>The body of <c>POST api/properties/{id}/listing-text/suggestion</c>.</summary>
public sealed class ListingTextSuggestionRequest
{
    /// <summary>
    /// Up to six photos of the gallery, cover first, reduced by the web app to about 1,024 pixels: base64 of a JPEG, PNG or WebP image (a
    /// <c>data:</c> prefix is accepted). Empty: the AI writes from the data alone.
    /// </summary>
    [MaxLength(ListingTextLimits.MaxPhotos, ErrorMessage = "AiPhotosInvalid")]
    public List<string> Photos { get; set; } = [];

    /// <summary>True for «Rigenera»: a new text even when one was written for the same data and photos a moment ago.</summary>
    public bool Regenerate { get; set; }
}
