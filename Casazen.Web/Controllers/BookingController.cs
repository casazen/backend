using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Alloggiati;
using Casazen.Web.DTOs.Compliance;
using Casazen.Web.Infrastructure;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "PropertyOwner")]
[Authorize(Policy = "RequireContext:short-rent:booking.read")]
public class BookingsController(
    IBookingService bookingService,
    ITaxCalculationService taxCalculationService,
    IAlloggiatiWebService alloggiatiWebService,
    IPropertyService propertyService,
    IPropertyAuthorizationService authorizationService,
    PropertyICalSyncService propertyICalSyncService,
    IGuestService guestService,
    IBackgroundJobClient backgroundJobClient,
    IGuestCheckInService checkInService,
    IComplianceWizardService complianceWizardService,
    ICheckoutReminderScheduler checkoutReminderScheduler,
    IOptions<ComplianceOptions> complianceOptions,
    IConfiguration configuration,
    IEmailService emailService,
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

    [HttpPost]
    [Authorize(Policy = "RequireContext:short-rent:booking.write")]
    public async Task<ActionResult<BookingResponseDto>> Create([FromBody] CreateBookingRequest request)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        var property = await propertyService.GetPropertyAsync(request.PropertyId);
        if (property == null)
            return NotFound("Property not found");

        if (!authorizationService.CanAccess(userId, property.OwnerId, GetUserRoles()))
            return Forbid();

        if (request.NumberOfGuests > property.MaxGuests)
        {
            return BadRequest(new
            {
                error = "Too many guests",
                message = $"This property allows a maximum of {property.MaxGuests} guests."
            });
        }

        var checkIn = DateTime.SpecifyKind(request.CheckInDate.Date, DateTimeKind.Utc);
        var checkOut = DateTime.SpecifyKind(request.CheckOutDate.Date, DateTimeKind.Utc);
        if (checkOut <= checkIn)
            return BadRequest("Check-out date must be after check-in date");

        logger.LogInformation("Creating booking for property: {PropertyId}", request.PropertyId);

        var isAvailable = await bookingService.IsPropertyAvailableAsync(
            request.PropertyId, checkIn, checkOut);

        if (!isAvailable)
        {
            logger.LogWarning("Property not available: {PropertyId} from {CheckIn} to {CheckOut}",
                request.PropertyId, checkIn, checkOut);
            return BadRequest("Property not available for these dates");
        }

        var guest = await ResolveGuestAsync(request.Guest);
        var nights = (checkOut - checkIn).Days;
        var basePrice = property.NightlyRate * nights + property.CleaningFee;

        var booking = new Booking
        {
            PropertyId = request.PropertyId,
            OrgId = property.OrgId,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            NumberOfGuests = request.NumberOfGuests,
            SpecialRequests = request.SpecialRequests ?? string.Empty,
            Status = BookingStatus.Pending,
            Source = BookingSource.Direct,
            BasePrice = basePrice,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        booking.TouristTax = await taxCalculationService.CalculateTouristTaxAsync(
            booking.PropertyId, booking.CheckInDate, booking.CheckOutDate, booking.NumberOfGuests);
        booking.TotalPrice = booking.BasePrice + booking.TouristTax;

        try
        {
            var created = await bookingService.CreateBookingAsync(booking);
            var loaded = await bookingService.GetBookingAsync(created.Id);
            var response = loaded is null ? BookingMapper.ToResponse(created) : BookingMapper.ToResponse(loaded);
            logger.LogInformation("Booking created: {BookingId}, tourist tax: {Tax} EUR", created.Id, created.TouristTax);
            return CreatedAtAction(nameof(GetById), new { id = created.Id }, response);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Booking creation failed for property {PropertyId}", request.PropertyId);
            return BadRequest(ex.Message);
        }
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "RequireContext:short-rent:booking.write")]
    public async Task<IActionResult> Update(Guid id, [FromBody] Booking booking)
    {
        var existing = await bookingService.GetBookingAsync(id);
        if (existing == null)
            return NotFound();

        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        if (!await authorizationService.CanAccessPropertyAsync(userId, existing.PropertyId, GetUserRoles()))
            return NotFound();

        booking.Id = id;
        booking.PropertyId = existing.PropertyId;
        booking.OrgId = existing.OrgId;
        await bookingService.UpdateBookingAsync(booking);
        return NoContent();
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "RequireContext:short-rent:booking.write")]
    public async Task<IActionResult> Cancel(Guid id)
    {
        logger.LogInformation("Cancelling booking: {BookingId}", id);
        var existing = await bookingService.GetBookingAsync(id);
        if (existing == null)
            return NotFound();

        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        if (!await authorizationService.CanAccessPropertyAsync(userId, existing.PropertyId, GetUserRoles()))
            return NotFound();

        var checkoutReminderJobId = existing.CheckoutReminderJobId;
        await bookingService.CancelBookingAsync(id);
        checkoutReminderScheduler.CancelReminder(checkoutReminderJobId);
        logger.LogInformation("Booking cancelled: {BookingId}", id);
        return NoContent();
    }

    [HttpGet("calendar")]
    public async Task<ActionResult<CalendarResponseDto>> GetCalendar(
        [FromQuery] Guid propertyId,
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate,
        [FromQuery] string? timezone = null)
    {
        var property = await propertyService.GetPropertyAsync(propertyId);
        if (property == null)
            return NotFound("Property not found");

        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        if (!await authorizationService.CanAccessPropertyAsync(userId, propertyId, GetUserRoles()))
            return NotFound();

        var targetTimezone = timezone ?? property.Timezone;

        if (!TimezoneHelper.IsValidTimezone(targetTimezone))
            return BadRequest($"Invalid timezone: {targetTimezone}");

        var startDateUtc = TimezoneHelper.ConvertLocalToUtc(startDate, targetTimezone);
        var endDateUtc = TimezoneHelper.ConvertLocalToUtc(endDate, targetTimezone);

        var bookings = await bookingService.GetCalendarAsync(propertyId, startDateUtc, endDateUtc);
        var icalBlocks = await propertyICalSyncService.GetBlocksInRangeAsync(propertyId, startDateUtc, endDateUtc);

        var utcOffsetMinutes = TimezoneHelper.GetUtcOffsetMinutes(targetTimezone, DateTime.UtcNow);

        var calendarBookings = bookings.Select(b => new CalendarBookingDto
        {
            Id = b.Id,
            PropertyId = b.PropertyId,
            GuestId = b.GuestId,
            CheckInDate = TimezoneHelper.ConvertUtcToLocal(b.CheckInDate, targetTimezone),
            CheckOutDate = TimezoneHelper.ConvertUtcToLocal(b.CheckOutDate, targetTimezone),
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

        foreach (var block in icalBlocks)
        {
            items.Add(new CalendarItemDto
            {
                Type = "ical-block",
                Id = block.Id,
                PropertyId = block.PropertyId,
                StartDate = TimezoneHelper.ConvertUtcToLocal(block.StartUtc, targetTimezone),
                EndDate = TimezoneHelper.ConvertUtcToLocal(block.EndUtc, targetTimezone),
                StartDateUtc = block.StartUtc,
                EndDateUtc = block.EndUtc,
                Summary = block.Summary,
            });
        }

        var response = new CalendarResponseDto
        {
            Timezone = targetTimezone,
            UtcOffsetMinutes = utcOffsetMinutes,
            Bookings = calendarBookings,
            Items = items,
        };

        return Ok(response);
    }

    [HttpPost("{id}/check-in")]
    [Authorize(Policy = "RequireContext:short-rent:booking.write")]
    public async Task<IActionResult> CheckIn(Guid id)
    {
        var booking = await bookingService.GetBookingAsync(id);
        if (booking == null)
            return NotFound();

        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        if (!await authorizationService.CanAccessPropertyAsync(userId, booking.PropertyId, GetUserRoles()))
            return NotFound();

        var property = await propertyService.GetPropertyAsync(booking.PropertyId);
        if (property == null)
            return NotFound();

        if (booking.Status != BookingStatus.Confirmed)
        {
            return BadRequest(new
            {
                error = "Transizione di stato non valida",
                message = $"Il check-in è possibile solo da Confirmed. Stato attuale: {booking.Status}"
            });
        }

        if (booking.CheckInDate.Date > DateTime.UtcNow.Date)
        {
            return BadRequest(new
            {
                error = "Data di check-in non raggiunta",
                message = $"Impossibile fare check-in prima del {booking.CheckInDate:yyyy-MM-dd}"
            });
        }

        booking.Status = BookingStatus.CheckedIn;
        await bookingService.UpdateBookingAsync(booking);

        // Mandatory guest registration with police database within 24h (D.L. 286/1998, Art. 7)
        backgroundJobClient.Enqueue<AlloggiatiWebReportJob>(
            job => job.ReportGuestAsync(booking.GuestId, booking.Id));

        var localCheckoutDate = TimezoneHelper.ConvertUtcToLocal(booking.CheckOutDate, property.Timezone);
        var reminderAtLocal = localCheckoutDate.Date
            .AddHours(complianceOptions.Value.CheckoutReminderHourLocal);
        var reminderAt = TimezoneHelper.ConvertLocalToUtc(reminderAtLocal, property.Timezone);
        if (reminderAt <= DateTime.UtcNow)
            reminderAt = DateTime.UtcNow.AddMinutes(5);

        booking.CheckoutReminderJobId = checkoutReminderScheduler.ScheduleReminder(booking.Id, reminderAt);
        await bookingService.UpdateBookingAsync(booking);

        logger.LogInformation("Check-in completed for booking {BookingId}, queued Alloggiati Web report", id);
        var updated = await bookingService.GetBookingAsync(id);
        return Ok(updated is null ? BookingMapper.ToResponse(booking) : BookingMapper.ToResponse(updated));
    }

    [HttpPost("{id}/checkout-wizard/start")]
    [Authorize(Policy = "RequireContext:short-rent:booking.write")]
    [ProducesResponseType(typeof(CheckoutWizardDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CheckoutWizardDto>> StartCheckoutWizard(Guid id)
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();

        var bookingForAuth = await bookingService.GetBookingAsync(id);
        if (bookingForAuth is null)
            return NotFound();

        if (!await authorizationService.CanAccessPropertyAsync(userId, bookingForAuth.PropertyId, GetUserRoles()))
            return NotFound();

        try
        {
            var (_, steps) = await complianceWizardService.StartCheckoutWizardAsync(id);
            return Ok(new CheckoutWizardDto
            {
                Steps = steps.Select(s => new ComplianceActivationStepDto
                {
                    Id = s.Id,
                    Label = s.Label,
                    Status = s.Status,
                    Blocker = s.Blocker,
                    Message = s.Message,
                }),
            });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("{id}/checkout-wizard/complete")]
    [Authorize(Policy = "RequireContext:short-rent:booking.write")]
    [ProducesResponseType(typeof(CompleteCheckoutWizardResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CompleteCheckoutWizardResponse>> CompleteCheckoutWizard(
        Guid id,
        [FromBody] CompleteCheckoutWizardRequest request)
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();

        var bookingForAuth = await bookingService.GetBookingAsync(id);
        if (bookingForAuth is null)
            return NotFound();

        if (!await authorizationService.CanAccessPropertyAsync(userId, bookingForAuth.PropertyId, GetUserRoles()))
            return NotFound();

        try
        {
            var (booking, propertyReady) = await complianceWizardService.CompleteCheckoutWizardAsync(
                id,
                userId,
                new CompleteCheckoutWizardInput(
                    request.ConfirmDeparture,
                    request.SupplierOrgId,
                    request.ServiceNotes,
                    request.ServiceCategory));

            checkoutReminderScheduler.CancelReminder(booking.CheckoutReminderJobId);

            return Ok(new CompleteCheckoutWizardResponse
            {
                PropertyReady = propertyReady,
                BookingStatus = booking.Status.ToString(),
            });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("{id}/check-out")]
    [Authorize(Policy = "RequireContext:short-rent:booking.write")]
    public async Task<IActionResult> CheckOut(Guid id)
    {
        var booking = await bookingService.GetBookingAsync(id);
        if (booking == null)
            return NotFound();

        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        if (!await authorizationService.CanAccessPropertyAsync(userId, booking.PropertyId, GetUserRoles()))
            return NotFound();

        // Validate status transition
        if (booking.Status != BookingStatus.CheckedIn)
        {
            return BadRequest(new
            {
                Error = "Invalid status transition",
                Message = $"Can only check-out bookings with CheckedIn status. Current status: {booking.Status}"
            });
        }

        // Validate date
        if (booking.CheckOutDate.Date > DateTime.UtcNow.Date)
        {
            return BadRequest(new
            {
                Error = "Check-out date not reached",
                Message = $"Cannot check-out before check-out date: {booking.CheckOutDate:yyyy-MM-dd}"
            });
        }

        booking.Status = BookingStatus.CheckedOut;
        await bookingService.UpdateBookingAsync(booking);
        var updated = await bookingService.GetBookingAsync(id);
        return Ok(updated is null ? BookingMapper.ToResponse(booking) : BookingMapper.ToResponse(updated));
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
        return Ok(new AlloggiatiStatusDto
        {
            BookingId = status.BookingId,
            Status = status.Status,
            ConfirmationNumber = status.ConfirmationNumber,
            ErrorMessage = status.ErrorMessage,
            ReportedAt = status.ReportedAt,
            HoursUntilDeadline = status.HoursUntilDeadline,
            IsOverdue = status.IsOverdue,
            DataComplete = status.DataComplete,
        });
    }

    private async Task<Guest> ResolveGuestAsync(CreateBookingGuestRequest guestInfo)
    {
        var existing = await guestService.GetGuestByEmailAsync(guestInfo.Email);
        if (existing != null)
            return existing;

        var guest = new Guest
        {
            FirstName = guestInfo.FirstName,
            LastName = guestInfo.LastName,
            Email = guestInfo.Email,
            PhoneNumber = guestInfo.Phone,
            Country = guestInfo.Country,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        try
        {
            return await guestService.CreateGuestAsync(guest);
        }
        catch (InvalidOperationException)
        {
            var raced = await guestService.GetGuestByEmailAsync(guestInfo.Email);
            if (raced == null)
                throw;
            return raced;
        }
    }

    /// <summary>Regenerates the guest check-in token and resends the email (AC9, US-020).</summary>
    [HttpPost("{id}/checkin/resend-link")]
    [Authorize(Policy = "RequireContext:short-rent:booking.write")]
    public async Task<ActionResult<DTOs.CheckIn.ResendCheckInLinkResponse>> ResendCheckInLink(Guid id)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();
        var booking = await bookingService.GetBookingAsync(id);
        if (booking is null) return NotFound();
        var property = await propertyService.GetPropertyAsync(booking.PropertyId);
        if (property is null) return NotFound();
        if (!authorizationService.CanAccess(userId, property.OwnerId, GetUserRoles())) return Forbid();

        if (!IsPublicCheckInLinkEligible(booking.Status))
        {
            return Conflict(new DTOs.CheckIn.ResendCheckInLinkResponse
            {
                Success = false,
                Message = $"Il link check-in è disponibile solo per prenotazioni confermate o in check-in. Stato attuale: {booking.Status}.",
            });
        }

        var existingSession = await checkInService.GetSessionForBookingAsync(booking.Id);
        if (existingSession?.Status is GuestCheckInSessionStatus.Completo or GuestCheckInSessionStatus.AlloggiatiInviato)
        {
            return Conflict(new DTOs.CheckIn.ResendCheckInLinkResponse
            {
                Success = false,
                Message = "Il check-in è già stato completato.",
            });
        }

        var token = await checkInService.CreateSessionAsync(booking.Id, booking.OrgId);
        var baseUrl = configuration["App:PublicSiteBaseUrl"] ?? "https://casazen-app.vercel.app";
        var link = $"{baseUrl}/checkin/{token}";
        var subject = $"Completa il check-in per il tuo soggiorno — {property.Name}";
        var html = BuildCheckInEmailHtml(booking.Guest.FirstName, property.Name, booking.CheckInDate, link);

        var emailSent = false;
        try
        {
            var result = await emailService.SendEmailAsync(booking.Guest.Email, subject, html);
            emailSent = result.Success;
            if (!result.Success)
            {
                logger.LogWarning(
                    "Check-in link created for booking {BookingId} but email was not sent: {ErrorDetail}",
                    booking.Id,
                    result.ErrorDetail ?? "email service returned failure");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Check-in link created for booking {BookingId} but email threw", booking.Id);
        }

        await checkInService.ExpireOtherActiveSessionsAsync(booking.Id, token);

        return Ok(new DTOs.CheckIn.ResendCheckInLinkResponse
        {
            Success = true,
            CheckInLink = link,
            Message = emailSent
                ? "Link rigenerato e inviato."
                : "Link check-in pronto. L'email non è partita: copia il link e invialo all'ospite.",
        });
    }

    /// <summary>Returns the current active guest check-in session for a booking (host view).</summary>
    [HttpGet("{id}/checkin-session")]
    [Authorize(Policy = "RequireContext:short-rent:booking.read")]
    public async Task<ActionResult<DTOs.CheckIn.CheckInSessionStatusResponse>> GetCheckInSession(Guid id)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();
        var booking = await bookingService.GetBookingAsync(id);
        if (booking is null) return NotFound();
        var property = await propertyService.GetPropertyAsync(booking.PropertyId);
        if (property is null) return NotFound();
        if (!authorizationService.CanAccess(userId, property.OwnerId, GetUserRoles())) return Forbid();

        var session = await checkInService.GetSessionForBookingAsync(booking.Id);
        return Ok(new DTOs.CheckIn.CheckInSessionStatusResponse
        {
            SessionId = session?.Id,
            Status = session?.Status.ToString(),
            SentAt = session?.SentAt,
            CompletedAt = session?.CompletedAt,
        });
    }

    private string? GetUserId() =>
        User.FindFirst("sub")?.Value
        ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;

    private IReadOnlyList<string> GetUserRoles() =>
        User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();

    private static bool IsPublicCheckInLinkEligible(BookingStatus status) =>
        status is BookingStatus.Confirmed or BookingStatus.CheckedIn;

    private static string BuildCheckInEmailHtml(string guestName, string propertyName, DateTime checkInDate, string link) =>
        $"""
        <p>Gentile {guestName},</p>
        <p>Il tuo soggiorno presso <strong>{propertyName}</strong> inizia il <strong>{checkInDate:dd/MM/yyyy}</strong>.</p>
        <p>Completa il check-in in anticipo cliccando il link qui sotto:</p>
        <p><a href="{link}">Completa il check-in</a></p>
        <p>Il link è valido per 7 giorni.</p>
        """;

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
