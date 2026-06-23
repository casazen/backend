using System.Text.Json;
using Casazen.Core.Services;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Supplier-facing endpoints: activation wizard, profile, inbox shell, and availability (US-022 / #292).
/// All routes are scoped to the caller's supplier org via <c>ISupplierOrgContextResolver</c>.
/// </summary>
[ApiController]
[Route("api/supplier")]
[Authorize(Policy = "RequireSupplier")]
public class SupplierProfileController(
    ISupplierService supplierService,
    ISupplierOrgContextResolver supplierOrgContextResolver,
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

    /// <summary>Updates the caller's supplier profile fields.</summary>
    [HttpPut("profile")]
    [ProducesResponseType(typeof(SupplierProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierProfileDto>> UpdateProfile(
        [FromBody] UpdateSupplierProfileRequest request,
        CancellationToken cancellationToken)
    {
        var orgId = await supplierOrgContextResolver.GetOrProvisionSupplierOrgIdAsync(cancellationToken);
        if (orgId is null) return NotFound(new { error = "No supplier org found" });

        var profile = await supplierService.UpdateProfileAsync(
            orgId.Value,
            request.LegalName, request.VatNumber, request.Phone,
            request.Categories, request.Comuni, request.Bio, request.PhotoUrls,
            cancellationToken);

        if (profile is null) return NotFound(new { error = "Supplier profile not found" });

        return Ok(MapProfile(profile));
    }

    // ─── Inbox ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns paginated open service requests assigned to this supplier.
    /// Returns empty list until #293 (micro-marketplace) is implemented.
    /// </summary>
    [HttpGet("inbox")]
    [ProducesResponseType(typeof(SupplierInboxResponse), StatusCodes.Status200OK)]
    public ActionResult<SupplierInboxResponse> GetInbox(
        [FromQuery] string? status = "open",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        logger.LogDebug("Supplier inbox requested — status={Status}, page={Page}", status, page);
        return Ok(new SupplierInboxResponse { Items = [], Total = 0 });
    }

    // ─── Availability ─────────────────────────────────────────────────────────

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

    // ─── Helpers ──────────────────────────────────────────────────────────────

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
