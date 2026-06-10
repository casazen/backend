using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class UsersController(
    IUserService userService,
    IOrgService orgService,
    ILogger<UsersController> logger) : ControllerBase
{
    // ─── Admin endpoints ────────────────────────────────────────────────────

    /// <summary>Returns a paginated, filtered list of all users. Admin only.</summary>
    [HttpGet]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<PagedResultDto<UserSummaryDto>>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? role = null,
        [FromQuery] bool? isActive = null,
        [FromQuery] string? search = null)
    {
        pageSize = Math.Min(pageSize, 100);
        logger.LogInformation("Admin: listing users page={Page} pageSize={PageSize}", page, pageSize);
        var (users, total) = await userService.GetPagedAsync(search, role, isActive, page, pageSize);
        var userList = users.ToList();
        await userService.EnrichUsersFromAuth0Async(userList);
        var orgIds = userList.Where(u => u.OrgId.HasValue).Select(u => u.OrgId!.Value);
        var orgMap = await orgService.GetByIdsAsync(orgIds, HttpContext.RequestAborted);

        return Ok(new PagedResultDto<UserSummaryDto>
        {
            Items = userList.Select(u => ToSummary(u, u.OrgId is Guid id && orgMap.TryGetValue(id, out var org) ? org : null)),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        });
    }

    /// <summary>Returns a single user by ID. Admin only.</summary>
    [HttpGet("{id}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<UserDetailDto>> GetById(string id)
    {
        var user = await userService.GetUserAsync(id);
        if (user == null)
            return NotFound();

        return Ok(ToDetail(user, await ResolveOrgAsync(user)));
    }

    // ─── Self-service endpoints ──────────────────────────────────────────────

    /// <summary>
    /// Returns the caller's own profile. Creates the DB record on first call (upsert by sub).
    /// </summary>
    [HttpGet("me")]
    public async Task<ActionResult<UserDetailDto>> GetMe()
    {
        var sub = GetSub();
        if (sub == null)
            return Unauthorized();

        // Extract name/email claims from JWT; fall back gracefully
        var email = User.FindFirst("email")?.Value
                        ?? User.FindFirst(ClaimTypes.Email)?.Value
                        ?? string.Empty;
        var firstName = User.FindFirst("given_name")?.Value
                        ?? User.FindFirst("name")?.Value?.Split(' ').FirstOrDefault()
                        ?? string.Empty;
        var lastName = User.FindFirst("family_name")?.Value
                        ?? User.FindFirst("name")?.Value?.Split(' ').Skip(1).FirstOrDefault()
                        ?? string.Empty;

        var user = await userService.GetCurrentUserAsync(sub, email, firstName, lastName);
        return Ok(ToDetail(user, await ResolveOrgAsync(user)));
    }

    /// <summary>Completes first-time onboarding by assigning roles from rental type choice.</summary>
    [HttpPost("onboarding")]
    public Task<ActionResult<OnboardingResponseDto>> PostOnboarding([FromBody] OnboardingRequestDto dto) =>
        CompleteOnboardingAsync(dto);

    /// <summary>Updates rental type and Auth0 roles (idempotent re-onboarding).</summary>
    [HttpPut("onboarding")]
    public Task<ActionResult<OnboardingResponseDto>> PutOnboarding([FromBody] OnboardingRequestDto dto) =>
        CompleteOnboardingAsync(dto);

    /// <summary>Updates the caller's own profile (first name, last name, phone).</summary>
    [HttpPut("me")]
    public async Task<ActionResult<UserDetailDto>> UpdateMe([FromBody] UpdateProfileDto dto)
    {
        var sub = GetSub();
        if (sub == null)
            return Unauthorized();

        var existing = await userService.GetUserAsync(sub);
        if (existing == null)
            return NotFound();

        if (dto.FirstName != null)
            existing.FirstName = dto.FirstName;
        if (dto.LastName != null)
            existing.LastName = dto.LastName;
        if (dto.PhoneNumber != null)
            existing.PhoneNumber = dto.PhoneNumber;

        var updated = await userService.UpdateUserAsync(existing);
        return Ok(ToDetail(updated, await ResolveOrgAsync(updated)));
    }

    /// <summary>Changes the role of a user. Admin only.</summary>
    [HttpPut("{id}/role")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult> ChangeRole(string id, [FromBody] ChangeRoleDto dto)
    {
        if (!Enum.TryParse<UserRole>(dto.Role, ignoreCase: true, out var newRole))
            return BadRequest(new { error = $"Unknown role: {dto.Role}" });

        var adminSub = GetSub();
        if (adminSub == null)
            return Unauthorized();

        try
        {
            await userService.ChangeRoleAsync(id, newRole, adminSub);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }

        return Ok(new { id, role = dto.Role });
    }

    /// <summary>Soft-deletes a user (sets IsActive = false). Admin only. Cannot self-delete.</summary>
    [HttpDelete("{id}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult> Delete(string id)
    {
        var adminSub = GetSub();
        if (adminSub == null)
            return Unauthorized();

        if (id == adminSub)
            return BadRequest(new { error = "Admins cannot deactivate their own account" });

        var deleted = await userService.DeleteUserAsync(id);
        if (!deleted)
            return NotFound();

        return NoContent();
    }

    private async Task<ActionResult<OnboardingResponseDto>> CompleteOnboardingAsync(OnboardingRequestDto dto)
    {
        if (!Enum.TryParse<RentalType>(dto.RentalType, ignoreCase: true, out var rentalType))
            return BadRequest(new { error = $"Unknown rentalType: {dto.RentalType}" });

        var planTier = PlanTier.Starter;
        if (!string.IsNullOrWhiteSpace(dto.PlanTier))
        {
            if (!PlanCatalog.TryParseTier(dto.PlanTier, out planTier))
                return BadRequest(new { error = $"Unknown planTier: {dto.PlanTier}" });
        }

        var sub = GetSub();
        if (sub == null)
            return Unauthorized();

        var email = User.FindFirst("email")?.Value
                    ?? User.FindFirst(ClaimTypes.Email)?.Value
                    ?? string.Empty;
        var firstName = User.FindFirst("given_name")?.Value
                        ?? User.FindFirst("name")?.Value?.Split(' ').FirstOrDefault()
                        ?? string.Empty;
        var lastName = User.FindFirst("family_name")?.Value
                       ?? User.FindFirst("name")?.Value?.Split(' ').Skip(1).FirstOrDefault()
                       ?? string.Empty;

        var (user, rolesAssigned) = await userService.CompleteOnboardingAsync(
            sub, rentalType, planTier, email, firstName, lastName);

        return Ok(new OnboardingResponseDto
        {
            RolesAssigned = rolesAssigned.ToArray(),
            RentalType = user.RentalType?.ToString() ?? rentalType.ToString()
        });
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private string? GetSub() =>
        User.FindFirst("sub")?.Value
        ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;

    private static UserSummaryDto ToSummary(User u, Org? org = null) => new()
    {
        Id = u.Id,
        Email = u.Email,
        FirstName = u.FirstName,
        LastName = u.LastName,
        Role = u.Role.ToString(),
        RentalType = u.RentalType?.ToString(),
        IsActive = u.IsActive,
        CreatedAt = u.CreatedAt,
        OrgId = u.OrgId,
        OrgName = org?.Name,
        PlanTier = org?.PlanTier.ToString(),
    };

    private async Task<Org?> ResolveOrgAsync(User user) =>
        user.OrgId.HasValue ? await orgService.GetByIdAsync(user.OrgId.Value) : null;

    private static UserDetailDto ToDetail(User u, Org? org = null) => new()
    {
        Id = u.Id,
        Email = u.Email,
        FirstName = u.FirstName,
        LastName = u.LastName,
        Role = u.Role.ToString(),
        RentalType = u.RentalType?.ToString(),
        IsActive = u.IsActive,
        CreatedAt = u.CreatedAt,
        PhoneNumber = u.PhoneNumber,
        UpdatedAt = u.UpdatedAt,
        OrgId = u.OrgId,
        Org = org is null
            ? null
            : new OrgSummaryDto
            {
                Id = org.Id,
                Name = org.Name,
                Slug = org.Slug,
                PlanTier = org.PlanTier.ToString()
            }
    };
}
