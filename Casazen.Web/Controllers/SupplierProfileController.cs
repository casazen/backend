using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Web.DTOs.ServiceRequests;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.Controllers;

/// <summary>
/// Supplier-facing endpoints: activation wizard, profile, inbox shell, availability, and calendar sync (US-022 / #292).
/// All routes are scoped to the caller's supplier org via <c>ISupplierOrgContextResolver</c>.
/// </summary>
[ApiController]
[Route("api/supplier")]
[Authorize(Policy = "RequireSupplier")]
public class SupplierProfileController(
    ISupplierService supplierService,
    ISupplierOrgContextResolver supplierOrgContextResolver,
    IServiceRequestService serviceRequestService,
    CalendarSyncService calendarSyncService,
    IImageStorageService imageStorageService,
    ILogger<SupplierProfileController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ─── Activation ──────────────────────────────────────────────────────────

    /// <summary>Returns the 5-step activation wizard status for the caller's supplier org.</summary>
    [HttpGet("profile/activation")]
    [ProducesResponseType(typeof(ActivationStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ActivationStatusDto>> GetActivationStatus(CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetOrProvisionSupplierOrgIdAsync(cancellationToken);
        if (orgId is null) return NotFound(new { error = "No supplier org found" });

        var profile = await supplierService.GetProfileAsync(orgId.Value, cancellationToken);
        if (profile is null) return NotFound(new { error = "Supplier profile not found" });

        var steps = await supplierService.GetActivationStepsAsync(orgId.Value, cancellationToken);

        return Ok(new ActivationStatusDto
        {
            Status = profile.Status.ToString(),
            Steps = steps.Select(s => new ActivationStepDto
            {
                Id = s.Id,
                Label = s.Label,
                Status = s.Status,
                Blocker = s.Blocker,
            }),
        });
    }

    /// <summary>Completes the activation wizard and sets the profile to <c>Active</c>.</summary>
    [HttpPost("profile/activation/complete")]
    [ProducesResponseType(typeof(CompleteActivationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CompleteActivationResponse>> CompleteActivation(
        [FromBody] CompleteActivationRequest request,
        CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetOrProvisionSupplierOrgIdAsync(cancellationToken);
        if (orgId is null) return NotFound(new { error = "No supplier org found" });

        try
        {
            var profile = await supplierService.CompleteActivationAsync(orgId.Value, request.TosAccepted, cancellationToken);
            return Ok(new CompleteActivationResponse { Status = profile.Status.ToString() });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message, code = "activation_blockers_remain" });
        }
    }

    // ─── Profile ─────────────────────────────────────────────────────────────

    /// <summary>Returns the caller's supplier profile.</summary>
    [HttpGet("profile")]
    [ProducesResponseType(typeof(SupplierProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierProfileDto>> GetProfile(CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetOrProvisionSupplierOrgIdAsync(cancellationToken);
        if (orgId is null) return NotFound(new { error = "No supplier org found" });

        var profile = await supplierService.GetProfileAsync(orgId.Value, cancellationToken);
        if (profile is null) return NotFound(new { error = "Supplier profile not found" });

        return Ok(MapProfile(profile));
    }

    /// <summary>
    /// Updates the caller's supplier profile fields. <c>categories</c> holds codes of <c>GET /api/service-categories</c>;
    /// any other value is a 422 <c>invalid_service_category</c> and nothing is saved (SU-03).
    /// </summary>
    [HttpPut("profile")]
    [ProducesResponseType(typeof(SupplierProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SupplierProfileDto>> UpdateProfile(
        [FromBody] UpdateSupplierProfileRequest request,
        CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetOrProvisionSupplierOrgIdAsync(cancellationToken);
        if (orgId is null) return NotFound(new { error = "No supplier org found" });

        logger.LogInformation(
            "UpdateProfile: OrgId={OrgId}, Categories={CatCount}, Comuni={ComCount}, Bio={BioLen}",
            orgId.Value,
            request.Categories?.Count() ?? 0,
            request.Comuni?.Count() ?? 0,
            request.Bio?.Length ?? 0);

        var profile = await supplierService.UpdateProfileAsync(
            orgId.Value,
            request.LegalName, request.VatNumber, request.Phone,
            request.Categories, request.Comuni, request.Bio, request.PhotoUrls,
            cancellationToken);

        if (profile is null) return NotFound(new { error = "Supplier profile not found" });

        var dto = MapProfile(profile);
        logger.LogInformation(
            "UpdateProfile result: Categories={CatCount}, Comuni={ComCount}, Bio={BioLen}",
            dto.Categories.Count(),
            dto.Comuni.Count(),
            dto.Bio?.Length ?? 0);

        return Ok(dto);
    }

    // ─── Inbox ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns paginated open service requests assigned to this supplier.
    /// Returns empty list until #293 (micro-marketplace) is implemented.
    /// </summary>
    [HttpGet("inbox")]
    [ProducesResponseType(typeof(SupplierInboxResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SupplierInboxResponse>> GetInbox(
        [FromQuery] string? status = "open",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var orgId = await supplierOrgContextResolver.GetOrProvisionSupplierOrgIdAsync(cancellationToken);
        if (orgId is null) return NotFound(new { error = "No supplier org found" });

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var openOnly = string.Equals(status, "open", StringComparison.OrdinalIgnoreCase);

        var (items, total) = await serviceRequestService.ListForSupplierAsync(
            orgId.Value, openOnly, page, pageSize, cancellationToken);

        logger.LogDebug("Supplier inbox — status={Status}, page={Page}, total={Total}", status, page, total);

        return Ok(new SupplierInboxResponse
        {
            Items = items.Select(ServiceRequestsController.MapSummary),
            Total = total,
        });
    }

    // ─── Availability ─────────────────────────────────────────────────────────

    /// <summary>Returns the supplier's saved availability for a date range (defaults: today → +13 days, max 90 days).</summary>
    [HttpGet("availability")]
    [ProducesResponseType(typeof(SupplierAvailabilityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierAvailabilityResponse>> GetAvailability(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetOrProvisionSupplierOrgIdAsync(cancellationToken);
        if (orgId is null) return NotFound(new { error = "No supplier org found" });

        var rangeFrom = from ?? TimeProvider.System.TodayInRomeAsDateOnly();
        var rangeTo = to ?? rangeFrom.AddDays(13);

        if (rangeTo < rangeFrom)
            return BadRequest(new { error = "to deve essere maggiore o uguale a from" });

        const int maxRangeDays = 90;
        if (rangeTo.DayNumber - rangeFrom.DayNumber > maxRangeDays)
            return BadRequest(new { error = $"L'intervallo massimo è di {maxRangeDays} giorni" });

        var entries = await supplierService.GetAvailabilityAsync(orgId.Value, rangeFrom, rangeTo, cancellationToken);

        return Ok(new SupplierAvailabilityResponse
        {
            Dates = entries.Select(e => new AvailabilityEntryDto
            {
                Date = e.Date,
                Available = e.Available,
            }),
        });
    }

    /// <summary>Updates the supplier's availability for a list of dates.</summary>
    [HttpPut("availability")]
    [ProducesResponseType(typeof(UpdateAvailabilityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UpdateAvailabilityResponse>> UpdateAvailability(
        [FromBody] UpdateAvailabilityRequest request,
        CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetOrProvisionSupplierOrgIdAsync(cancellationToken);
        if (orgId is null) return NotFound(new { error = "No supplier org found" });

        var entries = request.Dates.Select(d => (d.Date, d.Available));
        var updated = await supplierService.UpdateAvailabilityAsync(orgId.Value, entries, cancellationToken);

        return Ok(new UpdateAvailabilityResponse { Updated = updated });
    }

    // ─── Dashboard ────────────────────────────────────────────────────────────

    /// <summary>Returns aggregated KPIs for the supplier dashboard.</summary>
    [HttpGet("dashboard")]
    [ProducesResponseType(typeof(SupplierDashboardDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierDashboardDto>> GetDashboard(
        [FromServices] IStringLocalizer<SharedResources> localizer,
        CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetOrProvisionSupplierOrgIdAsync(cancellationToken);
        if (orgId is null) return NotFound(new { error = "No supplier org found" });

        var stats = await supplierService.GetDashboardStatsAsync(orgId.Value, cancellationToken);
        if (stats is null) return NotFound(new { error = "Supplier profile not found" });
        var (syncErrorCode, syncErrorMessage) = ICalErrorMessages.Describe(stats.CalendarSyncError, localizer);

        return Ok(new SupplierDashboardDto
        {
            ProfileCompletionPercent = stats.ProfileCompletionPercent,
            Status = stats.Status,
            AvailabilityRate = stats.AvailabilityRate,
            CalendarSyncStatus = new CalendarSyncStatusDto
            {
                CalendarSyncType = stats.CalendarSyncType,
                IcalFeedUrl = stats.IcalFeedUrl,
                CalendarLastSyncAt = stats.CalendarLastSyncAt,
                LastSyncStatus = stats.CalendarSyncStatus,
                CalendarSyncErrorCode = syncErrorCode,
                CalendarSyncError = syncErrorMessage,
            },
            LastUpdated = stats.LastUpdated,
        });
    }

    /// <summary>
    /// Work KPIs of the caller's supplier org from its service requests (SU-11, A4-15): completed and rejected in the
    /// Europe/Rome <paramref name="period"/>, waiting to be taken and taken (upcoming) now.
    /// </summary>
    [HttpGet("dashboard/kpis")]
    [ProducesResponseType(typeof(SupplierKpisDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierKpisDto>> GetKpis(
        [FromServices] ISupplierKpiService kpiService,
        [FromQuery] SupplierKpiPeriod period = SupplierKpiPeriod.CurrentMonth,
        CancellationToken cancellationToken = default)
    {
        // A number that is not a period ("?period=9") binds as an undefined enum value.
        if (!Enum.IsDefined(period))
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "SupplierKpiPeriodInvalid");

        var orgId = await supplierOrgContextResolver.GetOrProvisionSupplierOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        var kpis = await kpiService.GetKpisAsync(orgId.Value, period, cancellationToken);

        return Ok(new SupplierKpisDto
        {
            Period = kpis.Period.ToString(),
            From = kpis.From,
            To = kpis.To,
            TimeZone = RomeCalendar.TimeZoneId,
            Completed = kpis.Completed,
            Rejected = kpis.Rejected,
            AwaitingAcceptance = kpis.AwaitingAcceptance,
            Upcoming = kpis.Upcoming,
            TotalRequests = kpis.TotalRequests,
        });
    }

    // ─── Photo Upload ─────────────────────────────────────────────────────────

    /// <summary>Uploads supplier photos. Accepts up to 10 JPEG/PNG/WebP files (max 5 MB each).</summary>
    [HttpPost("profile/photos")]
    [ProducesResponseType(typeof(SupplierPhotoUploadResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierPhotoUploadResponse>> UploadPhotos(
        [FromForm] List<IFormFile> photos,
        CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetOrProvisionSupplierOrgIdAsync(cancellationToken);
        if (orgId is null) return NotFound(new { error = "No supplier org found" });

        var profile = await supplierService.GetProfileAsync(orgId.Value, cancellationToken);
        if (profile is null) return NotFound(new { error = "Supplier profile not found" });

        const int maxPhotos = 10;
        var existingUrls = JsonSerializer.Deserialize<List<string>>(profile.PhotoUrlsJson, JsonOpts) ?? [];
        if (existingUrls.Count + photos.Count > maxPhotos)
            return BadRequest(new { error = $"Massimo {maxPhotos} foto consentite. Attuali: {existingUrls.Count}, in upload: {photos.Count}" });

        var uploadedUrls = new List<string>();
        foreach (var photo in photos)
        {
            if (!imageStorageService.ValidateImage(photo))
            {
                logger.LogWarning("Invalid supplier photo rejected: {FileName}", photo.FileName);
                return BadRequest(new { error = $"File non valido: {photo.FileName}. Formati accettati: JPEG, PNG, WebP. Dimensione max: 10 MB" });
            }

            try
            {
                var url = await imageStorageService.UploadSupplierPhotoAsync(photo, orgId.Value);
                uploadedUrls.Add(url);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to upload supplier photo for org {OrgId}", orgId.Value);
                return StatusCode(500, new { error = "Upload foto fallito" });
            }
        }

        var allUrls = existingUrls.Concat(uploadedUrls).ToList();
        await supplierService.UpdateProfileAsync(
            orgId.Value,
            legalName: null, vatNumber: null, phone: null,
            categories: null, comuni: null, bio: null,
            photoUrls: allUrls,
            cancellationToken: cancellationToken);

        return Ok(new SupplierPhotoUploadResponse { Urls = allUrls });
    }

    // ─── Calendar Sync ───────────────────────────────────────────────────────

    /// <summary>Returns the supplier's calendar sync status (<c>lastSyncStatus</c> <c>Syncing</c> while a sync is queued).</summary>
    [HttpGet("calendar/status")]
    [ProducesResponseType(typeof(CalendarSyncStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CalendarSyncStatusDto>> GetCalendarStatus(
        [FromServices] IStringLocalizer<SharedResources> localizer,
        CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetOrProvisionSupplierOrgIdAsync(cancellationToken);
        if (orgId is null) return NotFound(new { error = "No supplier org found" });

        var profile = await supplierService.GetProfileAsync(orgId.Value, cancellationToken);
        if (profile is null) return NotFound(new { error = "Supplier profile not found" });

        return Ok(MapCalendarStatus(profile, localizer));
    }

    /// <summary>
    /// Sets or updates the supplier's iCal feed URL and queues its first sync in a Hangfire job (SU-15, A4-11, A9-14):
    /// the download never runs inside the request. 202 with <c>lastSyncStatus</c> <c>Syncing</c>; the calendar is
    /// synced when <c>GET calendar/status</c> leaves <c>Syncing</c>. 400 <c>ical_invalid_url</c>.
    /// </summary>
    [HttpPut("calendar/ical")]
    [ProducesResponseType(typeof(CalendarSyncStatusDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CalendarSyncStatusDto>> SetIcalFeed(
        [FromBody] SetIcalFeedRequest request,
        [FromServices] IBackgroundJobClient backgroundJobClient,
        [FromServices] IStringLocalizer<SharedResources> localizer,
        CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetOrProvisionSupplierOrgIdAsync(cancellationToken);
        if (orgId is null) return NotFound(new { error = "No supplier org found" });

        Casazen.Core.Entities.SupplierProfile? profile;
        try
        {
            profile = await supplierService.UpdateCalendarSyncAsync(
                orgId.Value,
                CalendarSyncType.ICalFeed,
                request.IcalFeedUrl,
                calendarSyncError: null,
                cancellationToken);
        }
        catch (DomainRuleException ex) when (ex.Code == ICalErrorCodes.InvalidUrl)
        {
            // Only an external https URL: the server downloads it (FD-16, A4-10).
            return this.ApiProblem(StatusCodes.Status400BadRequest, ex.Code, ex.MessageKey);
        }

        if (profile is null) return NotFound(new { error = "Supplier profile not found" });

        QueueSupplierSync(orgId.Value, backgroundJobClient);
        return Accepted(MapCalendarStatus(profile, localizer));
    }

    /// <summary>
    /// "Sync now": 202 with <c>lastSyncStatus</c> <c>Syncing</c> and a queued job, or the current state when a sync is
    /// already queued (nothing more is queued). 422 <c>ical_supplier_no_feed</c> when no iCal URL is saved.
    /// </summary>
    [HttpPost("calendar/sync")]
    [ProducesResponseType(typeof(CalendarSyncStatusDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<CalendarSyncStatusDto>> SyncCalendarNow(
        [FromServices] IBackgroundJobClient backgroundJobClient,
        [FromServices] IStringLocalizer<SharedResources> localizer,
        CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetOrProvisionSupplierOrgIdAsync(cancellationToken);
        if (orgId is null) return NotFound(new { error = "No supplier org found" });

        var (profile, queue) = await calendarSyncService.RequestSyncAsync(orgId.Value, cancellationToken);
        if (profile is null) return NotFound(new { error = "Supplier profile not found" });

        if (queue)
            QueueSupplierSync(orgId.Value, backgroundJobClient);

        return Accepted(MapCalendarStatus(profile, localizer));
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private void QueueSupplierSync(Guid orgId, IBackgroundJobClient backgroundJobClient)
    {
        try
        {
            backgroundJobClient.Enqueue<IcalSupplierSyncJob>(job => job.SyncSupplierAsync(orgId, CancellationToken.None));
        }
        catch (Exception ex)
        {
            // The URL is saved and the state says Syncing: the recurring ical-supplier-sync job (every 15 minutes) syncs it.
            logger.LogError(ex, "Could not queue the iCal sync of supplier {OrgId}", orgId);
        }
    }

    private static CalendarSyncStatusDto MapCalendarStatus(
        Casazen.Core.Entities.SupplierProfile profile,
        IStringLocalizer<SharedResources> localizer)
    {
        var (syncErrorCode, syncErrorMessage) = ICalErrorMessages.Describe(profile.CalendarSyncError, localizer);
        return new CalendarSyncStatusDto
        {
            CalendarSyncType = profile.CalendarSyncType.ToString(),
            IcalFeedUrl = profile.IcalFeedUrl,
            CalendarLastSyncAt = profile.CalendarLastSyncAt,
            LastSyncStatus = profile.CalendarSyncStatus.ToString(),
            CalendarSyncErrorCode = syncErrorCode,
            CalendarSyncError = syncErrorMessage,
        };
    }

    private static SupplierProfileDto MapProfile(Casazen.Core.Entities.SupplierProfile profile) => new()
    {
        OrgId = profile.OrgId,
        Status = profile.Status.ToString(),
        LegalName = profile.LegalName,
        VatNumber = profile.VatNumber,
        Phone = profile.Phone,
        Email = profile.Email,
        Categories = JsonSerializer.Deserialize<IEnumerable<string>>(profile.CategoriesJson, JsonOpts) ?? [],
        Comuni = JsonSerializer.Deserialize<IEnumerable<string>>(profile.ComuniJson, JsonOpts) ?? [],
        Bio = profile.Bio,
        PhotoUrls = JsonSerializer.Deserialize<IEnumerable<string>>(profile.PhotoUrlsJson, JsonOpts) ?? [],
        TosAcceptedAt = profile.TosAcceptedAt,
    };
}
