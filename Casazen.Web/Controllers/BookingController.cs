using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Alloggiati;
using Casazen.Web.DTOs.Compliance;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

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
    IAlloggiatiReportScheduler alloggiatiReportScheduler,
    IGuestCheckInService checkInService,
    IComplianceWizardService complianceWizardService,
    IEmailQueue emailQueue,
    PublicSiteLinks publicSiteLinks,
    IStringLocalizer<SharedResources> localizer,
    ILogger<BookingsController> logger,
    TimeProvider? timeProvider = null) : ControllerBase
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

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

        if (booking.CheckInDate.Date > _clock.TodayInRome())
        {
            return BadRequest(new
            {
                error = "Data di check-in non raggiunta",
                message = $"Impossibile fare check-in prima del {booking.CheckInDate:yyyy-MM-dd}"
            });
        }

        booking.Status = BookingStatus.CheckedIn;
        // Real arrival: the Alloggiati Web term (art. 109 TULPS: 24 hours, 6 for short stays) runs from here.
        booking.ArrivedAt ??= _clock.GetUtcNow().UtcDateTime;
        await bookingService.UpdateBookingAsync(booking);

        // Idempotent per booking and guest: if the guest portal already scheduled the report, nothing is queued again.
        // The check-in is already saved: a scheduling failure is logged, and from the arrival day the booking shows
        // "to send manually" anyway (derived status), so the host is never told it was sent.
        try
        {
            await alloggiatiReportScheduler.EnsureScheduledAsync(booking.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Alloggiati report of booking {BookingId} could not be scheduled at check-in", booking.Id);
        }

        // The check-out reminder (email + push) comes from the hourly stay-alerts job for every confirmed or checked-in
        // stay, not from the check-in (CO-10): nothing to schedule here.

        logger.LogInformation("Check-in completed for booking {BookingId}, Alloggiati Web report scheduled", id);
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

        // A missing App:PublicSiteBaseUrl is a configuration error (500) before any session is created: never a wrong link.
        publicSiteLinks.EnsureConfigured();

        var token = await checkInService.CreateSessionAsync(booking.Id, booking.OrgId);
        var link = publicSiteLinks.GuestCheckIn(token);
        var email = EmailTemplates.GuestCheckInLink(
            EmailTemplates.DefaultCulture,
            booking.Guest.FirstName,
            property.Name,
            booking.CheckInDate,
            link);

        // Delivered by a Hangfire job: the provider is never called inside the request.
        var emailQueued = emailQueue.Enqueue(booking.Guest.Email, email, EmailTemplates.Names.GuestCheckInLink);
        if (!emailQueued)
            logger.LogWarning("Check-in link created for booking {BookingId} but its email was not queued", booking.Id);

        await checkInService.ExpireOtherActiveSessionsAsync(booking.Id, token);

        return Ok(new DTOs.CheckIn.ResendCheckInLinkResponse
        {
            Success = true,
            CheckInLink = link,
            Message = emailQueued ? localizer["CheckInLinkEmailQueued"] : localizer["CheckInLinkEmailNotQueued"],
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
