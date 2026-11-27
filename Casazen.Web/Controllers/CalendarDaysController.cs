using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The price and the rules of every night of the host's properties in one read (E08/F05/US01/T02, NF-36, NF-78): for each property the caller
/// reaches and each day of the range, the price of the night with where it comes from, the stay and advance rules in force, whether the night is
/// taken, the time zone of the property and whether it books into the channel manager. Limited to the caller's org and, unless the role is org-wide,
/// to the properties they reach (<see cref="HostScope"/>, TN-3), filtered in SQL. The host writes the rules through the
/// changes API, not here.
/// </summary>
[ApiController]
[Route("api/calendar/days")]
[Authorize(Policy = CasazenPolicies.PropertyRead)]
public class CalendarDaysController(
    IPropertyDayRuleService dayRules,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    TimeProvider timeProvider) : ControllerBase
{
    /// <summary>
    /// The nights of the properties the caller reaches from <c>from</c> to <c>to</c> (<c>yyyy-MM-dd</c>, both included; today and the 29 days
    /// after it when missing; at most 92 days: 422 <c>calendar_window_too_large</c>; 400 <c>booking_calendar_range_invalid</c> when <c>to</c> is
    /// before <c>from</c>). <c>propertyIds</c> (repeated or separated by commas, at most 50: 422 <c>calendar_too_many_properties</c>) narrows it to
    /// those properties; one the caller does not reach is left out, as if it did not exist; without it the first 50 by name. The answer is
    /// <c>{ today, properties: [{ propertyId, timezone, currency, mirrored, days: [{ date, priceCents, priceSource, minNights, maxNights,
    /// closedToArrival, closedToDeparture, minAdvanceDays, maxAdvanceDays, hasRule, taken }] }] }</c>.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(CalendarDaysDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<CalendarDaysDto>> GetDays(
        [FromQuery(Name = "from")] DateOnly? fromDay = null,
        [FromQuery(Name = "to")] DateOnly? toDay = null,
        [FromQuery] string[]? propertyIds = null,
        CancellationToken cancellationToken = default)
    {
        // The days the calendar can write, and the ids: the same bounds and the same list as the calendar of the host.
        if (!CalendarController.InBounds(fromDay)
            || !CalendarController.InBounds(toDay)
            || !CalendarController.TryParseIds(propertyIds, out var ids))
        {
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.BadRequest, "CalendarInvalidRequest");
        }

        var from = fromDay ?? timeProvider.TodayInRomeAsDateOnly();
        var to = toDay ?? from.AddDays(HostCalendarLimits.TodoAlertDays - 1);
        if (to < from)
            return this.ApiProblem(StatusCodes.Status400BadRequest, BookingErrorCodes.CalendarRangeInvalid, "BookingCalendarRangeInvalid");

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null
            || await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken) is not { } scope)
        {
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");
        }

        var days = await dayRules.GetDaysAsync(scope, new CalendarDaysQuery(from, to, ids), cancellationToken);
        return Ok(CalendarDaysDto.Of(days));
    }
}
