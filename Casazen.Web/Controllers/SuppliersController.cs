using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
    IUserAuthorizationCache authorizationCache,
    AppDbContext db,
    IOrgContextResolver orgContextResolver,
    IAuthorizationService authorizationService,
    ILogger<SuppliersController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Public self-serve supplier registration. Optionally validates an admin invite token.
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.PublicRegistration)]
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
            var userId = User.GetUserId();

            if (userId is not null)
            {
                var authenticatedEmail = User.FindFirstValue("email")
                    ?? User.FindFirstValue(ClaimTypes.Email);

                if (!string.IsNullOrWhiteSpace(authenticatedEmail)
                    && !EmailsMatch(authenticatedEmail, request.Email))
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

            logger.LogInformation(
                "Supplier registered: {OrgId} for {MaskedEmail}", org.Id, LogRedaction.MaskEmail(request.Email));

            // Registration is the moment the supplier link is created: assign the Supplier role in
            // Auth0 once, here (additive — host/admin roles are preserved). The outcome is returned
            // instead of being swallowed; backend supplier access already works through the DB link.
            Auth0SyncResult? roleSync = null;
            if (userId is not null)
            {
                authorizationCache.Invalidate(userId);
                roleSync = await auth0Management.AssignRoleAsync(userId, UserRole.Supplier, cancellationToken);
            }

            return CreatedAtAction(nameof(Register), new SupplierRegisterResponse
            {
                OrgId = org.Id,
                AuthRedirectUrl = "/supplier/activation",
                RolesSynced = roleSync?.Succeeded == true,
                RolesSyncError = roleSync is { Succeeded: false } ? roleSync.ErrorCode : null,
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
    /// Returns <c>Active</c> suppliers for a comune or property. Hosts only (<c>property.read</c>, TN-3); with
    /// <c>propertyId</c> the property is authorized as a <see cref="HostResource"/>. <c>category</c> is a code of
    /// <c>GET /api/service-categories</c>: only suppliers that declared it are returned, an unknown code is a 422
    /// <c>invalid_service_category</c> (SU-03).
    /// </summary>
    [HttpGet]
    [Authorize(Policy = CasazenPolicies.PropertyRead)]
    [ProducesResponseType(typeof(PagedResultDto<SupplierPickerDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PagedResultDto<SupplierPickerDto>>> GetSuppliers(
        [FromQuery] GetSuppliersQuery query,
        CancellationToken cancellationToken)
    {
        var resolvedComune = query.Comune?.Trim();

        if (query.PropertyId is Guid pid)
        {
            var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
            if (orgId is null)
                return Unauthorized();

            var property = await db.Properties
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == pid && p.OrgId == orgId.Value, cancellationToken);

            if (property is null)
                return NotFound(new { error = "Proprietà non trovata." });

            if (!await authorizationService.IsAuthorizedAsync(User, HostResource.ForProperty(property), PropertyOperations.Read))
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
}
