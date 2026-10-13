using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Alloggiati;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.Controllers;

/// <summary>
/// Alloggiati Web communications of the host's bookings (art. 109 TULPS). CasaZen does not transmit to the
/// Questura: the host sends each schedina on the portal, from the per-guest summary or by uploading the record file
/// (<c>record-file</c>, CO-13), and declares it with <c>mark-sent-manually</c> (CO-11, decision D6). Since CO-12 every
/// guest of the stay is registered (<c>stay-guests</c>): one line per guest, a head of family or group before its members.
/// </summary>
/// <remarks>
/// Booking-scoped actions use the TN-3 resource authorization (<see cref="BookingOperations"/> on the booking's
/// property): a booking of another org answers 404, one of the same org the caller may not act on answers 403.
/// </remarks>
[ApiController]
[Route("api/alloggiati")]
[Authorize(Policy = CasazenPolicies.BookingRead)]
public class AlloggiatiController(
    IAlloggiatiWebService alloggiatiWebService,
    IBookingService bookingService,
    IPropertyAuthorizationService propertyAuthorizationService,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IStayGuestService stayGuestService,
    IAlloggiatiCodeTableService codeTableService,
    IAlloggiatiReportScheduler alloggiatiReportScheduler,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    IStringLocalizer<SharedResources> localizer,
    ILogger<AlloggiatiController> logger) : ControllerBase
{
    /// <summary>Code of a send request: CasaZen has no Alloggiati Web client (422).</summary>
    public const string TransmissionUnavailableCode = "alloggiati_transmission_unavailable";

    /// <summary>Code of a code search on an unknown list (400).</summary>
    public const string CodeListUnknownCode = "alloggiati_code_list_unknown";

    /// <summary>Results of a code search.</summary>
    public const int CodeSearchLimit = 20;

    [HttpGet("summary")]
    public async Task<ActionResult<IEnumerable<AlloggiatiSummaryDto>>> GetSummary([FromQuery] Guid? propertyId)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Unauthorized();

        // AM-03: the stays of the properties the caller reaches (a member «Solo alcuni» sees only its own).
        if (await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, HttpContext.RequestAborted) is not { } scope)
            return Forbid();

        if (propertyId.HasValue && !await CanAccessPropertyAsync(propertyId.Value))
            return Forbid();

        var summaries = await alloggiatiWebService.GetSummaryAsync(scope, propertyId);
        return Ok(summaries.Select(MapSummary));
    }

    [HttpGet("{bookingId:guid}/status")]
    public async Task<ActionResult<AlloggiatiStatusDto>> GetStatus(Guid bookingId)
    {
        var (_, denied) = await AuthorizeBookingAsync(bookingId, BookingOperations.Read);
        if (denied is not null)
            return denied;

        var status = await alloggiatiWebService.GetStatusAsync(bookingId);
        return Ok(AlloggiatiStatusDto.From(status));
    }

    /// <summary>
    /// Per-guest data to copy on the Alloggiati Web portal: one row per guest of the stay in record order, with the
    /// fields still missing and the official codes still to complete. Contains the identity documents: host of the
    /// booking only.
    /// </summary>
    [HttpGet("{bookingId:guid}/guest-summary")]
    public async Task<ActionResult<AlloggiatiGuestSummaryDto>> GetGuestSummary(Guid bookingId)
    {
        var (_, denied) = await AuthorizeBookingAsync(bookingId, BookingOperations.Read);
        if (denied is not null)
            return denied;

        var summary = await alloggiatiWebService.GetGuestSummaryAsync(bookingId);
        return Ok(AlloggiatiGuestSummaryDto.From(summary));
    }

    /// <summary>
    /// The record file to upload on the Alloggiati Web portal, menu "File" (CO-13): one 168-character line per guest of
    /// the stay, UTF-8, CR+LF. Built on every request from the data and the official codes, never stored: it holds the
    /// identity documents, so it needs <c>guest.read</c> like the full document numbers, the booking must be of the
    /// caller's org (another org's booking answers 404) and it is never cached. Every download is logged with the user and
    /// the booking, never with the data. Downloading is not sending: no status changes, nothing is transmitted (D6).
    /// 422 when the file cannot be built (data incomplete or codes to complete, stay of more than 30 days, a name that
    /// cannot be written with the letters the portal accepts).
    /// </summary>
    [HttpGet("{bookingId:guid}/record-file")]
    [Authorize(Policy = CasazenPolicies.GuestRead)]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> DownloadRecordFile(Guid bookingId)
    {
        var (_, denied) = await AuthorizeBookingAsync(bookingId, BookingOperations.Read);
        if (denied is not null)
            return denied;

        var file = await alloggiatiWebService.BuildRecordFileAsync(bookingId);

        logger.LogInformation(
            "User {UserId} downloaded the Alloggiati record file of booking {BookingId}: {LineCount} lines",
            User.GetUserId(), bookingId, file.LineCount);
        Response.Headers.CacheControl = "private, no-store";
        return File(file.Content, "text/plain; charset=utf-8", file.FileName);
    }

    /// <summary>
    /// Counts of the guest data of the stay (MO-08): declared, registered and complete guests, with no personal data. The
    /// host app shows "K ospiti completi su N" in the booking detail without downloading the identity data of the guests.
    /// </summary>
    [HttpGet("{bookingId:guid}/guest-progress")]
    public async Task<ActionResult<AlloggiatiGuestProgressDto>> GetGuestProgress(Guid bookingId)
    {
        var (_, denied) = await AuthorizeBookingAsync(bookingId, BookingOperations.Read);
        if (denied is not null)
            return denied;

        var summary = await alloggiatiWebService.GetGuestSummaryAsync(bookingId);
        return Ok(AlloggiatiGuestProgressDto.From(summary));
    }

    /// <summary>
    /// Full document numbers of the guests of the stay (the summary shows them masked, CO-09). Host of the booking with
    /// <c>guest.read</c> only; every request is logged with the user and the positions (audit), never the numbers.
    /// <paramref name="position"/> limits the answer to one guest.
    /// </summary>
    [HttpGet("{bookingId:guid}/stay-guests/document-numbers")]
    [Authorize(Policy = CasazenPolicies.GuestRead)]
    public async Task<ActionResult<IEnumerable<StayGuestDocumentNumberDto>>> GetDocumentNumbers(
        Guid bookingId,
        [FromQuery] int? position)
    {
        var (booking, denied) = await AuthorizeBookingAsync(bookingId, BookingOperations.Read);
        if (denied is not null)
            return denied;

        var guests = await stayGuestService.GetForBookingAsync(booking!, HttpContext.RequestAborted);
        var numbers = guests
            .Where(g => position is null || g.Position == position)
            .Where(g => AlloggiatiRecordRules.RequiresDocument(g.Type) && !string.IsNullOrWhiteSpace(g.DocumentNumber))
            .Select(g => new StayGuestDocumentNumberDto
            {
                Position = g.Position,
                StayGuestId = g.Id == Guid.Empty ? null : g.Id,
                DocumentNumber = g.DocumentNumber,
            })
            .ToList();

        logger.LogInformation(
            "User {UserId} viewed the document numbers of booking {BookingId}, positions {Positions}",
            User.GetUserId(), bookingId, string.Join(',', numbers.Select(n => n.Position)));
        return Ok(numbers);
    }

    /// <summary>
    /// Replaces the guests of the stay (host entry: walk-in, a guest who cannot use the link, corrections, codes to
    /// complete). Same rules as the guest portal: kinds and order, document only for a single guest or a head of family
    /// or group. Invalid data → 400 ValidationProblem keyed <c>Guests[i].Field</c>; answers the updated summary. The rows
    /// record the host as author (CO-09); complete data schedules the communication like the guest portal does.
    /// </summary>
    [HttpPut("{bookingId:guid}/stay-guests")]
    [Authorize(Policy = CasazenPolicies.AlloggiatiSubmit)]
    public async Task<ActionResult<AlloggiatiGuestSummaryDto>> ReplaceStayGuests(
        Guid bookingId,
        [FromBody] ReplaceStayGuestsRequest request)
    {
        var (booking, denied) = await AuthorizeBookingAsync(bookingId, AlloggiatiOperations.Submit);
        if (denied is not null)
            return denied;

        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var userId = User.GetUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var result = await stayGuestService.ReplaceAsync(
            booking!,
            request.Guests.Select(g => g.ToInput()).ToList(),
            StayGuestAuthor.Host(userId),
            cancellationToken: HttpContext.RequestAborted);
        if (!result.Success)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError(error.ModelStateKey(nameof(request.Guests)), localizer[error.MessageKey, error.MessageArgs]);
            return ValidationProblem(ModelState);
        }

        logger.LogInformation(
            "Guests of booking {BookingId} replaced by the host {UserId}: {GuestCount} guests",
            bookingId, userId, result.Guests.Count);
        var summary = await alloggiatiWebService.GetGuestSummaryAsync(bookingId);

        // Same as the guest portal: complete data schedules the communication for the arrival day (idempotent). The data
        // is saved: a scheduling failure is logged, and from the arrival day the booking shows "to send manually" anyway.
        if (summary.DataComplete)
        {
            try
            {
                await alloggiatiReportScheduler.EnsureScheduledAsync(bookingId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Alloggiati report of booking {BookingId} could not be scheduled after the host entry", bookingId);
            }
        }

        return Ok(AlloggiatiGuestSummaryDto.From(summary));
    }

    /// <summary>
    /// Official codes of a list (<c>comuni</c>, <c>stati</c>, <c>documenti</c>, <c>luoghi</c>) whose description matches
    /// <paramref name="q"/>. Empty until an admin imports the tables.
    /// </summary>
    [HttpGet("codes")]
    public async Task<ActionResult<IEnumerable<AlloggiatiCodeEntryDto>>> SearchCodes([FromQuery] string? list, [FromQuery] string? q)
    {
        if (!AlloggiatiCodeLists.TryParse(list, out var tables))
            return this.ApiProblem(StatusCodes.Status400BadRequest, CodeListUnknownCode, "AlloggiatiCodeListUnknown");

        var results = await codeTableService.SearchAsync(tables, q ?? string.Empty, CodeSearchLimit, HttpContext.RequestAborted);
        return Ok(results.Select(AlloggiatiCodeEntryDto.From));
    }

    /// <summary>
    /// Records that the host sent the schedina on the Questura portal on <c>sentOn</c>. The status becomes
    /// <c>InviatoManualmente</c> (declared by the host), never <c>Inviato</c>, which needs a real receipt.
    /// </summary>
    [HttpPost("{bookingId:guid}/mark-sent-manually")]
    [Authorize(Policy = CasazenPolicies.AlloggiatiSubmit)]
    public async Task<ActionResult<AlloggiatiStatusDto>> MarkSentManually(
        Guid bookingId,
        [FromBody] MarkAlloggiatiSentManuallyRequest request)
    {
        var (_, denied) = await AuthorizeBookingAsync(bookingId, AlloggiatiOperations.Submit);
        if (denied is not null)
            return denied;

        var status = await alloggiatiWebService.MarkSentManuallyAsync(bookingId, request.SentOn!.Value);
        logger.LogInformation("Alloggiati communication of booking {BookingId} declared sent manually", bookingId);
        return Ok(AlloggiatiStatusDto.From(status));
    }

    /// <summary>
    /// Transmission to Alloggiati Web is not available (CasaZen has no web service client): always 422, nothing is
    /// changed. The host sends the schedina on the portal (typing it or uploading the <c>record-file</c>) and uses
    /// <c>mark-sent-manually</c>.
    /// </summary>
    [HttpPost("{bookingId:guid}/send")]
    [Authorize(Policy = CasazenPolicies.AlloggiatiSubmit)]
    public async Task<IActionResult> SendManual(Guid bookingId)
    {
        var (_, denied) = await AuthorizeBookingAsync(bookingId, AlloggiatiOperations.Submit);
        if (denied is not null)
            return denied;

        return this.ApiProblem(
            StatusCodes.Status422UnprocessableEntity,
            TransmissionUnavailableCode,
            "AlloggiatiTransmissionUnavailable");
    }

    /// <summary>
    /// TN-3: the booking must be visible (tenant filter, otherwise 404) and the caller must hold
    /// <paramref name="operation"/> on its property (org, permission, ownership; otherwise 403).
    /// </summary>
    private async Task<(Booking? Booking, ActionResult? Denied)> AuthorizeBookingAsync(
        Guid bookingId,
        HostOperationRequirement operation)
    {
        var booking = await bookingService.GetBookingAsync(bookingId);
        if (booking is null)
            return (null, NotFound());

        var property = await hostResources.ForPropertyAsync(booking.PropertyId, HttpContext.RequestAborted);
        if (property is null)
            return (null, NotFound());

        if (!await authorizationService.IsAuthorizedAsync(User, property with { OrgId = booking.OrgId }, operation))
        {
            logger.LogWarning(
                "User {UserId} denied {Permission} on the Alloggiati data of booking {BookingId}",
                User.GetUserId(), operation.PermissionKey, bookingId);
            return (null, Forbid());
        }

        return (booking, null);
    }

    private async Task<bool> CanAccessPropertyAsync(Guid propertyId)
    {
        var userId = User.GetUserId();
        if (userId is null)
            return false;

        return await propertyAuthorizationService.CanAccessPropertyAsync(userId, propertyId, GetUserRoles());
    }

    private IEnumerable<string> GetUserRoles() =>
        User.FindAll(ClaimTypes.Role).Select(c => c.Value);

    private static AlloggiatiSummaryDto MapSummary(AlloggiatiSummaryInfo info) =>
        new()
        {
            BookingId = info.BookingId,
            GuestName = info.GuestName,
            PropertyName = info.PropertyName,
            CheckInDate = info.CheckInDate,
            Status = info.Status,
            DataComplete = info.DataComplete,
            IsOverdue = info.IsOverdue,
            HoursUntilDeadline = info.HoursUntilDeadline,
            DeadlineAt = info.DeadlineAt,
            IsShortStay = info.IsShortStay,
        };
}
