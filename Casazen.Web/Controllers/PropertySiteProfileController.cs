using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.DTOs;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.SiteProfile;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The site profile of a property (DB-11a): the optional facts and texts the property page of the host's booking site shows when they are
/// filled in (floor, renovation, energy class, kind of place, children, latest arrival, video and tour, the zone and what is near, how
/// check-in works, highlights, safety notes and the labels of the site). Short-rent only, like every short-stay side of a property: the
/// permissions are <c>property.read</c> / <c>property.write</c> of the short-rent context and the row is authorized like the photos
/// (the property of the caller's org, a colleague's one only with a role that reaches it, TN-3). The fields the wizard «Aggiungi
/// immobile» owns (area, beds, rules, times, switch «Mostra la casa sul sito») are not written here.
/// </summary>
[ApiController]
[Route("api/properties/{id:guid}/site-profile")]
[Authorize(Policy = CasazenPolicies.PropertyRead)]
public class PropertySiteProfileController(
    IPropertySiteProfileService profiles,
    IPropertyPhotoAltService photoAlt,
    IPropertyFloorPlanService floorPlans,
    IPropertyPoiService pois,
    IPropertyService properties,
    IAuthorizationService hostAuthorization,
    IAdminAccessAuditService adminAccessAuditService,
    ILogger<PropertySiteProfileController> logger) : ControllerBase
{
    /// <summary>
    /// The profile as the editor shows it: every field, <c>null</c> where the host never said; <c>derivedTags</c> are the labels the data of
    /// the house gives it (a pool, a terrace, pets, the coast...), which the site shows whatever the host chose.
    /// </summary>
    /// <response code="200">The profile.</response>
    /// <response code="403">The caller may not read this property.</response>
    /// <response code="404">No property with this id in the caller's org.</response>
    [HttpGet]
    [ProducesResponseType(typeof(SitePropertyProfileView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SitePropertyProfileView>> Get(Guid id, CancellationToken cancellationToken)
    {
        var (_, denied) = await AuthorizeAsync(id, PropertyOperations.Read);
        if (denied is not null)
            return denied;

        var view = await profiles.GetAsync(id, cancellationToken);
        return view is null ? NotFound() : Ok(view);
    }

    /// <summary>
    /// Saves the profile: the body is the whole profile, and whatever it leaves out is cleared. Texts are written in Italian and, if the
    /// host wants, in English (nothing is translated for them); a video must be a YouTube or Vimeo address and a tour a Matterport one,
    /// stored in their canonical embed form; the labels and the highlight icons come from fixed lists. Answers with what is stored.
    /// </summary>
    /// <response code="200">The profile as stored.</response>
    /// <response code="400"><c>validation_error</c>: a text or a list is absurdly long.</response>
    /// <response code="403">The caller may not change this property.</response>
    /// <response code="404">No property with this id in the caller's org.</response>
    /// <response code="422">
    /// <c>property_not_bookable_in_long_mode</c> (a long-rent property has no page on the site), or one of
    /// <c>property_site_profile_text_too_long</c>, <c>_italian_required</c>, <c>_too_many_highlights</c>, <c>_highlight_icon_unknown</c>,
    /// <c>_too_many_safety_notes</c>, <c>_tag_unknown</c>, <c>_floor_level_invalid</c>, <c>_renovated_year_invalid</c>,
    /// <c>_energy_class_invalid</c>, <c>_energy_index_invalid</c>, <c>_energy_index_without_class</c>, <c>_check_in_window_invalid</c>,
    /// <c>_video_url_invalid</c>, <c>_tour_url_invalid</c>, with the <c>field</c> that broke the rule. Nothing is saved.
    /// </response>
    [HttpPut]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(SitePropertyProfileView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SitePropertyProfileView>> Put(
        Guid id,
        [FromBody] SitePropertyProfileRequest request,
        CancellationToken cancellationToken)
    {
        var (property, denied) = await AuthorizeAsync(id, PropertyOperations.Write, "PropertySiteProfile.Update");
        if (denied is not null)
            return denied;

        var view = await profiles.SaveAsync(id, request.ToInput(), cancellationToken);
        if (view is null)
            return NotFound();

        logger.LogInformation("Site profile of property {PropertyId} of org {OrgId} saved", id, property!.OrgId);
        return Ok(view);
    }

    /// <summary>
    /// The photos of the gallery in display order, each with its alternative text and size in pixels (<c>null</c> = the host wrote none / not known).
    /// </summary>
    /// <response code="200">The photos.</response>
    /// <response code="403">The caller may not read this property.</response>
    /// <response code="404">No property with this id in the caller's org.</response>
    [HttpGet("photos")]
    [ProducesResponseType(typeof(IReadOnlyList<PublicPropertyPhotoDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<PublicPropertyPhotoDto>>> GetPhotos(Guid id, CancellationToken cancellationToken)
    {
        var (_, denied) = await AuthorizeAsync(id, PropertyOperations.Read);
        if (denied is not null)
            return denied;

        var photos = await photoAlt.GetAsync(id, cancellationToken);
        return photos is null ? NotFound() : Ok(photos);
    }

    /// <summary>
    /// Saves the alternative text of the photos (Italian always, English if the host wants): the body lists the photos that have one, and the
    /// gallery photos it does not list lose theirs, so the editor sends them all. The size of a photo is read from the file when it is uploaded
    /// and is not part of the body. Answers with the gallery as stored.
    /// </summary>
    /// <response code="200">The photos as stored.</response>
    /// <response code="400"><c>validation_error</c>: a text is absurdly long.</response>
    /// <response code="403">The caller may not change this property.</response>
    /// <response code="404">No property in the caller's org, or <c>property_photo_not_found</c>: an address that is not in its gallery.</response>
    /// <response code="422"><c>property_site_profile_italian_required</c> or <c>_text_too_long</c> with <c>field: photos</c>. Nothing is saved.</response>
    [HttpPut("photos")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(IReadOnlyList<PublicPropertyPhotoDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<IReadOnlyList<PublicPropertyPhotoDto>>> PutPhotos(
        Guid id,
        [FromBody] PropertyPhotoAltRequest request,
        CancellationToken cancellationToken)
    {
        var (_, denied) = await AuthorizeAsync(id, PropertyOperations.Write, "PropertySiteProfile.PhotoAlt");
        if (denied is not null)
            return denied;

        var photos = await photoAlt.SaveAsync(id, request.ToInput(), cancellationToken);
        return photos is null ? NotFound() : Ok(photos);
    }

    /// <summary>The levels of the floor plan, lowest floor first (each with its image address, size and area).</summary>
    /// <response code="200">The levels.</response>
    /// <response code="403">The caller may not read this property.</response>
    /// <response code="404">No property with this id in the caller's org.</response>
    [HttpGet("floor-plans")]
    [ProducesResponseType(typeof(IReadOnlyList<PublicFloorPlanDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<PublicFloorPlanDto>>> GetFloorPlans(Guid id, CancellationToken cancellationToken)
    {
        var (_, denied) = await AuthorizeAsync(id, PropertyOperations.Read);
        if (denied is not null)
            return denied;

        var plans = await floorPlans.ListAsync(id, cancellationToken);
        return plans is null ? NotFound() : Ok(plans);
    }

    /// <summary>
    /// Adds a level to the floor plan (multipart: <c>image</c> PNG or JPEG of 5 MB at most checked on its content, <c>nameIt</c> required, <c>nameEn</c>, <c>level</c>, <c>areaSqm</c>);
    /// at most six levels. All or nothing.
    /// </summary>
    /// <response code="200">The level as stored.</response>
    /// <response code="403">The caller may not change this property.</response>
    /// <response code="404">No property with this id in the caller's org.</response>
    /// <response code="413">The request is larger than the image allows.</response>
    /// <response code="422"><c>property_floor_plan_none</c>, <c>_invalid_type</c>, <c>_invalid_size</c>, <c>_limit_reached</c>, <c>_area_invalid</c>, or <c>property_site_profile_italian_required</c> / <c>_text_too_long</c> / <c>_floor_level_invalid</c> with the <c>field</c>.</response>
    [HttpPost("floor-plans")]
    [Consumes("multipart/form-data")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [RequestSizeLimit(FloorPlanLimits.MaxFileSizeBytes + (1024 * 1024))]
    [RequestFormLimits(MultipartBodyLengthLimit = FloorPlanLimits.MaxFileSizeBytes + (1024 * 1024))]
    [ProducesResponseType(typeof(PublicFloorPlanDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PublicFloorPlanDto>> AddFloorPlan(Guid id, [FromForm] FloorPlanForm form, CancellationToken cancellationToken)
    {
        var (_, denied) = await AuthorizeAsync(id, PropertyOperations.Write, "PropertySiteProfile.FloorPlan");
        if (denied is not null)
            return denied;

        var plan = await floorPlans.AddAsync(id, form.Image, form.ToInput(), cancellationToken);
        return plan is null ? NotFound() : Ok(plan);
    }

    /// <summary>Changes the name, the floor and the area of a level (the image stays).</summary>
    /// <response code="200">The level as stored.</response>
    /// <response code="403">The caller may not change this property.</response>
    /// <response code="404">No property in the caller's org, or <c>property_floor_plan_not_found</c>.</response>
    /// <response code="422">The rules of the name, the floor and the area, as for an upload.</response>
    [HttpPut("floor-plans/{planId:guid}")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(PublicFloorPlanDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PublicFloorPlanDto>> UpdateFloorPlan(
        Guid id,
        Guid planId,
        [FromBody] FloorPlanRequest request,
        CancellationToken cancellationToken)
    {
        var (_, denied) = await AuthorizeAsync(id, PropertyOperations.Write, "PropertySiteProfile.FloorPlan");
        if (denied is not null)
            return denied;

        var plan = await floorPlans.UpdateAsync(id, planId, request.ToInput(), cancellationToken);
        return plan is null ? NotFound() : Ok(plan);
    }

    /// <summary>Removes a level and its image.</summary>
    /// <response code="204">Removed.</response>
    /// <response code="403">The caller may not change this property.</response>
    /// <response code="404">No property in the caller's org, or <c>property_floor_plan_not_found</c>.</response>
    [HttpDelete("floor-plans/{planId:guid}")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteFloorPlan(Guid id, Guid planId, CancellationToken cancellationToken)
    {
        var (_, denied) = await AuthorizeAsync(id, PropertyOperations.Write, "PropertySiteProfile.FloorPlan");
        if (denied is not null)
            return denied;

        return await floorPlans.DeleteAsync(id, planId, cancellationToken) ? NoContent() : NotFound();
    }

    /// <summary>The points of interest around the house, in the order the host gave (distance rounded, side of the compass: never a position).</summary>
    /// <response code="200">The points.</response>
    /// <response code="403">The caller may not read this property.</response>
    /// <response code="404">No property with this id in the caller's org.</response>
    [HttpGet("pois")]
    [ProducesResponseType(typeof(IReadOnlyList<PublicPoiDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<PublicPoiDto>>> GetPois(Guid id, CancellationToken cancellationToken)
    {
        var (_, denied) = await AuthorizeAsync(id, PropertyOperations.Read);
        if (denied is not null)
            return denied;

        var list = await pois.ListAsync(id, cancellationToken);
        return list is null ? NotFound() : Ok(list);
    }

    /// <summary>
    /// Replaces the points of interest (at most twelve; an empty list removes them all): each with a name (Italian required), a kind, how it is
    /// travelled, the minutes, the side of the compass and a distance. The distance is stored rounded (100 m below 1 km, 250 m below 5 km, 500 m
    /// below 20 km, then 1 km) and never closer than 200 m, so a known place does not give the door of the house away. Answers with what is stored.
    /// </summary>
    /// <response code="200">The points as stored.</response>
    /// <response code="400"><c>validation_error</c>: a name is absurdly long, or a kind, mode or side is not one of the lists.</response>
    /// <response code="403">The caller may not change this property.</response>
    /// <response code="404">No property with this id in the caller's org.</response>
    /// <response code="422"><c>property_poi_limit_reached</c>, <c>property_poi_minutes_invalid</c>, or <c>property_site_profile_italian_required</c> / <c>_text_too_long</c> with <c>field: pois</c>. Nothing is saved.</response>
    [HttpPut("pois")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(IReadOnlyList<PublicPoiDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<IReadOnlyList<PublicPoiDto>>> PutPois(Guid id, [FromBody] PoiListRequest request, CancellationToken cancellationToken)
    {
        var (_, denied) = await AuthorizeAsync(id, PropertyOperations.Write, "PropertySiteProfile.Pois");
        if (denied is not null)
            return denied;

        var list = await pois.ReplaceAsync(id, request.ToInput(), cancellationToken);
        return list is null ? NotFound() : Ok(list);
    }

    /// <summary>
    /// TN-3 check, like the photo endpoints: the property of the caller's org (the tenant filter hides another org's one, so 404), the
    /// host permission for the row (403), and the audit of an org-wide role acting on a colleague's property.
    /// </summary>
    private async Task<(Property? Property, ActionResult? Denied)> AuthorizeAsync(
        Guid id,
        HostOperationRequirement operation,
        string? auditAction = null)
    {
        var userId = User.FindFirst("sub")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
        if (string.IsNullOrEmpty(userId))
            return (null, Unauthorized());

        var property = await properties.GetPropertyRecordAsync(id);
        if (property is null)
            return (null, NotFound());

        if (!await hostAuthorization.IsAuthorizedAsync(User, HostResource.ForProperty(property), operation))
        {
            logger.LogWarning(
                "User {UserId} denied {Permission} on the site profile of property {PropertyId}",
                userId, operation.PermissionKey, id);
            return (null, Forbid());
        }

        if (auditAction is not null && userId != property.OwnerId && IsOrgWide())
            await adminAccessAuditService.LogPrivilegedPropertyAccessAsync(userId, id, property.OwnerId, auditAction);

        return (property, null);
    }

    private bool IsOrgWide()
    {
        var roles = Auth0RolesClaimParser.Parse(User.FindAll("https://casazen.app/roles").Select(c => c.Value));
        if (roles.Count == 0)
            roles = [.. User.FindAll(ClaimTypes.Role).Select(c => c.Value)];

        return roles.Any(HostRoles.OrgWide.Contains);
    }
}
