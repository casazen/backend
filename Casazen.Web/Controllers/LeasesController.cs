using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "LongTermLandlord")]
[Authorize(Policy = "RequireContext:long-rent:lease.read")]
public class LeasesController(
    ILeaseWorkflowService leaseService,
    IComuneImuNotificationService imuNotification,
    ICedolareAdvisoryService cedolareAdvisory,
    IRliExportService rliExport,
    IRliChecklistService rliChecklist,
    IStringLocalizer<SharedResources> localizer) : ControllerBase
{
    private string? GetOwnerId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");

    /// <summary>List all lease contracts for the authenticated owner.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] Guid? propertyId = null)
    {
        if (GetOwnerId() is not { } ownerId) return Unauthorized();
        var leases = await leaseService.GetOwnerLeasesAsync(ownerId, propertyId);
        return Ok(leases);
    }

    /// <summary>Get full lease contract detail including parties, registration, and events.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        if (GetOwnerId() is not { } ownerId) return Unauthorized();
        var lease = await leaseService.GetLeaseDetailAsync(id, ownerId);
        return lease is null ? NotFound() : Ok(lease);
    }

    /// <summary>Create a new lease contract draft.</summary>
    [HttpPost]
    [Authorize(Policy = "RequireContext:long-rent:lease.create")]
    public async Task<IActionResult> Create([FromBody] CreateLeaseDto dto)
    {
        if (GetOwnerId() is not { } ownerId) return Unauthorized();
        try
        {
            var request = new CreateLeaseRequest(
                dto.FiscalRegime,
                dto.StartDate,
                dto.EndDate,
                dto.MonthlyRent,
                dto.Parties.Select(p => new CreatePartyRequest(
                    p.Role, p.FirstName, p.LastName, p.FiscalCode, p.Citizenship, p.ContactEmail)));

            var lease = await leaseService.CreateDraftAsync(dto.PropertyId, ownerId, request);
            return CreatedAtAction(nameof(GetById), new { id = lease.Id }, lease);
        }
        catch (ApeComplianceException ex)
        {
            var error = ex.Code == ApeComplianceException.InvalidContentCode
                ? localizer["ApeInvalidContent"].Value
                : ex.Message;
            return BadRequest(new { error, code = ex.Code });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    /// <summary>Generate PDF/A and initiate digital signing for a lease in Draft status.</summary>
    [HttpPost("{id:guid}/signing")]
    [Authorize(Policy = "RequireContext:long-rent:lease.sign")]
    public async Task<IActionResult> InitiateSigning(Guid id)
    {
        if (GetOwnerId() is not { } ownerId) return Unauthorized();
        try
        {
            var result = await leaseService.InitiateSigningAsync(id, ownerId);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    /// <summary>Submit a Signed lease to the filing channel after per-lease delega (async).</summary>
    [HttpPost("{id:guid}/registration")]
    [Authorize(Policy = "RequireContext:long-rent:lease.register")]
    public async Task<IActionResult> TriggerRegistration(Guid id, [FromBody] TriggerRegistrationDto dto)
    {
        if (GetOwnerId() is not { } ownerId) return Unauthorized();
        if (dto is null || !dto.AttestationAccepted)
            return BadRequest(new { error = localizer["RliDelegaRequired"].Value });
        try
        {
            var registration = await leaseService.TriggerRegistrationAsync(
                id, ownerId, new RegistrationAuthorizationRequest(dto.TosVersion, dto.AttestationAccepted));
            return Accepted(new
            {
                leaseId = id,
                registrationStatus = registration.Status.ToString(),
                message = localizer["RliRegistrationAccepted"].Value
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    [HttpGet("{id:guid}/rli/advisory")]
    public async Task<IActionResult> GetRliAdvisory(Guid id, CancellationToken cancellationToken)
    {
        if (GetOwnerId() is not { } ownerId) return Unauthorized();
        var result = await cedolareAdvisory.EvaluateAsync(id, ownerId, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("{id:guid}/rli/export")]
    [Authorize(Policy = "RequireContext:long-rent:lease.register")]
    public async Task<IActionResult> ExportRli(Guid id, CancellationToken cancellationToken)
    {
        if (GetOwnerId() is not { } ownerId) return Unauthorized();
        var result = await rliExport.ExportAsync(id, ownerId, cancellationToken);
        return result is null
            ? NotFound()
            : File(result.PdfBytes, "application/pdf", result.FileName);
    }

    [HttpGet("{id:guid}/rli/checklist")]
    public async Task<IActionResult> GetRliChecklist(Guid id, CancellationToken cancellationToken)
    {
        if (GetOwnerId() is not { } ownerId) return Unauthorized();
        var result = await rliChecklist.GetAsync(id, ownerId, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>Get current RLI registration status.</summary>
    [HttpGet("{id:guid}/registration")]
    public async Task<IActionResult> GetRegistration(Guid id)
    {
        if (GetOwnerId() is not { } ownerId) return Unauthorized();
        var registration = await leaseService.GetRegistrationAsync(id, ownerId);
        return registration is null ? NotFound() : Ok(registration);
    }

    /// <summary>Download the official RLI registration receipt (PDF).</summary>
    [HttpGet("{id:guid}/registration/receipt")]
    public async Task<IActionResult> GetReceipt(Guid id)
    {
        if (GetOwnerId() is not { } ownerId) return Unauthorized();
        try
        {
            var stream = await leaseService.GetRegistrationReceiptAsync(id, ownerId);
            return File(stream, "application/pdf", $"receipt-{id}.pdf");
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    /// <summary>Export a draft comune IMU-reduction notification (PDF). Landlord sends it themselves.</summary>
    [HttpGet("{id:guid}/canone-concordato/imu-notification/export")]
    public async Task<IActionResult> ExportImuNotification(Guid id, CancellationToken cancellationToken)
    {
        if (GetOwnerId() is not { } ownerId) return Unauthorized();
        try
        {
            var result = await imuNotification.ExportAsync(id, ownerId, cancellationToken);
            return result is null
                ? NotFound()
                : File(result.PdfBytes, "application/pdf", result.FileName);
        }
        catch (ImuNotificationNotReadyException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>Landlord attests they sent the IMU notification. Never inferred.</summary>
    [HttpPost("{id:guid}/canone-concordato/imu-notification/mark-sent")]
    [Authorize(Policy = "RequireContext:long-rent:lease.register")]
    public async Task<IActionResult> MarkImuNotificationSent(Guid id, CancellationToken cancellationToken)
    {
        if (GetOwnerId() is not { } ownerId) return Unauthorized();
        try
        {
            var result = await imuNotification.MarkSentAsync(id, ownerId, cancellationToken);
            return result is null ? NotFound() : NoContent();
        }
        catch (ImuNotificationNotReadyException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }
}

public record CreateLeaseDto(
    [param: Required] Guid PropertyId,
    [param: Required] FiscalRegime FiscalRegime,
    [param: Required] DateTime StartDate,
    [param: Required] DateTime EndDate,
    [param: Range(0.01, 1_000_000.0)] decimal MonthlyRent,
    [param: Required, MinLength(1)] IEnumerable<CreatePartyDto> Parties);

public record CreatePartyDto(
    [param: Required] PartyRole Role,
    [param: Required, MaxLength(100), MinLength(1)] string FirstName,
    [param: Required, MaxLength(100), MinLength(1)] string LastName,
    [param: Required, MaxLength(16), MinLength(1)] string FiscalCode,
    [param: Required, MaxLength(2), MinLength(2)] string Citizenship,
    [param: Required, EmailAddress] string ContactEmail);

public record TriggerRegistrationDto(
    [param: Required, MaxLength(80)] string TosVersion,
    [param: Required] bool AttestationAccepted);
