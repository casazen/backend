using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

/// <summary>
/// Public supplier registration + host-facing supplier discovery endpoints (US-022 / #292).
/// </summary>
[ApiController]
[Route("api/suppliers")]
public class SuppliersController(
    ISupplierService supplierService,
    IAuth0ManagementService auth0Management,
    AppDbContext db,
    IOrgContextResolver orgContextResolver,
    IPropertyAuthorizationService propertyAuthorization,
    ILogger<SuppliersController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Public self-serve supplier registration. Optionally validates an admin invite token.
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(SupplierRegisterResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SupplierRegisterResponse>> Register(
        [FromBody] SupplierRegisterRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            // Resolve the authenticated user's sub claim if present (the endpoint is
            // [AllowAnonymous] but the JWT may be valid if the user logged in via Auth0
            // before submitting the registration form). Linking User.OrgId at registration
            // time prevents duplicate auto-provisioning on first supplier endpoint access.
            var userId = User.FindFirstValue("sub")
                ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? User.FindFirstValue("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier");

            var (org, _) = await supplierService.RegisterAsync(
                request.Email,
                request.LegalName,
                request.Phone,
                request.ComuneCode,
                request.InviteToken,
                userId,
                cancellationToken);

            logger.LogInformation("Supplier registered: {OrgId} for {Email}", org.Id, request.Email);

            // Fire-and-forget: assign the Supplier role in Auth0 so the user can access
            // supplier endpoints after completing Auth0 signup. Silently skips if the
            // Management API token is not configured.
            if (userId is not null)
            {
                _ = auth0Management.AssignRoleAsync(userId, UserRole.Supplier);
            }

            return CreatedAtAction(nameof(Register), new SupplierRegisterResponse
            {
                OrgId = org.Id,
                AuthRedirectUrl = "/supplier/activation",
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Returns <c>Active</c> suppliers for a comune or property. Available to hosts (PropertyOwner role).
    /// </summary>
    [HttpGet]
    [Authorize]
    [ProducesResponseType(typeof(PagedResultDto<SupplierPickerDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResultDto<SupplierPickerDto>>> GetSuppliers(
        [FromQuery] GetSuppliersQuery query,
        CancellationToken cancellationToken)
    {
        var resolvedComune = query.Comune?.Trim();

        if (query.PropertyId is Guid pid)
        {
            var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
            var userId = GetUserId();
            if (orgId is null || userId is null)
                return Unauthorized();

            var property = await db.Properties
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == pid && p.OrgId == orgId.Value, cancellationToken);

            if (property is null)
                return NotFound(new { error = "Proprietà non trovata." });

            if (!await propertyAuthorization.CanAccessPropertyAsync(
                    userId, pid, ["PropertyOwner", "Admin", "PropertyManager"]))
                return Forbid();

            resolvedComune = property.City;
        }

        if (string.IsNullOrWhiteSpace(resolvedComune))
            return BadRequest(new { error = "Specificare comune o propertyId." });

        var suppliers = await supplierService.GetActiveByComune(resolvedComune, query.Category, cancellationToken);

        var items = suppliers.Select(sp => new SupplierPickerDto
        {
            OrgId = sp.OrgId,
            LegalName = sp.LegalName,
            Phone = sp.Phone,
            Email = sp.Email,
            Categories = JsonSerializer.Deserialize<IEnumerable<string>>(sp.CategoriesJson, JsonOpts) ?? [],
            Comuni = JsonSerializer.Deserialize<IEnumerable<string>>(sp.ComuniJson, JsonOpts) ?? [],
            Bio = sp.Bio,
            PhotoUrls = JsonSerializer.Deserialize<IEnumerable<string>>(sp.PhotoUrlsJson, JsonOpts) ?? [],
        });

        return Ok(new PagedResultDto<SupplierPickerDto>
        {
            Items = items,
            TotalCount = suppliers.Count,
            Page = 1,
            PageSize = suppliers.Count,
        });
    }

    private string? GetUserId() =>
        User.FindFirstValue("sub")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.FindFirstValue("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier");
}
