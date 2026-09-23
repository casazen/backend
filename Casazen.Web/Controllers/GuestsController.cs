using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Guests of the caller's org (TN-1). Every action is scoped to the caller's org: a guest of another
/// org answers 404, exactly like a missing one. Responses are DTOs, never the entity.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "PropertyOwner")]
[Authorize(Policy = "RequireContext:short-rent:guest.read")]
public class GuestsController(
    IGuestService guestService,
    IOrgContextResolver orgContextResolver,
    ILogger<GuestsController> logger) : ControllerBase
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    [HttpGet]
    public async Task<ActionResult<PagedResultDto<GuestSummaryDto>>> GetAll(
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize)
    {
        logger.LogInformation("Retrieving guests with search term: {HasSearch}", !string.IsNullOrWhiteSpace(search));

        var orgId = await ResolveOrgIdAsync();
        if (orgId is null)
            return Forbid();

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var (guests, total) = await guestService.GetGuestsPageAsync(
            orgId.Value, search, page, pageSize, HttpContext.RequestAborted);

        return Ok(new PagedResultDto<GuestSummaryDto>
        {
            Items = guests.Select(GuestDtoMapper.ToSummary).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<GuestDto>> GetById(Guid id)
    {
        logger.LogInformation("Retrieving guest: {GuestId}", id);

        var guest = await FindGuestInOrgAsync(id);
        if (guest is null)
            return GuestNotFound();

        return Ok(GuestDtoMapper.ToDto(guest));
    }

    [HttpGet("email/{email}")]
    public async Task<ActionResult<GuestDto>> GetByEmail(string email)
    {
        logger.LogInformation("Retrieving guest by email lookup");

        var orgId = await ResolveOrgIdAsync();
        if (orgId is null)
            return GuestNotFound();

        var guest = await guestService.GetGuestByEmailAsync(orgId.Value, email, HttpContext.RequestAborted);
        if (guest is null)
        {
            logger.LogWarning("Guest not found for email lookup");
            return GuestNotFound();
        }

        return Ok(GuestDtoMapper.ToDto(guest));
    }

    [HttpPost]
    [Authorize(Policy = "RequireContext:short-rent:guest.write")]
    public async Task<ActionResult<GuestDto>> Create([FromBody] CreateGuestRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        logger.LogInformation("Creating guest");

        var orgId = await ResolveOrgIdAsync();
        if (orgId is null)
            return Forbid();

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

        // A duplicate e-mail in the same org raises DomainConflictException (409 guest_email_exists);
        // the same e-mail in another org is a different guest and never surfaces here.
        var created = await guestService.CreateGuestAsync(orgId.Value, guest, HttpContext.RequestAborted);
        logger.LogInformation("Guest created: {GuestId}", created.Id);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, GuestDtoMapper.ToDto(created));
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "RequireContext:short-rent:guest.write")]
    public async Task<ActionResult<GuestDto>> Update(Guid id, [FromBody] UpdateGuestRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        logger.LogInformation("Updating guest: {GuestId}", id);

        var existing = await FindGuestInOrgAsync(id);
        if (existing is null)
        {
            logger.LogWarning("Guest not found: {GuestId}", id);
            return GuestNotFound();
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

        var updated = await guestService.UpdateGuestAsync(existing);
        logger.LogInformation("Guest updated: {GuestId}", updated.Id);
        return Ok(GuestDtoMapper.ToDto(updated));
    }

    /// <summary>
    /// Deletes a guest of the caller's org. Without bookings the row is removed; with past bookings it is
    /// kept (Restrict FK) but marked deleted and anonymized; with an open booking the answer is
    /// 409 <c>guest_has_open_bookings</c>. Never a 500 for the foreign key (A9-11).
    /// </summary>
    [HttpDelete("{id}")]
    [Authorize(Policy = "RequireContext:short-rent:guest.write")]
    public async Task<IActionResult> Delete(Guid id)
    {
        logger.LogInformation("Deleting guest: {GuestId}", id);

        var orgId = await ResolveOrgIdAsync();
        if (orgId is null)
            return GuestNotFound();

        var result = await guestService.DeleteGuestAsync(orgId.Value, id, HttpContext.RequestAborted);
        logger.LogInformation("Guest {GuestId} deletion completed: {Result}", id, result);
        return NoContent();
    }

    private Task<Guid?> ResolveOrgIdAsync() =>
        orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);

    private async Task<Guest?> FindGuestInOrgAsync(Guid guestId)
    {
        var orgId = await ResolveOrgIdAsync();
        if (orgId is null)
            return null;

        return await guestService.GetGuestAsync(orgId.Value, guestId, HttpContext.RequestAborted);
    }

    private ObjectResult GuestNotFound() =>
        this.ApiProblem(StatusCodes.Status404NotFound, "guest_not_found", "GuestNotFound");
}
