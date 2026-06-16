using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/public/bookings")]
[AllowAnonymous]
public class PublicBookingsController(
    IBookingService bookingService,
    ILogger<PublicBookingsController> logger) : ControllerBase
{
    [HttpGet("property/{propertyId}/availability")]
    public async Task<ActionResult<PropertyAvailabilityResponse>> GetPropertyAvailability(
        Guid propertyId,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null)
    {
        var start = startDate ?? DateTime.UtcNow.Date;
        var end = endDate ?? DateTime.UtcNow.Date.AddDays(365);

        var bookings = await bookingService.GetCalendarAsync(propertyId, start, end);

        var bookedDates = bookings
            .Where(b => b.Status != BookingStatus.Cancelled)
            .SelectMany(b => GetDateRange(b.CheckInDate, b.CheckOutDate))
            .ToHashSet();

        return Ok(new PropertyAvailabilityResponse
        {
            PropertyId = propertyId,
            StartDate = start,
            EndDate = end,
            BookedDates = bookedDates.OrderBy(d => d).ToList(),
        });
    }

    private static IEnumerable<string> GetDateRange(DateTime checkIn, DateTime checkOut)
    {
        var current = checkIn.Date;
        while (current < checkOut.Date)
        {
            yield return current.ToString("yyyy-MM-dd");
            current = current.AddDays(1);
        }
    }

    [HttpPost]
    [EnableRateLimiting("PublicBookingCreate")]
    public async Task<ActionResult<DirectBookingResponse>> CreateDirectBooking(
        [FromBody] CreateDirectBookingRequest request)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        if (request.Consent is null || !request.Consent.DataProcessing)
        {
            return BadRequest(new
            {
                error = "Consent required",
                message = "Data processing consent is required to complete booking.",
            });
        }

        var consentIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
        var guest = request.Guest;

        try
        {
            var result = await bookingService.CreateDirectBookingAsync(new DirectBookingCreateInput(
                request.PropertyId,
                request.CheckInDate,
                request.CheckOutDate,
                request.NumberOfAdults,
                request.NumberOfChildren,
                new DirectBookingGuestInput(
                    guest.FirstName,
                    guest.LastName,
                    guest.Email,
                    guest.Phone,
                    guest.Country),
                request.Consent.ConsentVersion,
                consentIp,
                request.SpecialRequests));

            return Ok(new DirectBookingResponse
            {
                BookingId = result.BookingId,
                ClientSecret = result.ClientSecret,
                ConnectedAccountPublishableContext = new ConnectedAccountPublishableContext
                {
                    PublishableKey = result.PublishableKey,
                    StripeAccountId = result.StripeAccountId,
                },
                Amount = result.Amount,
                Currency = result.Currency,
                TouristTaxAmount = result.TouristTaxAmount,
                BasePrice = result.BasePrice,
            });
        }
        catch (DirectBookingException ex)
        {
            logger.LogWarning(ex, "Direct booking rejected: {ErrorCode}", ex.ErrorCode);
            return ex.ErrorCode switch
            {
                DirectBookingErrorCodes.PropertyNotFound => NotFound(new { error = "Property not found" }),
                DirectBookingErrorCodes.PaymentNotReady => Conflict(new
                {
                    error = "Complete Stripe onboarding before accepting guest payments",
                }),
                DirectBookingErrorCodes.NotAvailable => Conflict(new
                {
                    error = "Property not available for selected dates",
                }),
                DirectBookingErrorCodes.TooManyGuests or DirectBookingErrorCodes.InvalidDates
                    or DirectBookingErrorCodes.InvalidConsentVersion => BadRequest(new
                    {
                        error = ex.ErrorCode,
                        message = ex.Message,
                    }),
                DirectBookingErrorCodes.StripeError => StatusCode(500, new
                {
                    error = "Payment initialization failed",
                }),
                _ => BadRequest(new { error = ex.Message }),
            };
        }
    }
}
