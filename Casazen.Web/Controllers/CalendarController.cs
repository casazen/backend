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
/// The calendar of all the host's properties in one read (SR-02a): stays, portal reservations, blocks and interventions of every
/// property the caller reaches, in the range of days asked for. Limited to the caller's org and, unless the role is org-wide, to the
/// properties they reach (<see cref="HostScope"/>, TN-3), filtered in SQL. <c>GET /api/bookings/calendar</c>, the calendar of one
/// property, is unchanged.
/// </summary>
[ApiController]
[Route("api/calendar")]
[Authorize(Policy = CasazenPolicies.BookingRead)]
public class CalendarController(
    IHostCalendarService calendarService,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    TimeProvider timeProvider) : ControllerBase
{
    /// <summary>First and last year a day may be asked for.</summary>
    private const int MinYear = 2000;
    private const int MaxYear = 2100;

    /// <summary>
    /// The calendar of the properties the caller reaches from <c>from</c> to <c>to</c> (<c>yyyy-MM-dd</c>, both included; today and the
    /// 29 days after it when missing; at most 92 days: 422 <c>calendar_window_too_large</c>; 400 <c>booking_calendar_range_invalid</c>
    /// when <c>to</c> is before <c>from</c>). <c>propertyIds</c> (repeated or separated by commas, at most 50: 422
    /// <c>calendar_too_many_properties</c>) narrows it to those properties; one the caller does not reach is left out, as if it did not
    /// exist; without it the first 50 by name, <c>propertiesTotal</c> saying how many there are. <c>timezone</c> (an IANA id, Europe/Rome
    /// by default) only sets <c>utcOffsetMinutes</c>: every day is a calendar date and never moves.
    /// <para>
    /// <c>properties</c> lists the short-rent properties; <c>items</c> everything that has a day in the range, from its arrival day to
    /// its departure day (<see cref="HostCalendarItemDto"/>): <c>Booking</c> (a stay, with its channel, how much of its total came in and,
    /// for a "pay at the property" request, until when the host may answer), <c>Ical</c> (a portal's reservation as it arrives, a block
    /// of its calendar), <c>Block</c> (the host's own) and <c>Service</c> (an intervention, on the day of its time or of the departure of
    /// its stay). One read for each kind whatever the number of properties.
    /// </para>
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(HostCalendarDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<HostCalendarDto>> GetCalendar(
        [FromQuery(Name = "from")] DateOnly? fromDay = null,
        [FromQuery(Name = "to")] DateOnly? toDay = null,
        [FromQuery] string[]? propertyIds = null,
        [FromQuery] string? timezone = null,
        CancellationToken cancellationToken = default)
    {
        // Days the calendar can write: a day outside them is no calendar, and the day after it may not exist.
        if (!InBounds(fromDay) || !InBounds(toDay) || !TryParseIds(propertyIds, out var ids))
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.BadRequest, "CalendarInvalidRequest");

        var from = fromDay ?? timeProvider.TodayInRomeAsDateOnly();
        var to = toDay ?? from.AddDays(HostCalendarLimits.TodoAlertDays - 1);
        if (to < from)
            return this.ApiProblem(StatusCodes.Status400BadRequest, BookingErrorCodes.CalendarRangeInvalid, "BookingCalendarRangeInvalid");

        var zone = string.IsNullOrWhiteSpace(timezone) ? RomeCalendar.TimeZoneId : timezone.Trim();
        if (!TimezoneHelper.IsValidTimezone(zone))
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.BadRequest, "PropertyTimezoneInvalid");

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null
            || await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var calendar = await calendarService.GetCalendarAsync(scope, new HostCalendarQuery(from, to, ids), cancellationToken);
        return Ok(HostCalendarDto.Of(
            calendar, from, to, zone, TimezoneHelper.GetUtcOffsetMinutes(zone, timeProvider.GetUtcNow().UtcDateTime)));
    }

    private static bool InBounds(DateOnly? day) => day is null || day.Value.Year is >= MinYear and <= MaxYear;

    // The ids of the request, repeated or separated by commas; false for anything that is not an id.
    private static bool TryParseIds(string[]? values, out List<Guid> ids)
    {
        ids = [];
        foreach (var token in (values ?? []).SelectMany(
                     v => (v ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)))
        {
            if (!Guid.TryParse(token, out var id))
                return false;

            ids.Add(id);
        }

        return true;
    }
}
