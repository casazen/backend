using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Supplier;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Public supplier registration + host-facing supplier discovery endpoints (US-022 / #292).
/// </summary>
[ApiController]
[Route("api/suppliers")]
public class SuppliersController(
    ISupplierService supplierService,
    IAuth0ManagementService auth0Management,
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

            if (userId is not null)
            {
                var authenticatedEmail = User.FindFirstValue("email")
                    ?? User.FindFirstValue(ClaimTypes.Email);

                if (!EmailsMatch(authenticatedEmail, request.Email))
                {
                    return BadRequest(new
                    {
                        error = "Authenticated email must match the supplier registration email.",
                    });
                }
            }

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

    private static bool EmailsMatch(string? authenticatedEmail, string requestEmail) =>
        !string.IsNullOrWhiteSpace(authenticatedEmail) &&
        string.Equals(
            authenticatedEmail.Trim(),
            requestEmail.Trim(),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <c>Active</c> suppliers for a comune. Available to hosts (PropertyOwner role).
    /// </summary>
    [HttpGet]
    [Authorize]
    [ProducesResponseType(typeof(PagedResultDto<SupplierPickerDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PagedResultDto<SupplierPickerDto>>> GetSuppliers(
        [FromQuery] string comune,
        [FromQuery] string? category,
        CancellationToken cancellationToken)
    {
        var suppliers = await supplierService.GetActiveByComune(comune, category, cancellationToken);

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
}
