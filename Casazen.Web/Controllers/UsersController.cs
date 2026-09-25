using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Core.Validation;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Users;
using Casazen.Web.Infrastructure;
using Casazen.Web.Mapping;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class UsersController(
    IUserService userService,
    IOrgService orgService,
    IOnboardingService onboardingService,
    IRequestTenantContext tenantContext,
    ILogger<UsersController> logger,
    IEntitlementService entitlementService,
    IHostOnboardingGate hostOnboardingGate) : ControllerBase
{
    /// <summary>
    /// 422 on <c>PUT /api/users/onboarding</c> from a user without an org and without consents: the first org is
    /// created only together with the legal consents. The client answers it with the consents step (A1-01).
    /// </summary>
    public const string ConsentsRequiredCode = "consents_required";

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
        return Ok(await ToOwnDetailAsync(user));
    }

    /// <summary>Completes first-time onboarding by assigning roles from rental type choice.</summary>
    [HttpPost("onboarding")]
    public Task<ActionResult<OnboardingResponseDto>> PostOnboarding([FromBody] OnboardingRequestDto dto) =>
        CompleteOnboardingAsync(dto, requireConsents: true);

    /// <summary>
    /// Updates rental type and Auth0 roles (idempotent re-onboarding). A user who has no org yet (for example Auth0
    /// roles assigned by hand) gets the org here too, provided the request carries the consents.
    /// </summary>
    [HttpPut("onboarding")]
    public Task<ActionResult<OnboardingResponseDto>> PutOnboarding([FromBody] OnboardingRequestDto dto) =>
        CompleteOnboardingAsync(dto, requireConsents: false);

    /// <summary>
    /// Records where the caller's signup came from (SE-03): UTM parameters, comune of the SEO page, landing path and
    /// referrer host, sent by the web app after the first onboarding. Idempotent: only the first attribution of the org
    /// is kept (<c>recorded: false</c> afterwards). 422 <c>signup_attribution_onboarding_required</c> before the
    /// onboarding, 400 <c>validation_error</c> for a value outside the rules (nothing stored).
    /// </summary>
    [HttpPost("me/signup-attribution")]
    [ProducesResponseType(typeof(SignupAttributionResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SignupAttributionResultDto>> RecordSignupAttribution(
        [FromBody] SignupAttributionRequestDto dto,
        [FromServices] ISignupAttributionService signupAttributionService,
        CancellationToken cancellationToken)
    {
        var sub = GetSub();
        if (sub == null)
            return Unauthorized();

        var recorded = await signupAttributionService.RecordAsync(sub, dto.ToInput(), cancellationToken);
        return Ok(new SignupAttributionResultDto { Recorded = recorded });
    }

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
        return Ok(await ToOwnDetailAsync(updated));
    }

    /// <summary>Changes the role of a user. Admin only.</summary>
    [HttpPut("{id}/role")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult> ChangeRole(string id, [FromBody] ChangeRoleDto dto)
    {
        // Enum.TryParse alone also accepts a numeric string with no declared member (e.g. "99"), which would
        // otherwise reach Auth0 sync and persistence as an undefined role (A1-35).
        if (!EnumNames.TryParseDefined<UserRole>(dto.Role, out var newRole))
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "UserRoleUnknown");

        var adminSub = GetSub();
        if (adminSub == null)
            return Unauthorized();

        Auth0SyncResult sync;
        try
        {
            sync = await userService.ChangeRoleAsync(id, newRole, adminSub);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }

        if (!sync.Succeeded)
        {
            // The CasaZen role is unchanged: Auth0 is the source of the JWT roles, so a role change
            // that cannot reach it must not be reported as done. Retrying is safe (idempotent).
            return this.ApiProblem(
                StatusCodes.Status502BadGateway,
                sync.ErrorCode ?? Auth0SyncResult.ApiErrorCode,
                "Auth0RoleSyncFailed");
        }

        return Ok(new { id, role = newRole.ToString(), rolesSynced = true });
    }

    /// <summary>
    /// Deactivates a user (soft delete, PL-03). Admin only. From the next request the API refuses the user with 403
    /// <c>account_inactive</c>; Auth0 blocks the account and loses its roles (<c>auth0Synced</c> tells whether it did).
    /// 422 <c>cannot_deactivate_self</c> for the caller's own account, <c>last_active_admin</c> for the last active admin.
    /// </summary>
    [HttpDelete("{id}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<UserActivationResponseDto>> Deactivate(
        string id,
        [FromServices] IStringLocalizer<SharedResources> localizer)
    {
        var adminSub = GetSub();
        if (adminSub == null)
            return Unauthorized();

        var result = await userService.DeactivateUserAsync(id, adminSub, HttpContext.RequestAborted);
        return Ok(ToActivationResponse(
            result,
            localizer[result.Auth0Sync.Succeeded ? "UserDeactivated" : "UserDeactivatedAuth0NotSynced"]));
    }

    /// <summary>
    /// Reactivates a deactivated user (PL-03). Admin only. Auth0 gets back the roles removed by the deactivation, then the
    /// account is unblocked; when Auth0 fails the user stays unable to log in until the call is repeated.
    /// </summary>
    [HttpPost("{id}/reactivate")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<UserActivationResponseDto>> Reactivate(
        string id,
        [FromServices] IStringLocalizer<SharedResources> localizer)
    {
        var adminSub = GetSub();
        if (adminSub == null)
            return Unauthorized();

        var result = await userService.ReactivateUserAsync(id, adminSub, HttpContext.RequestAborted);
        return Ok(ToActivationResponse(
            result,
            localizer[result.Auth0Sync.Succeeded ? "UserReactivated" : "UserReactivatedAuth0NotSynced"]));
    }

    private async Task<ActionResult<OnboardingResponseDto>> CompleteOnboardingAsync(
        OnboardingRequestDto dto,
        bool requireConsents)
    {
        // Enum.TryParse alone also accepts a numeric string with no declared member (e.g. "7"), which then reached
        // MapRentalTypeToRoles as a value nothing was written to handle: ArgumentOutOfRangeException -> 500 instead
        // of a clean 400 (A1-35, A7-31).
        if (!EnumNames.TryParseDefined<RentalType>(dto.RentalType, out var rentalType))
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "RentalTypeUnknown");

        // The requested plan is only validated: the org starts on Starter, paid tiers come from Stripe (#274).
        if (!string.IsNullOrWhiteSpace(dto.PlanTier) && !PlanCatalog.TryParseTier(dto.PlanTier, out _))
            return BadRequest(new { error = $"Unknown planTier: {dto.PlanTier}" });

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

        var existingUser = await userService.GetUserAsync(sub);
        var hadOrg = existingUser?.OrgId.HasValue == true;

        // The first org of a user is created only together with the consents (PLG-AC2), whatever the verb. A PUT
        // from a user with Auth0 roles but no org (roles assigned by hand, org never provisioned) is not refused:
        // with the consents it creates or links the org like the POST, without them it gets a stable code the
        // client answers with the consents step (A1-01).
        var consentsRequired = requireConsents || !hadOrg;
        if (!requireConsents && !hadOrg && dto.Consents is null)
        {
            return this.ApiProblem(
                StatusCodes.Status422UnprocessableEntity,
                ConsentsRequiredCode,
                "OnboardingConsentsRequired");
        }

        var consentInput = dto.Consents.ToInput();
        var (validationSuccess, validationError) = onboardingService.ValidateConsents(consentInput, consentsRequired);
        if (!validationSuccess)
            return ToConsentError(validationError);

        var (user, rolesAssigned, roleSync) = await userService.CompleteOnboardingAsync(
            sub, rentalType, email, firstName, lastName);

        if (user.OrgId is not Guid orgId)
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Org provisioning failed." });

        // TN-4: the rest of this request (consent records, tenant filter) is scoped to the org just created or linked.
        tenantContext.SetOrgId(orgId);

        var (success, error, consentsRecorded) = await onboardingService.ValidateAndRecordConsentsAsync(
            sub,
            orgId,
            consentInput,
            consentsRequired,
            ClientIp.GetString(HttpContext),
            HttpContext.RequestAborted);

        if (!success)
            return ToConsentError(error);

        return Ok(new OnboardingResponseDto
        {
            RolesAssigned = rolesAssigned.ToArray(),
            RentalType = user.RentalType?.ToString() ?? rentalType.ToString(),
            OrgId = orgId,
            OrgProvisioned = !hadOrg,
            ConsentsRecorded = consentsRecorded,
            RolesSynced = roleSync.Succeeded,
            RolesSyncError = roleSync.Succeeded ? null : roleSync.ErrorCode,
        });
    }

    private ActionResult<OnboardingResponseDto> ToConsentError(ConsentValidationError? error) =>
        error?.Type switch
        {
            ConsentValidationErrorType.Incomplete => BadRequest(new { error = error.Message }),
            ConsentValidationErrorType.StaleVersion => BadRequest(new
            {
                error = error.Message,
                staleDocuments = error.StaleDocuments,
            }),
            _ => BadRequest(new { error = error?.Message ?? "Invalid consents." }),
        };

    // ─── Helpers ────────────────────────────────────────────────────────────

    private string? GetSub() =>
        User.FindFirst("sub")?.Value
        ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;

    private static UserActivationResponseDto ToActivationResponse(UserActivationResult result, string message) => new()
    {
        Id = result.User.Id,
        IsActive = result.User.IsActive,
        Changed = result.Changed,
        Auth0Synced = result.Auth0Sync.Succeeded,
        Auth0SyncError = result.Auth0Sync.Succeeded ? null : result.Auth0Sync.ErrorCode,
        Message = message,
        RolesRestored = result.RestoredRoles.Select(r => r.ToString()).ToList(),
    };

    private UserSummaryDto ToSummary(User u, Org? org = null) => new()
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
        PlanTier = org is null ? null : entitlementService.ResolveEffectiveTier(org).ToString(),
    };

    private async Task<Org?> ResolveOrgAsync(User user) =>
        user.OrgId.HasValue ? await orgService.GetByIdAsync(user.OrgId.Value) : null;

    /// <summary>The caller's own profile, with where it stands with the host onboarding gate (PL-02).</summary>
    private async Task<UserDetailDto> ToOwnDetailAsync(User user)
    {
        var detail = ToDetail(user, await ResolveOrgAsync(user));
        var onboarding = await hostOnboardingGate.GetStatusAsync(user.Id, HttpContext.RequestAborted);
        detail.OnboardingRequired = !onboarding.IsComplete;
        detail.ConsentsAccepted = onboarding.ConsentsAccepted;
        return detail;
    }

    private UserDetailDto ToDetail(User u, Org? org = null) => new()
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
        OnboardingCompletedAt = u.OnboardingCompletedAt,
        OrgId = u.OrgId,
        // A user whose only org is a supplier org registered before SupplierOrgId existed counts as linked too.
        SupplierOrgId = u.SupplierOrgId ?? (org?.OrgType == OrgType.Supplier ? org.Id : null),
        Org = org is null
            ? null
            : new OrgSummaryDto
            {
                Id = org.Id,
                Name = org.Name,
                Slug = org.Slug,
                PlanTier = entitlementService.ResolveEffectiveTier(org).ToString()
            }
    };
}
