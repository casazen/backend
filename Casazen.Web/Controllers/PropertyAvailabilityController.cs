using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Core.Validation;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Whether a stay of one of the host's properties is free, said before the host enters a booking by hand (SR-06a). It reads what the
/// calendar, the iCal export and the final check on a new booking read (<see cref="IHostAvailabilityService"/>): there is no second
/// rule of occupancy.
/// </summary>
/// <remarks>
/// TN-3: the same permission as entering the booking, <c>booking.write</c>, on the property. A property of another org, or one a
/// collaborator limited to some properties does not reach, is 404, as for the host quote. Errors are ProblemDetails (FD-05).
/// </remarks>
[ApiController]
[Route("api/properties/{propertyId:guid}/availability")]
[Authorize(Policy = CasazenPolicies.BookingWrite)]
public class PropertyAvailabilityController(
    IHostAvailabilityService availability,
    IPropertyService propertyService,
    IAuthorizationService authorizationService,
    ILogger<PropertyAvailabilityController> logger) : ControllerBase
{
    /// <summary>First and last year a day may be asked for.</summary>
    private const int MinYear = 2000;
    private const int MaxYear = 2100;

    /// <summary>
    /// The state of the stay <c>from</c> (arrival) to <c>to</c> (departure, not a night), both <c>yyyy-MM-dd</c>:
    /// <c>ok</c>, <c>belowMinimum</c> (free, but shorter than the minimum stay: the host may go on), <c>occupied</c> (with the
    /// <c>conflicts</c> and the <c>nearestFreeWindow</c> of the same length, 40 days either way, never before today),
    /// <c>linkableBlock</c> (only a portal's reservation, not yet a stay, sits on exactly these dates: complete it with
    /// <c>blockId</c>) or <c>invalid</c> (with its <c>invalidReason</c>). <c>channel</c> (<c>Airbnb</c>, <c>BookingCom</c>,
    /// <c>Other</c>) narrows the block offered to the calendar of that portal. 400 without valid dates; 404 for a property the caller
    /// cannot book; 422 <c>property_not_bookable_in_long_mode</c>.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(HostAvailabilityDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<HostAvailabilityDto>> Get(
        Guid propertyId,
        [FromQuery(Name = "from")] DateOnly? fromDay = null,
        [FromQuery(Name = "to")] DateOnly? toDay = null,
        [FromQuery] string? channel = null,
        CancellationToken cancellationToken = default)
    {
        var property = await propertyService.GetPropertyAsync(propertyId);
        if (property is null
            || !await authorizationService.IsAuthorizedAsync(User, HostResource.ForProperty(property), BookingOperations.Write))
        {
            if (property is not null)
            {
                logger.LogWarning(
                    "User {UserId} denied booking.write on the availability of property {PropertyId}", User.GetUserId(), propertyId);
            }

            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "PropertyNotFound");
        }

        if (fromDay is not { } from || toDay is not { } to || !InBounds(from) || !InBounds(to) || !TryParseChannel(channel, out var portal))
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.BadRequest, "AvailabilityInvalidRequest");

        var result = await availability.CheckAsync(property, from, to, portal, cancellationToken);
        return Ok(HostAvailabilityDto.Of(propertyId, from, to, result));
    }

    private static bool InBounds(DateOnly day) => day.Year is >= MinYear and <= MaxYear;

    // No channel is a valid answer; a word that is not the name of a channel is not.
    private static bool TryParseChannel(string? value, out BookingChannel? channel)
    {
        channel = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        if (!EnumNames.TryParseDefined<BookingChannel>(value.Trim(), out var parsed))
            return false;

        channel = parsed;
        return true;
    }
}
