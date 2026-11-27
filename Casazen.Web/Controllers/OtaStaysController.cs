using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Casazen.Web.Controllers;

/// <summary>
/// OTA stays created by the host from an iCal block (CO-21, decision D7, GC-AC9): an iCal feed carries dates only, the
/// host gives the guest's name and email and the block becomes a confirmed stay with the OTA source of its feed. From it
/// the check-in link (<c>/api/bookings/{id}/checkin/*</c>), Alloggiati Web, the cockpit, the arrival and the check-out
/// work as for any stay. A later sync never changes the stay: it marks it "da verificare" and the host clears the mark
/// here. Rules: <see cref="OtaStays"/>; runbook docs/runbooks/ical.md.
/// </summary>
/// <remarks>
/// TN-3: a block or booking of another org is invisible (404); on a visible one the caller needs <c>booking.write</c> on
/// its property (owner or org-wide role), otherwise 403. Errors are ProblemDetails with the codes of
/// <see cref="OtaStayErrorCodes"/> and <see cref="BookingErrorCodes"/> (FD-05).
/// </remarks>
[ApiController]
[Route("api")]
[Authorize(Policy = CasazenPolicies.BookingRead)]
public class OtaStaysController(
    IOtaStayService otaStays,
    IBookingService bookingService,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    ILogger<OtaStaysController> logger) : ControllerBase
{
    [HttpPost("ical-blocks/{blockId:guid}/ota-stay")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(BookingResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BookingResponseDto>> CreateFromBlock(
        Guid blockId,
        [FromBody] CreateOtaStayRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var block = await otaStays.FindBlockAsync(blockId, cancellationToken);
        var property = block is null ? null : await hostResources.ForPropertyAsync(block.PropertyId, cancellationToken);
        if (block is null || property is null)
        {
            return this.ApiProblem(
                StatusCodes.Status404NotFound, OtaStayErrorCodes.BlockNotFound, OtaStayErrorCodes.BlockNotFoundMessageKey);
        }

        if (!await authorizationService.IsAuthorizedAsync(User, property, BookingOperations.Write))
        {
            logger.LogWarning(
                "User {UserId} denied booking.write to create an OTA stay from block {BlockId}", User.GetUserId(), blockId);
            return Forbid();
        }

        var stay = await otaStays.ConvertBlockAsync(
            new OtaStayConversion(
                blockId,
                request.FirstName,
                request.LastName,
                request.Email,
                request.NumberOfGuests,
                request.TotalPrice,
                request.Source),
            cancellationToken);

        logger.LogInformation("OTA stay {BookingId} created by user {UserId} from block {BlockId}", stay.Id, User.GetUserId(), blockId);
        var response = await ToDetailAsync(stay.Id, cancellationToken);
        return Created($"/api/bookings/{stay.Id}", response);
    }

    [HttpPost("bookings/{id:guid}/ota-review/resolve")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(BookingResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BookingResponseDto>> ResolveReview(
        Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ResolveOtaReviewRequest? request,
        CancellationToken cancellationToken)
    {
        var resource = await hostResources.ForBookingAsync(id, cancellationToken);
        if (resource is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "BookingNotFound");

        if (!await authorizationService.IsAuthorizedAsync(User, resource, BookingOperations.Write))
        {
            logger.LogWarning("User {UserId} denied booking.write to verify OTA stay {BookingId}", User.GetUserId(), id);
            return Forbid();
        }

        await otaStays.ResolveReviewAsync(id, request?.ApplyChannelDates ?? false, cancellationToken);
        return Ok(await ToDetailAsync(id, cancellationToken));
    }

    private async Task<BookingResponseDto> ToDetailAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var booking = await bookingService.GetBookingAsync(bookingId)
            ?? throw new InvalidOperationException($"Booking {bookingId} vanished after its change.");
        var response = BookingMapper.ToResponse(booking);
        var block = await otaStays.GetLinkedBlockAsync(bookingId, cancellationToken);
        response.ChannelBlock = block is null ? null : OtaChannelBlockDto.From(block);
        return response;
    }
}
