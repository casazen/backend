using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Web.Authorization;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.DTOs;
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
    IComuneDirectory comuneDirectory,
    CalendarSyncService calendarSyncService,
    IImageStorageService imageStorageService,
    ILogger<SupplierProfileController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ─── Activation ──────────────────────────────────────────────────────────

    /// <summary>
    /// The 5-step activation wizard (SU-05): the step each requirement belongs to, the step to resume at (saved by the
    /// server) and the Terms of Service acceptance. The same shape for a pending, active or suspended supplier: an
    /// active one whose accepted Terms are not the current version sees <c>tos.reacceptanceRequired</c>.
    /// </summary>
    [HttpGet("profile/activation")]
    [ProducesResponseType(typeof(ActivationStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ActivationStatusDto>> GetActivationStatus(CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetOrProvisionSupplierOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        var profile = await supplierService.GetProfileAsync(orgId.Value, cancellationToken);
        var state = await supplierService.GetActivationAsync(orgId.Value, cancellationToken);
        if (profile is null || state is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        return Ok(new ActivationStatusDto
        {
            Status = profile.Status.ToString(),
            CurrentStep = state.CurrentStep,
            Steps = state.Steps.Select(s => new ActivationStepDto
            {
                Id = s.Id,
                Status = s.Status,
                Blocker = s.Blocker,
                Required = s.Required,
            }),
            Tos = new SupplierTosDto
            {
                CurrentVersion = state.Tos.CurrentVersion,
                AcceptedVersion = state.Tos.AcceptedVersion,
                AcceptedAt = state.Tos.AcceptedAt,
                ReacceptanceRequired = state.Tos.ReacceptanceRequired,
                BlocksActions = state.Tos.BlocksActions,
            },
        });
    }

    /// <summary>Saves the wizard step (1-5) the supplier reached, so the wizard resumes there on any device.</summary>
    [HttpPut("profile/activation/step")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SetActivationStep(
        [FromBody] SetActivationStepRequest request,
        CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetOrProvisionSupplierOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        try
        {
            await supplierService.SetActivationStepAsync(orgId.Value, request.Step, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");
        }
    }

    /// <summary>
    /// Completes the activation wizard and sets the profile to <c>Active</c> (SU-05). 409
    /// <c>supplier_activation_blocked</c> with <c>blockers</c> (stable codes, e.g. <c>categories_missing</c>) when the stored
    /// profile does not meet the requirements or the Terms are not accepted; 409 <c>supplier_tos_version_stale</c> when
    /// <c>tosVersion</c> is not the current version. The acceptance is recorded with version, time and IP.
    /// </summary>
    [HttpPost("profile/activation/complete")]
    [ProducesResponseType(typeof(CompleteActivationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CompleteActivationResponse>> CompleteActivation(
        [FromBody] CompleteActivationRequest request,
        CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetOrProvisionSupplierOrgIdAsync(cancellationToken);
        var userId = User.GetUserId();
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");
        if (userId is null)
            return Unauthorized();

        try
        {
            var profile = await supplierService.CompleteActivationAsync(
                orgId.Value, request.TosAccepted, request.TosVersion, userId, ClientIp.GetString(HttpContext), cancellationToken);
            return Ok(new CompleteActivationResponse { Status = profile.Status.ToString() });
        }
        catch (SupplierActivationBlockedException ex)
        {
            var problem = ApiProblemDetails.Create(
                HttpContext, StatusCodes.Status409Conflict, ex.Code, ex.MessageKey);
            problem.Extensions["blockers"] = ex.Blockers;
            return new ObjectResult(problem)
            {
                StatusCode = StatusCodes.Status409Conflict,
                ContentTypes = { ApiProblemDetails.ContentType },
            };
        }
        catch (KeyNotFoundException)
        {
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");
        }
    }

    /// <summary>
    /// Re-acceptance of the current Terms of Service by an active supplier after a new version (SU-05, A4-31). It never
    /// changes the status. 409 <c>supplier_tos_version_stale</c> when <c>tosVersion</c> is not the current version.
    /// </summary>
    [HttpPost("profile/tos/accept")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AcceptTos([FromBody] AcceptSupplierTosRequest request, CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetOrProvisionSupplierOrgIdAsync(cancellationToken);
        var userId = User.GetUserId();
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");
        if (userId is null)
            return Unauthorized();

        try
        {
            await supplierService.AcceptTosAsync(orgId.Value, request.TosVersion, userId, ClientIp.GetString(HttpContext), cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");
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
        if (orgId is null) return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        var profile = await supplierService.GetProfileAsync(orgId.Value, cancellationToken);
        if (profile is null) return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        return Ok(await MapProfileAsync(profile, cancellationToken));
    }

    /// <summary>
    /// Updates the caller's supplier profile fields. <c>categories</c> holds codes of <c>GET /api/service-categories</c>;
    /// any other value is a 422 <c>invalid_service_category</c> and nothing is saved (SU-03). <c>comuneIstatCodes</c> holds the
    /// ISTAT codes of the comuni chosen from the official list (<c>GET /api/comuni</c>): a code that is not an active comune of
    /// it is a 422 <c>comune_istat_unknown</c>, any code while the list is not imported <c>comuni_dataset_unavailable</c> (SU-04).
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
        if (orgId is null) return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

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
            cancellationToken,
            request.ComuneIstatCodes);

        if (profile is null) return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        var dto = await MapProfileAsync(profile, cancellationToken);
        logger.LogInformation(
            "UpdateProfile result: Categories={CatCount}, Comuni={ComCount}, Bio={BioLen}",
            dto.Categories.Count(),
            dto.Comuni.Count(),
            dto.Bio?.Length ?? 0);

        return Ok(dto);
    }

    // ─── Inbox ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A page of the service requests sent to the caller's supplier org (SU-08, A4-14), server-side paginated.
    /// <c>status</c>: <c>open</c> (default: waiting, taken, in progress), <c>history</c> (completed, paid, rejected),
    /// <c>all</c>, or one status name; <c>from</c>/<c>to</c>: Europe/Rome days (<c>YYYY-MM-DD</c>, both included) on the
    /// activity date of each request (<see cref="SupplierInboxStatusFilter"/>). 400 <c>validation_error</c> for another
    /// status or <c>from</c> after <c>to</c>. Items carry comune, date and stay dates; street address and host contact
    /// only for the requests the supplier took; never the guest.
    /// </summary>
    [HttpGet("inbox")]
    [ProducesResponseType(typeof(SupplierInboxResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierInboxResponse>> GetInbox(
        [FromServices] ISupplierServiceRequestReader reader,
        [FromQuery] string? status = SupplierInboxStatusFilter.OpenValue,
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (!SupplierInboxStatusFilter.TryParse(status, out var statuses))
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "SupplierInboxStatusInvalid");

        if (from is { } start && to is { } end && start > end)
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "SupplierInboxPeriodInvalid");

        var orgId = await supplierOrgContextResolver.GetOrProvisionSupplierOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var (items, total) = await reader.ListAsync(
            orgId.Value, new SupplierInboxQuery(statuses, from, to, page, pageSize), cancellationToken);

        logger.LogDebug("Supplier inbox: status={Status}, page={Page}, total={Total}", status, page, total);

        return Ok(new SupplierInboxResponse
        {
            Items = items.Select(SupplierServiceRequestMapper.ToDto).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    /// <summary>
    /// One request of the caller's supplier org with its history (SU-08): 404 <c>service_request_not_found</c> when it
    /// does not exist or was sent to another supplier. Street address and host contact only after the take.
    /// </summary>
    [HttpGet("inbox/{id:guid}")]
    [ProducesResponseType(typeof(SupplierServiceRequestDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierServiceRequestDetailDto>> GetInboxItem(
        Guid id,
        [FromServices] ISupplierServiceRequestReader reader,
        CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetOrProvisionSupplierOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        var request = await reader.GetAsync(id, orgId.Value, cancellationToken);
        if (request is null)
            return ServiceRequestsController.ServiceRequestNotFound(this);

        return Ok(SupplierServiceRequestMapper.ToDetailDto(request));
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
        if (orgId is null) return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        var rangeFrom = from ?? TimeProvider.System.TodayInRomeAsDateOnly();
        var rangeTo = to ?? rangeFrom.AddDays(13);

        if (rangeTo < rangeFrom)
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "SupplierAvailabilityRangeInvalid");

        const int maxRangeDays = 90;
        if (rangeTo.DayNumber - rangeFrom.DayNumber > maxRangeDays)
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "SupplierAvailabilityRangeTooLong", maxRangeDays);

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
        if (orgId is null) return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

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
        if (orgId is null) return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        var stats = await supplierService.GetDashboardStatsAsync(orgId.Value, cancellationToken);
        if (stats is null) return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");
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
        if (orgId is null) return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        var profile = await supplierService.GetProfileAsync(orgId.Value, cancellationToken);
        if (profile is null) return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        const int maxPhotos = 10;
        var existingUrls = JsonSerializer.Deserialize<List<string>>(profile.PhotoUrlsJson, JsonOpts) ?? [];
        if (existingUrls.Count + photos.Count > maxPhotos)
            return this.ApiProblem(
                StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "SupplierPhotoLimit",
                maxPhotos, existingUrls.Count, photos.Count);

        var uploadedUrls = new List<string>();
        foreach (var photo in photos)
        {
            if (!imageStorageService.ValidateImage(photo))
            {
                logger.LogWarning("Invalid supplier photo rejected: {FileName}", photo.FileName);
                // The file name is the caller's own text: shown back to them, never logged beyond the warning above.
                return this.ApiProblem(
                    StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "SupplierPhotoInvalid",
                    Path.GetFileName(photo.FileName));
            }

            try
            {
                var url = await imageStorageService.UploadSupplierPhotoAsync(photo, orgId.Value);
                uploadedUrls.Add(url);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to upload supplier photo for org {OrgId}", orgId.Value);
                return this.ApiProblem(StatusCodes.Status500InternalServerError, ProblemCodes.ServerError, "SupplierPhotoUploadFailed");
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
        if (orgId is null) return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        var profile = await supplierService.GetProfileAsync(orgId.Value, cancellationToken);
        if (profile is null) return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

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
        if (orgId is null) return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

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

        if (profile is null) return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

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
        if (orgId is null) return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        var (profile, queue) = await calendarSyncService.RequestSyncAsync(orgId.Value, cancellationToken);
        if (profile is null) return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

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

    private async Task<SupplierProfileDto> MapProfileAsync(
        Casazen.Core.Entities.SupplierProfile profile,
        CancellationToken cancellationToken)
    {
        var codes = SupplierComuniView.IstatCodes(profile);
        var listed = await comuneDirectory.GetByIstatCodesAsync(codes, cancellationToken);
        var dto = MapProfile(profile);
        dto.ComuneIstatCodes = codes;
        dto.OperatingComuni = codes.Where(listed.ContainsKey).Select(c => ComuneDto.From(listed[c])).ToList();
        return dto;
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
