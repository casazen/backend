using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The supplier's own settings (SP-16a): the three notification switches, the automatic acceptance of regular hosts and the time a customer
/// of the public showcase waits for an answer; and, <b>read-only</b>, whether the showcase takes bookings. Every route is scoped to the
/// caller's own supplier org (its supplier link, never a value of the request, and never provisioned by a settings call). Not behind a
/// feature flag. Runbook <c>docs/runbooks/suppliers.md</c> section 29.
/// </summary>
/// <remarks>
/// Errors: 404 <c>not_found</c> when the caller has no linked supplier org; 400 <c>validation_error</c> for a malformed body; 422
/// <c>supplier_settings_invalid</c> (with <c>fields</c>: the names of the fields at fault).
/// </remarks>
[ApiController]
[Route("api/supplier/settings")]
[Authorize(Policy = CasazenPolicies.Supplier)]
public class SupplierSettingsController(
    ISupplierAgendaService agenda,
    ISupplierOrgContextResolver supplierOrgContextResolver) : ControllerBase
{
    /// <summary>
    /// The settings; the defaults while the supplier never saved any (a read never creates the row). <c>onlineBookingEnabled</c> says
    /// whether the showcase takes bookings: the showcase editor is the only place that changes it.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(SupplierSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierSettingsDto>> Get(CancellationToken cancellationToken) =>
        await RunAsync(
            async orgId => Ok(SupplierSettingsMapper.ToDto(await agenda.GetSettingsAsync(orgId, cancellationToken))),
            cancellationToken);

    /// <summary>
    /// Replaces the five settings (all of them are needed): <c>autoAcceptRegulars</c>, <c>notifyNewRequests</c>,
    /// <c>notifyDayBeforeReminder</c>, <c>notifyPayments</c> and <c>respondWithinMinutes</c> (15 to 10080). It <b>never</b> changes
    /// <c>onlineBookingEnabled</c>: a value sent for it is ignored. 422 <c>supplier_settings_invalid</c> (with <c>fields</c>) for a setting
    /// that is missing or outside its limits.
    /// </summary>
    [HttpPut]
    [ProducesResponseType(typeof(SupplierSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SupplierSettingsDto>> Replace(
        [FromBody] SaveSupplierSettingsRequest request,
        CancellationToken cancellationToken) =>
        await RunAsync(
            async orgId => Ok(SupplierSettingsMapper.ToDto(
                await agenda.ReplaceSettingsAsync(orgId, SupplierSettingsMapper.ToInput(request), cancellationToken))),
            cancellationToken);

    /// <summary>
    /// Runs <paramref name="action"/> for the caller's linked supplier org (only an already linked one: a settings call never provisions
    /// or links a supplier org). A setting that is not valid is answered with the names of its fields (<c>fields</c>).
    /// </summary>
    private async Task<ActionResult<SupplierSettingsDto>> RunAsync(
        Func<Guid, Task<ActionResult<SupplierSettingsDto>>> action,
        CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        try
        {
            return await action(orgId.Value);
        }
        catch (SupplierAgendaRuleException ex)
        {
            var problem = ApiProblemDetails.Create(
                HttpContext, StatusCodes.Status422UnprocessableEntity, ex.Code, ex.MessageKey, ex.MessageArgs);
            problem.Extensions["fields"] = ex.Fields;
            return new ObjectResult(problem)
            {
                StatusCode = StatusCodes.Status422UnprocessableEntity,
                ContentTypes = { ApiProblemDetails.ContentType },
            };
        }
    }
}
