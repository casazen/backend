using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The steps of the stays of the caller's own organization (casazen/backend#978): the online check-in link, the arrival, the departure and the property ready for
/// the next guest, each done by CasaZen (<c>Automatic</c>, the default) or by the host (<c>Manual</c>), with the parameter a step needs. Whoever reads the bookings
/// reads them (the booking page says what CasaZen will do); only the organization's administrators (<see cref="CasazenPolicies.OrgBillingAdmin"/>, like the rest of
/// the org's settings) change them. The route has no organization: it is the one of the caller's context.
/// </summary>
[ApiController]
[Route("api/settings/booking-flow")]
public sealed class BookingFlowSettingsController(
    IBookingFlowSettingsService settings,
    IOrgContextResolver orgContextResolver) : ControllerBase
{
    /// <summary>The settings in force: the row of the organization, or the defaults (<c>isDefault</c>, every step automatic) while it has none.</summary>
    /// <response code="200">The settings.</response>
    [HttpGet]
    [Authorize(Policy = CasazenPolicies.BookingRead)]
    [ProducesResponseType(typeof(BookingFlowSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingFlowSettingsDto>> Get(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

        return Ok(BookingFlowSettingsDto.From(await settings.GetAsync(orgId.Value, cancellationToken)));
    }

    /// <summary>
    /// Saves the settings: a member left out keeps its value. 422 <c>booking_flow_days_out_of_range</c> (days not 0 to <c>maxCheckInLinkDaysBefore</c>) or
    /// <c>booking_flow_value_invalid</c>, and nothing is saved. A change writes a line in the activity log; the same values again write nothing.
    /// </summary>
    /// <response code="200">The settings in force after the change.</response>
    [HttpPut]
    [Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
    [ProducesResponseType(typeof(BookingFlowSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BookingFlowSettingsDto>> Put([FromBody] UpdateBookingFlowSettingsDto body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

        var view = await settings.UpdateAsync(orgId.Value, body.ToUpdate(), User.GetUserId(), cancellationToken);
        return Ok(BookingFlowSettingsDto.From(view));
    }
}
