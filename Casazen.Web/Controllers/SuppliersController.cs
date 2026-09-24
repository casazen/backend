using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Controllers;

/// <summary>
/// Public supplier registration + host-facing supplier discovery endpoints (US-022 / #292).
/// </summary>
[ApiController]
[Route("api/suppliers")]
public class SuppliersController(
    ISupplierService supplierService,
    IUserService userService,
    IAuth0ManagementService auth0Management,
    IUserAuthorizationCache authorizationCache,
    IOptions<SupplierRegistrationOptions> registrationOptions,
    AppDbContext db,
    IOrgContextResolver orgContextResolver,
    IAuthorizationService authorizationService,
    ILogger<SuppliersController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Supplier registration (SU-01). Without <c>inviteToken</c> it is self-serve: anonymous or signed in, only for a
    /// configured pilot comune (<c>Suppliers:PilotComuni</c>; none configured = self-serve off). With
    /// <c>inviteToken</c> the caller must be signed in (web app Auth0 login) with the invited email, and the comune is
    /// the invited one; the invite is then used up. A signed-in caller is linked to the new org and gets the Auth0
    /// <c>Supplier</c> role (additive). Errors: 422 with the codes of <see cref="ISupplierService.RegisterAsync"/>,
    /// 429 <c>rate_limited</c>.
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.PublicRegistration)]
    [ProducesResponseType(typeof(SupplierRegisterResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<SupplierRegisterResponse>> Register(
        [FromBody] SupplierRegisterRequest request,
        CancellationToken cancellationToken)
    {
        // The endpoint is anonymous, but a signed-in caller (bearer token sent by the web app) registers for their own
        // account: the account email is checked against the form and the invite, and the user is linked at once.
        var userId = User.GetUserId();
        string? accountEmail = null;
        if (userId is not null)
        {
            // Access token claim first; without it (Auth0 Action not deployed yet) ask Auth0 itself, never trust a
            // stored copy for a check that binds the invite to the account.
            accountEmail = GetAccountEmail(User);
            if (accountEmail is null)
            {
                var profile = await auth0Management.GetUserProfileAsync(userId);
                accountEmail = string.IsNullOrWhiteSpace(profile?.Email) ? null : profile.Email.Trim();
            }

            // The registration page is outside the app shell, so /api/users/me may not have run yet: create the
            // caller's User row now, otherwise the new org could not be linked to it.
            if (accountEmail is not null)
                await userService.GetCurrentUserAsync(userId, accountEmail, string.Empty, string.Empty);
        }

        var (org, _) = await supplierService.RegisterAsync(
            new SupplierRegistration(
                request.Email,
                request.LegalName,
                request.Phone,
                request.ComuneCode,
                request.InviteToken,
                userId,
                accountEmail),
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

    /// <summary>
    /// The invite of a link token (email, comune, categories, expiry), to pre-fill and lock the registration page.
    /// The token is in the body so it never appears in URLs or request logs. 422 <c>supplier_invite_invalid</c>
    /// (malformed or unknown), <c>supplier_invite_expired</c>, <c>supplier_invite_used</c>.
    /// </summary>
    [HttpPost("invites/lookup")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.PublicRead)]
    [ProducesResponseType(typeof(SupplierInviteLookupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<SupplierInviteLookupResponse>> LookupInvite(
        [FromBody] SupplierInviteLookupRequest request,
        CancellationToken cancellationToken)
    {
        var invite = await supplierService.GetInviteAsync(request.Token, cancellationToken);

        return Ok(new SupplierInviteLookupResponse
        {
            Email = invite.Email,
            ComuneCode = invite.ComuneCode,
            ComuneName = invite.ComuneName,
            Categories = invite.Categories,
            ExpiresAt = invite.ExpiresAt,
        });
    }

    /// <summary>
    /// Whether suppliers can register without an invite, and for which pilot comuni (<c>Suppliers:PilotComuni</c>).
    /// </summary>
    [HttpGet("registration-options")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.PublicRead)]
    [ProducesResponseType(typeof(SupplierRegistrationOptionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public ActionResult<SupplierRegistrationOptionsResponse> GetRegistrationOptions()
    {
        var options = registrationOptions.Value;
        return Ok(new SupplierRegistrationOptionsResponse
        {
            SelfServeEnabled = options.SelfServeEnabled,
            PilotComuni = options.PilotComuni
                .Select(c => new SupplierPilotComuneDto { Code = c.Code.Trim(), Name = c.Name.Trim() })
                .ToList(),
        });
    }

    /// <summary>
    /// Email of the signed-in account: the Auth0 Action claim (<c>https://casazen.app/email</c>, runbook auth0.md §6),
    /// else the standard claims.
    /// </summary>
    private static string? GetAccountEmail(ClaimsPrincipal user) =>
        new[] { AccountEmailClaim, "email", ClaimTypes.Email }
            .Select(user.FindFirstValue)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?.Trim();

    private const string AccountEmailClaim = "https://casazen.app/email";

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
