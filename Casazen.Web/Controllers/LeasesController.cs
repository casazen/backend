using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "LongTermLandlord")]
[Authorize(Policy = "RequireContext:long-rent:lease.read")]
public class LeasesController(ILeaseWorkflowService leaseService) : ControllerBase
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

    /// <summary>Submit a Signed lease to Openapi.it Docuengine for RLI registration (async).</summary>
    [HttpPost("{id:guid}/registration")]
    [Authorize(Policy = "RequireContext:long-rent:lease.register")]
    public async Task<IActionResult> TriggerRegistration(Guid id)
    {
        if (GetOwnerId() is not { } ownerId) return Unauthorized();
        try
        {
            var registration = await leaseService.TriggerRegistrationAsync(id, ownerId);
            return Accepted(new
            {
                leaseId = id,
                registrationStatus = registration.Status.ToString(),
                message = $"Registration submitted. Check GET /api/leases/{id}/registration for status."
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
}

public record CreateLeaseDto(
    [property: Required] Guid PropertyId,
    [property: Required] FiscalRegime FiscalRegime,
    [property: Required] DateTime StartDate,
    [property: Required] DateTime EndDate,
    [property: Range(0.01, 1_000_000.0)] decimal MonthlyRent,
    [property: Required, MinLength(1)] IEnumerable<CreatePartyDto> Parties);

public record CreatePartyDto(
    [property: Required] PartyRole Role,
    [property: Required, MaxLength(100), MinLength(1)] string FirstName,
    [property: Required, MaxLength(100), MinLength(1)] string LastName,
    [property: Required, MaxLength(16), MinLength(1)] string FiscalCode,
    [property: Required, MaxLength(2), MinLength(2)] string Citizenship,
    [property: Required, EmailAddress] string ContactEmail);
