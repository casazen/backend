using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "LongTermLandlord")]
public class LeasesController(ILeaseWorkflowService leaseService, ILogger<LeasesController> logger) : ControllerBase
{
    private string OwnerId => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.FindFirstValue("sub")
        ?? throw new UnauthorizedAccessException("Owner ID claim not found.");

    /// <summary>List all lease contracts for the authenticated owner.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] Guid? propertyId = null)
    {
        var leases = await leaseService.GetOwnerLeasesAsync(OwnerId, propertyId);
        return Ok(leases);
    }

    /// <summary>Get full lease contract detail including parties, registration, and events.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var lease = await leaseService.GetLeaseDetailAsync(id, OwnerId);
        return lease is null ? NotFound() : Ok(lease);
    }

    /// <summary>Create a new lease contract draft.</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateLeaseDto dto)
    {
        try
        {
            var request = new CreateLeaseRequest(
                dto.FiscalRegime,
                dto.StartDate,
                dto.EndDate,
                dto.MonthlyRent,
                dto.Parties.Select(p => new CreatePartyRequest(
                    p.Role, p.FirstName, p.LastName, p.FiscalCode, p.Citizenship, p.ContactEmail)));

            var lease = await leaseService.CreateDraftAsync(dto.PropertyId, OwnerId, request);
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
    public async Task<IActionResult> InitiateSigning(Guid id)
    {
        try
        {
            var result = await leaseService.InitiateSigningAsync(id, OwnerId);
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
    public async Task<IActionResult> TriggerRegistration(Guid id)
    {
        try
        {
            var registration = await leaseService.TriggerRegistrationAsync(id, OwnerId);
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
        var registration = await leaseService.GetRegistrationAsync(id, OwnerId);
        return registration is null ? NotFound() : Ok(registration);
    }

    /// <summary>Download the official RLI registration receipt (PDF).</summary>
    [HttpGet("{id:guid}/registration/receipt")]
    public async Task<IActionResult> GetReceipt(Guid id)
    {
        try
        {
            var stream = await leaseService.GetRegistrationReceiptAsync(id, OwnerId);
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
    Guid PropertyId,
    FiscalRegime FiscalRegime,
    DateTime StartDate,
    DateTime EndDate,
    decimal MonthlyRent,
    IEnumerable<CreatePartyDto> Parties);

public record CreatePartyDto(
    PartyRole Role,
    string FirstName,
    string LastName,
    string FiscalCode,
    string Citizenship,
    string ContactEmail);
