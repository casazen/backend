using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "PropertyOwner")]
public class GuestsController(IGuestService guestService, ILogger<GuestsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Guest>>> GetAll([FromQuery] string? search)
    {
        logger.LogInformation("Retrieving guests with search term: {SearchTerm}", search ?? "none");

        var guests = string.IsNullOrWhiteSpace(search)
            ? await guestService.GetAllGuestsAsync()
            : await guestService.SearchGuestsAsync(search);

        return Ok(guests);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Guest>> GetById(Guid id)
    {
        logger.LogInformation("Retrieving guest: {GuestId}", id);

        var guest = await guestService.GetGuestAsync(id);
        if (guest == null)
        {
            logger.LogWarning("Guest not found: {GuestId}", id);
            return NotFound(new { message = $"Guest with ID {id} not found" });
        }

        return Ok(guest);
    }

    [HttpGet("email/{email}")]
    public async Task<ActionResult<Guest>> GetByEmail(string email)
    {
        logger.LogInformation("Retrieving guest by email: {Email}", email);

        var guest = await guestService.GetGuestByEmailAsync(email);
        if (guest == null)
        {
            logger.LogWarning("Guest not found with email: {Email}", email);
            return NotFound(new { message = $"Guest with email {email} not found" });
        }

        return Ok(guest);
    }

    [HttpPost]
    public async Task<ActionResult<Guest>> Create([FromBody] CreateGuestRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        logger.LogInformation("Creating guest with email: {Email}", request.Email);

        var guest = new Guest
        {
            FirstName = request.FirstName,
            LastName = request.LastName,
            Email = request.Email,
            PhoneNumber = request.PhoneNumber ?? string.Empty,
            Address = request.Address ?? string.Empty,
            City = request.City ?? string.Empty,
            PostalCode = request.PostalCode ?? string.Empty,
            Country = request.Country ?? string.Empty,
            Notes = request.Notes ?? string.Empty,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        try
        {
            var created = await guestService.CreateGuestAsync(guest);
            logger.LogInformation("Guest created: {GuestId}", created.Id);
            return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Failed to create guest: {Message}", ex.Message);
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<Guest>> Update(Guid id, [FromBody] UpdateGuestRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        logger.LogInformation("Updating guest: {GuestId}", id);

        var existing = await guestService.GetGuestAsync(id);
        if (existing == null)
        {
            logger.LogWarning("Guest not found: {GuestId}", id);
            return NotFound(new { message = $"Guest with ID {id} not found" });
        }

        existing.FirstName = request.FirstName;
        existing.LastName = request.LastName;
        existing.Email = request.Email;
        existing.PhoneNumber = request.PhoneNumber ?? string.Empty;
        existing.Address = request.Address ?? string.Empty;
        existing.City = request.City ?? string.Empty;
        existing.PostalCode = request.PostalCode ?? string.Empty;
        existing.Country = request.Country ?? string.Empty;
        existing.Notes = request.Notes ?? string.Empty;

        try
        {
            var updated = await guestService.UpdateGuestAsync(existing);
            logger.LogInformation("Guest updated: {GuestId}", updated.Id);
            return Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Failed to update guest: {Message}", ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        logger.LogInformation("Deleting guest: {GuestId}", id);

        var result = await guestService.DeleteGuestAsync(id);
        if (!result)
        {
            logger.LogWarning("Guest not found: {GuestId}", id);
            return NotFound(new { message = $"Guest with ID {id} not found" });
        }

        logger.LogInformation("Guest deleted: {GuestId}", id);
        return NoContent();
    }
}
