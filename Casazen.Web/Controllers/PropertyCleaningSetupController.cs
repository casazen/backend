using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Properties;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The default cleaner of a property at the check-out of its stays, with the price and the time agreed (E24/F01/US01/T01, NF-120, decision O11): a supplier of the marketplace
/// (preferably one of the org's trusted suppliers) or a member of the staff, set for one property or, in bulk, for many. It is what the routine action
/// <c>CreateCleaningRequest</c> reads to create the cleaning of a stay; a property with no active setup is not automated. No flag of its own: the automation runs only through
/// <c>Features:Routines</c>.
/// </summary>
/// <remarks>
/// <para>The policy is <see cref="CasazenPolicies.PropertyWrite"/> (<c>property.write</c> of the short-rent context) for the reads too: the setup holds a price. TN-3: the property is
/// authorized as a <see cref="HostResource"/> (org, permission, the property within the reach of the caller): another org's property answers 404, a property of the same org the caller
/// may not manage 403. The bulk save applies the reach of the caller in the query (<c>InScope</c>): a collaborator «Solo alcuni» saves only the properties it was given, and the others
/// come back as «not found» in the result, whatever the rest of the list is.</para>
/// <para>The answer is private and never cached. Every change is logged with the user and the property, never with a value.</para>
/// </remarks>
[ApiController]
[Route("api/properties")]
[Authorize(Policy = CasazenPolicies.PropertyWrite)]
[PrivateAnswer]
public sealed class PropertyCleaningSetupController(
    IPropertyCleaningSetupService setups,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IHostScopeResolver hostScopeResolver,
    IOrgContextResolver orgContextResolver,
    ILogger<PropertyCleaningSetupController> logger) : ControllerBase
{
    /// <summary>
    /// The default cleaner of a property: who cleans, the agreed price in the currency of the property, the time and the length, whether the automation uses it; a property that
    /// has none answers <c>configured: false</c> with the defaults (the time and the length that apply when the host sets none).
    /// </summary>
    /// <response code="200">The setup, or the defaults.</response>
    /// <response code="403">The caller may not manage this property.</response>
    /// <response code="404"><c>property_not_found</c>: no such property in the caller's org.</response>
    [HttpGet("{propertyId:guid}/cleaning-setup")]
    [ProducesResponseType(typeof(PropertyCleaningSetupDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PropertyCleaningSetupDto>> Get(Guid propertyId, CancellationToken cancellationToken)
    {
        var (resource, denied) = await AuthorizePropertyAsync(propertyId, PropertyOperations.Read, cancellationToken);
        if (denied is not null)
            return denied;

        return Ok(PropertyCleaningSetupDto.From(await setups.GetAsync(resource!.OrgId, propertyId, cancellationToken)));
    }

    /// <summary>
    /// Sets the default cleaner of a property: the body is the whole setup, and whatever it leaves out is cleared. An active setup needs a cleaner that can work now: a supplier
    /// with an active profile that covers the comune of the property (the checks of a service request), or a member of the staff who is active and reaches the property; a supplier
    /// needs a price from 1 cent, and every price carries its currency, which must be the one of the property. Answers with what is stored.
    /// </summary>
    /// <response code="200">The setup as stored.</response>
    /// <response code="403">The caller may not manage this property.</response>
    /// <response code="404"><c>property_not_found</c>.</response>
    /// <response code="422">
    /// One of <c>cleaning_setup_cleaner_required</c>, <c>_cleaner_mismatch</c>, <c>_price_invalid</c>, <c>_currency_required</c>, <c>_currency_mismatch</c>, <c>_start_time_invalid</c>,
    /// <c>_duration_invalid</c>, <c>_supplier_not_found</c>, <c>_supplier_inactive</c>, <c>_supplier_outside_comune</c>, <c>_staff_invalid</c>, or <c>property_not_bookable_in_long_mode</c>.
    /// Nothing is saved.
    /// </response>
    [HttpPut("{propertyId:guid}/cleaning-setup")]
    [ProducesResponseType(typeof(PropertyCleaningSetupDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PropertyCleaningSetupDto>> Put(
        Guid propertyId,
        [FromBody] SaveCleaningSetupRequest request,
        CancellationToken cancellationToken)
    {
        var (resource, denied) = await AuthorizePropertyAsync(propertyId, PropertyOperations.Write, cancellationToken);
        if (denied is not null)
            return denied;

        var view = await setups.SaveAsync(resource!.OrgId, propertyId, request.ToInput(), User.GetUserId() ?? string.Empty, cancellationToken);
        logger.LogInformation("User {UserId} set the cleaner of property {PropertyId}", User.GetUserId(), propertyId);
        return Ok(PropertyCleaningSetupDto.From(view));
    }

    /// <summary>
    /// Sets the same cleaner for many properties of the org (at most 200; a repeated id counts once). Each property is saved on its own, as <c>PUT api/properties/{id}/cleaning-setup</c>
    /// would: the answer is 200 with one result for each, in the order received, and one that cannot be saved (a supplier that does not cover its comune, a currency that is not its
    /// own) comes back with its <c>code</c> and localized <c>message</c> without stopping the others, never «all saved» when one was not. A property of another org, deleted, unknown
    /// or outside the reach of the caller is <c>property_not_found</c> for it. What is wrong with the body itself is 422 for the whole request, before anything is saved; no
    /// property at all is 422 <c>cleaning_setup_bulk_empty</c>, never «every property».
    /// </summary>
    /// <response code="200">One result for each property, with the counts.</response>
    /// <response code="404">The caller has no org or reaches no property.</response>
    /// <response code="422"><c>cleaning_setup_bulk_empty</c>, <c>_bulk_too_many</c>, or a code of the body (<c>_cleaner_required</c>, <c>_cleaner_mismatch</c>, <c>_price_invalid</c>, <c>_currency_required</c>, <c>_start_time_invalid</c>, <c>_duration_invalid</c>).</response>
    [HttpPut("cleaning-setup/bulk")]
    [ProducesResponseType(typeof(CleaningSetupBulkResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<CleaningSetupBulkResponse>> PutBulk(
        [FromBody] BulkSaveCleaningSetupRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return Unauthorized();

        // What the caller reaches, from its org membership: never widened (a caller with no scope reaches no property).
        if (await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status404NotFound, "property_not_found", "PropertyNotFound");

        var results = await setups.SaveBulkAsync(
            orgId.Value, scope, request.PropertyIds ?? [], request.ToInput(), userId, cancellationToken);

        logger.LogInformation(
            "User {UserId} set the cleaner of {Count} properties in bulk: {Saved} saved",
            userId, results.Count, results.Count(r => r.Saved));
        return Ok(new CleaningSetupBulkResponse
        {
            Results =
            [
                .. results.Select(result => new CleaningSetupBulkResultDto
                {
                    PropertyId = result.PropertyId,
                    Saved = result.Saved,
                    Code = result.Code,
                    Message = result.MessageKey is null || result.Code is null
                        ? null
                        : ApiProblemDetails.Create(
                            HttpContext, StatusCodes.Status422UnprocessableEntity, result.Code, result.MessageKey, result.MessageArgs).Detail,
                }),
            ],
            Saved = results.Count(r => r.Saved),
            Failed = results.Count(r => !r.Saved),
        });
    }

    private async Task<(HostResource? Resource, ActionResult? Denied)> AuthorizePropertyAsync(
        Guid propertyId,
        HostOperationRequirement operation,
        CancellationToken cancellationToken)
    {
        // Tenant filter: another org's property is not found.
        var resource = await hostResources.ForPropertyAsync(propertyId, cancellationToken);
        if (resource is null)
            return (null, this.ApiProblem(StatusCodes.Status404NotFound, "property_not_found", "PropertyNotFound"));

        if (!await authorizationService.IsAuthorizedAsync(User, resource, operation))
        {
            logger.LogWarning(
                "User {UserId} denied {Permission} on the cleaner of property {PropertyId}",
                User.GetUserId(), operation.PermissionKey, propertyId);
            return (resource, Forbid());
        }

        return (resource, null);
    }
}
