using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.TouristTax;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The tourist tax of a stay guest by guest and the rate an org declares for a comune with no official one (NF-46, ADR-037; the web wizard
/// E03/F01/US04/T02 calls these). TN-3, the pattern of <see cref="BookingLifecycleController"/>: the tenant filter hides a booking or a property
/// of another org (404), and a row the caller may not use on its property is 404 as well. Domain errors are ProblemDetails with the codes of
/// <c>StayTouristTaxService</c> (<c>tax_not_calculable</c>, <c>tax_exemption_unknown</c>, <c>tax_guest_unknown</c>,
/// <c>tax_collector_not_applicable</c>, <c>tax_rate_official_exists</c>, <c>tax_rate_source_required</c>, <c>tax_rate_invalid</c>).
/// </summary>
[ApiController]
[Route("api")]
public class StayTouristTaxController(
    IStayTouristTaxService stayTouristTax,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    ILogger<StayTouristTaxController> logger) : ControllerBase
{
    /// <summary>The tax of the stay per guest, the collector and what the host must collect; <c>missingRate</c> when no rate is known.</summary>
    [HttpGet("bookings/{bookingId:guid}/tourist-tax")]
    [Authorize(Policy = CasazenPolicies.BookingRead)]
    [ProducesResponseType(typeof(StayTouristTaxDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StayTouristTaxDto>> Get(Guid bookingId, CancellationToken cancellationToken)
    {
        if (!await CanUseBookingAsync(bookingId, BookingOperations.Read, cancellationToken))
            return BookingNotFound();

        return Ok(StayTouristTaxDto.From(await stayTouristTax.GetAsync(bookingId, cancellationToken)));
    }

    /// <summary>The exemptions the host chooses for the guests (a null code clears one); 422 for an unknown reason or position.</summary>
    [HttpPut("bookings/{bookingId:guid}/tourist-tax/exemptions")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(StayTouristTaxDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<StayTouristTaxDto>> SetExemptions(
        Guid bookingId,
        [FromBody] SetTouristTaxExemptionsRequest request,
        CancellationToken cancellationToken)
    {
        if (!await CanUseBookingAsync(bookingId, BookingOperations.Write, cancellationToken))
            return BookingNotFound();
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        var view = await stayTouristTax.SetExemptionsAsync(
            bookingId,
            request.Exemptions.Select(e => new GuestExemption(e.Position, e.Code)).ToList(),
            userId,
            cancellationToken);
        return Ok(StayTouristTaxDto.From(view));
    }

    /// <summary>Who collected the tax of a channel stay whose comune has no channel rule; 422 <c>tax_collector_not_applicable</c> otherwise.</summary>
    [HttpPut("bookings/{bookingId:guid}/tourist-tax/collector")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(StayTouristTaxDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<StayTouristTaxDto>> SetCollector(
        Guid bookingId,
        [FromBody] SetTouristTaxCollectorRequest request,
        CancellationToken cancellationToken)
    {
        if (!await CanUseBookingAsync(bookingId, BookingOperations.Write, cancellationToken))
            return BookingNotFound();
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        return Ok(StayTouristTaxDto.From(await stayTouristTax.SetCollectorAsync(bookingId, request.Collector, userId, cancellationToken)));
    }

    /// <summary>Confirms the tax of the stay (the record kept 10 years); 422 <c>tax_not_calculable</c> while no amount is known.</summary>
    [HttpPost("bookings/{bookingId:guid}/tourist-tax/confirm")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(StayTouristTaxDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<StayTouristTaxDto>> Confirm(Guid bookingId, CancellationToken cancellationToken)
    {
        if (!await CanUseBookingAsync(bookingId, BookingOperations.Write, cancellationToken))
            return BookingNotFound();
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        return Ok(StayTouristTaxDto.From(await stayTouristTax.ConfirmAsync(bookingId, userId, cancellationToken)));
    }

    /// <summary>The comune of the property for its tourist tax and the rate the org declared for it.</summary>
    [HttpGet("properties/{propertyId:guid}/tourist-tax-rate")]
    [Authorize(Policy = CasazenPolicies.PropertyRead)]
    [ProducesResponseType(typeof(MunicipalityRateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MunicipalityRateDto>> GetMunicipalityRate(Guid propertyId, CancellationToken cancellationToken)
    {
        if (!await CanUsePropertyAsync(propertyId, PropertyOperations.Read, cancellationToken))
            return PropertyNotFound();

        return Ok(MunicipalityRateDto.From(await stayTouristTax.GetMunicipalityRateAsync(propertyId, cancellationToken)));
    }

    /// <summary>
    /// Declares the org's rate for the comune of the property, private to the org: 409 <c>tax_rate_official_exists</c> where an official rate is
    /// in force, 422 <c>tax_rate_source_required</c> without a source, 422 <c>tax_rate_invalid</c> for values out of range.
    /// </summary>
    [HttpPut("properties/{propertyId:guid}/tourist-tax-rate")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(MunicipalityRateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MunicipalityRateDto>> SaveMunicipalityRate(
        Guid propertyId,
        [FromBody] SaveMunicipalityRateRequest request,
        CancellationToken cancellationToken)
    {
        if (!await CanUsePropertyAsync(propertyId, PropertyOperations.Write, cancellationToken))
            return PropertyNotFound();
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        var view = await stayTouristTax.SaveMunicipalityRateAsync(propertyId, request.ToInput(), userId, cancellationToken);
        return Ok(MunicipalityRateDto.From(view));
    }

    /// <summary>TN-3: the booking is visible (tenant filter) and the caller has <paramref name="operation"/> on its property.</summary>
    private async Task<bool> CanUseBookingAsync(Guid bookingId, HostOperationRequirement operation, CancellationToken cancellationToken)
    {
        var resource = await hostResources.ForBookingAsync(bookingId, cancellationToken);
        if (resource is not null && await authorizationService.IsAuthorizedAsync(User, resource, operation))
            return true;

        logger.LogWarning(
            "User {UserId} denied {Permission} on the tourist tax of booking {BookingId}", User.GetUserId(), operation.PermissionKey, bookingId);
        return false;
    }

    /// <summary>TN-3: the property is visible (tenant filter) and the caller has <paramref name="operation"/> on it.</summary>
    private async Task<bool> CanUsePropertyAsync(Guid propertyId, HostOperationRequirement operation, CancellationToken cancellationToken)
    {
        var resource = await hostResources.ForPropertyAsync(propertyId, cancellationToken);
        if (resource is not null && await authorizationService.IsAuthorizedAsync(User, resource, operation))
            return true;

        logger.LogWarning(
            "User {UserId} denied {Permission} on the tourist tax rate of property {PropertyId}", User.GetUserId(), operation.PermissionKey, propertyId);
        return false;
    }

    private ObjectResult BookingNotFound() =>
        this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "BookingNotFound");

    private ObjectResult PropertyNotFound() =>
        this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "PropertyNotFound");
}
