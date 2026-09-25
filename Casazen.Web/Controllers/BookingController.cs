using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Alloggiati;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = CasazenPolicies.BookingRead)]
public class BookingsController(
    IBookingService bookingService,
    IAlloggiatiWebService alloggiatiWebService,
    IPropertyService propertyService,
    IPropertyAuthorizationService authorizationService,
    PropertyICalSyncService propertyICalSyncService,
    ILogger<BookingsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<BookingResponseDto>>> GetAll([FromQuery] Guid? propertyId = null, [FromQuery] Guid? guestId = null)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        IEnumerable<Booking> bookings;

        if (propertyId.HasValue)
        {
            if (!await authorizationService.CanAccessPropertyAsync(userId, propertyId.Value, GetUserRoles()))
                return NotFound();

            bookings = await bookingService.GetPropertyBookingsAsync(propertyId.Value);
        }
        else if (guestId.HasValue)
        {
            bookings = await bookingService.GetGuestBookingsAsync(guestId.Value);
            bookings = await FilterAccessibleBookingsAsync(bookings, userId);
        }
        else
        {
            bookings = await bookingService.GetAllBookingsAsync();
            bookings = await FilterAccessibleBookingsAsync(bookings, userId);
        }

        return Ok(bookings.Select(BookingMapper.ToResponse));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<BookingResponseDto>> GetById(Guid id)
    {
        var booking = await bookingService.GetBookingAsync(id);
        if (booking == null)
            return NotFound();

        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        if (!await authorizationService.CanAccessPropertyAsync(userId, booking.PropertyId, GetUserRoles()))
            return NotFound();

        return Ok(BookingMapper.ToResponse(booking));
    }

    /// <summary>
    /// Booking entered by the host (phone, walk-in, another channel): created <c>Confirmed</c> with source
    /// <c>Manual</c>, so it occupies its dates on the booking site and in the iCal export at once and is never
    /// cancelled by the expiry of abandoned checkout holds (PC-01, A2-01). Overlapping dates answer 409
    /// <c>booking_dates_unavailable</c>.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(BookingResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BookingResponseDto>> Create(
        [FromBody] CreateBookingRequest request,
        [FromServices] IAuthorizationService hostAuthorization)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        // TN-3: the tenant filter hides a property of another org (404); a visible one still needs booking.write on
        // it (owner or org-wide role), otherwise 403.
        var property = await propertyService.GetPropertyAsync(request.PropertyId);
        if (property == null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "PropertyNotFound");

        if (!await hostAuthorization.IsAuthorizedAsync(User, HostResource.ForProperty(property), BookingOperations.Write))
        {
            logger.LogWarning(
                "User {UserId} denied booking.write on property {PropertyId}",
                User.GetUserId(), request.PropertyId);
            return Forbid();
        }

        if (request.NumberOfGuests > property.MaxGuests)
        {
            return this.ApiProblem(
                StatusCodes.Status422UnprocessableEntity,
                BookingErrorCodes.TooManyGuests,
                "BookingTooManyGuests",
                property.MaxGuests);
        }

        // Same pricing as the change of a manual booking and the host quote (PC-07): nightly rate x nights + cleaning
        // fee, tourist tax of BK-03 with the minors and their ages when the rates of the comune depend on them. Unknown
        // rate: no tax, never an invented one.
        var adults = request.NumberOfGuests - request.NumberOfChildren;
        var price = await bookingService.PriceHostStayAsync(
            property,
            request.CheckInDate,
            request.CheckOutDate,
            adults,
            request.NumberOfChildren,
            request.ChildrenAges,
            HttpContext.RequestAborted);
        if (price.TouristTax.Status == TouristTaxQuoteStatus.ChildAgesRequired)
        {
            return this.ApiProblem(
                StatusCodes.Status422UnprocessableEntity,
                DirectBookingErrorCodes.ChildAgesRequired,
                "TouristTaxChildAgesRequired");
        }

        logger.LogInformation("Creating manual booking for property {PropertyId}", request.PropertyId);

        var booking = new Booking
        {
            PropertyId = request.PropertyId,
            OrgId = property.OrgId,
            CheckInDate = price.CheckInDate,
            CheckOutDate = price.CheckOutDate,
            NumberOfGuests = request.NumberOfGuests,
            NumberOfAdults = adults,
            NumberOfChildren = request.NumberOfChildren,
            SpecialRequests = request.SpecialRequests ?? string.Empty,
            BasePrice = price.BasePrice,
            CleaningFee = price.CleaningFee,
            TouristTax = price.TouristTax.AmountOrZero,
            TouristTaxAmount = price.TouristTax.AmountOrZero,
            TotalPrice = price.TotalPrice,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        // Status (Confirmed), source (Manual), guest snapshot and the overlap check (409) are the service's job.
        var created = await bookingService.CreateManualBookingAsync(booking, NewGuestSnapshot(property.OrgId, request.Guest));
        var loaded = await bookingService.GetBookingAsync(created.Id);
        var response = loaded is null ? BookingMapper.ToResponse(created) : BookingMapper.ToResponse(loaded);
        logger.LogInformation("Manual booking created: {BookingId}, tourist tax: {Tax} EUR", created.Id, created.TouristTax);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, response);
    }

    /// <summary>
    /// Host calendar of a property, read by the web console and the app (MO-06): its bookings and calendar blocks from
    /// <paramref name="startDate"/> to <paramref name="endDate"/>. The two ends are stay dates (<c>2026-09-01</c>,
    /// <c>2026-09-30</c>), both included, never converted to a time zone; an entry is returned when a day of it, from
    /// arrival to departure, falls in the range (<see cref="HostCalendarRange"/>). The dates of the entries are stay
    /// dates too. <paramref name="timezone"/> (default: the property's) only fills <c>timezone</c> and
    /// <c>utcOffsetMinutes</c>. Blocks are <c>ical-block</c> items with the channel of their feed: they have no guest and
    /// no booking detail.
    /// </summary>
    [HttpGet("calendar")]
    [ProducesResponseType(typeof(CalendarResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CalendarResponseDto>> GetCalendar(
        [FromQuery] Guid propertyId,
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate,
        [FromServices] IAuthorizationService hostAuthorization,
        [FromQuery] string? timezone = null)
    {
        // TN-3: the tenant filter hides a property of another org (404); a visible one still needs booking.read on it.
        var property = await propertyService.GetPropertyAsync(propertyId);
        if (property == null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "PropertyNotFound");

        if (!await hostAuthorization.IsAuthorizedAsync(User, HostResource.ForProperty(property), BookingOperations.Read))
        {
            logger.LogWarning(
                "User {UserId} denied booking.read on the calendar of property {PropertyId}",
                User.GetUserId(), propertyId);
            return Forbid();
        }

        if (endDate.Date < startDate.Date)
        {
            return this.ApiProblem(
                StatusCodes.Status400BadRequest, BookingErrorCodes.CalendarRangeInvalid, "BookingCalendarRangeInvalid");
        }

        var targetTimezone = timezone ?? property.Timezone;
        if (!TimezoneHelper.IsValidTimezone(targetTimezone))
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.BadRequest, "PropertyTimezoneInvalid");

        var bookings = await bookingService.GetCalendarAsync(propertyId, startDate, endDate);
        var blocks = await propertyICalSyncService.GetBlocksInRangeAsync(propertyId, startDate, endDate);

        var utcOffsetMinutes = TimezoneHelper.GetUtcOffsetMinutes(targetTimezone, DateTime.UtcNow);

        var calendarBookings = bookings
            .OrderBy(b => b.CheckInDate)
            .Select(b => new CalendarBookingDto
            {
                Id = b.Id,
                PropertyId = b.PropertyId,
                GuestId = b.GuestId,
                CheckInDate = HostCalendarRange.StayDate(b.CheckInDate),
                CheckOutDate = HostCalendarRange.StayDate(b.CheckOutDate),
                CheckInDateUtc = b.CheckInDate,
                CheckOutDateUtc = b.CheckOutDate,
                Status = b.Status.ToString(),
                Source = b.Source.ToString(),
                NumberOfGuests = b.NumberOfGuests,
                TotalPrice = b.TotalPrice,
                GuestName = b.Guest != null ? $"{b.Guest.FirstName} {b.Guest.LastName}".Trim() : ""
            }).ToList();

        var items = calendarBookings.Select(b => new CalendarItemDto
        {
            Type = "booking",
            Id = b.Id,
            PropertyId = b.PropertyId,
            StartDate = b.CheckInDate,
            EndDate = b.CheckOutDate,
            StartDateUtc = b.CheckInDateUtc,
            EndDateUtc = b.CheckOutDateUtc,
            Status = b.Status,
            Source = b.Source,
            NumberOfGuests = b.NumberOfGuests,
            TotalPrice = b.TotalPrice,
            GuestName = b.GuestName,
        }).ToList();

        items.AddRange(blocks.Select(block => new CalendarItemDto
        {
            Type = "ical-block",
            Id = block.Id,
            PropertyId = block.PropertyId,
            StartDate = HostCalendarRange.StayDate(block.StartUtc),
            EndDate = HostCalendarRange.StayDate(block.EndUtc),
            StartDateUtc = block.StartUtc,
            EndDateUtc = block.EndUtc,
            Summary = block.Summary,
            Channel = block.Channel?.ToString(),
            FeedLabel = block.FeedLabel,
        }));

        var response = new CalendarResponseDto
        {
            Timezone = targetTimezone,
            UtcOffsetMinutes = utcOffsetMinutes,
            Bookings = calendarBookings,
            Items = items.OrderBy(i => i.StartDate).ThenBy(i => i.Type).ToList(),
        };

        return Ok(response);
    }

    [HttpGet("{id}/alloggiati-status")]
    [Authorize(Policy = "RequireContext:short-rent:booking.read")]
    public async Task<IActionResult> GetAlloggiatiStatus(Guid id)
    {
        var booking = await bookingService.GetBookingAsync(id);
        if (booking == null)
            return NotFound();

        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        if (!await authorizationService.CanAccessPropertyAsync(userId, booking.PropertyId, GetUserRoles()))
            return NotFound();

        var status = await alloggiatiWebService.GetStatusAsync(id);
        return Ok(AlloggiatiStatusDto.From(status));
    }

    // One guest snapshot per host booking (#431), owned by the property's org (TN-1): never a lookup by
    // e-mail, so a booking can neither reuse nor reveal a guest of another org.
    private static Guest NewGuestSnapshot(Guid orgId, CreateBookingGuestRequest guestInfo) => new()
    {
        OrgId = orgId,
        FirstName = guestInfo.FirstName,
        LastName = guestInfo.LastName,
        Email = guestInfo.Email,
        PhoneNumber = guestInfo.Phone,
        Country = guestInfo.Country,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private string? GetUserId() =>
        User.FindFirst("sub")?.Value
        ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;

    private IReadOnlyList<string> GetUserRoles() =>
        User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();

    private async Task<IReadOnlyList<Booking>> FilterAccessibleBookingsAsync(IEnumerable<Booking> bookings, string userId)
    {
        var roles = GetUserRoles();
        var visible = new List<Booking>();

        foreach (var booking in bookings)
        {
            if (await authorizationService.CanAccessPropertyAsync(userId, booking.PropertyId, roles))
                visible.Add(booking);
        }

        return visible;
    }
}
