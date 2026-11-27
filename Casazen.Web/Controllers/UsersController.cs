using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Core.Validation;
using Casazen.Infrastructure.Email;
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
    IHostOnboardingGate hostOnboardingGate,
    PublicSiteLinks publicSiteLinks,
    Casazen.Infrastructure.Data.AppDbContext? commercialDb = null) : ControllerBase
{
    public const string ConsentsRequiredCode = "consents_required";
    public const string ConsentsIncompleteCode = "consents_incomplete";
    public const string StaleDocumentsCode = "stale_documents";

    [HttpGet]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<PagedResultDto<UserSummaryDto>>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? role = null,
        [FromQuery] bool? isActive = null,
        [FromQuery] string? search = null)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
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

    [HttpGet("{id}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<UserDetailDto>> GetById(string id)
    {
        var user = await userService.GetUserAsync(id);
        if (user == null)
            return NotFound();

        return Ok(ToDetail(user, await ResolveOrgAsync(user)));
    }

    [HttpGet("me")]
    public async Task<ActionResult<UserDetailDto>> GetMe(
        [FromServices] IAccountEmailResolver accountEmails,
        CancellationToken cancellationToken)
    {
        var sub = GetSub();
        if (sub == null)
            return Unauthorized();

        var (email, firstName, lastName) = AccountEmailResolver.ReadProfile(User);

        var user = await userService.GetCurrentUserAsync(sub, email, firstName, lastName);
        return Ok(await ToOwnDetailAsync(user, accountEmails, cancellationToken));
    }

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

    [HttpPut("me")]
    public async Task<ActionResult<UserDetailDto>> UpdateMe(
        [FromBody] UpdateProfileDto dto,
        [FromServices] IAccountEmailResolver accountEmails,
        CancellationToken cancellationToken)
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
        return Ok(await ToOwnDetailAsync(updated, accountEmails, cancellationToken));
    }

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

    [HttpGet("{id}/roles")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<UserRolesDto>> GetRoles(string id)
    {
        Auth0UserRolesResult result;
        try
        {
            result = await userService.GetRolesAsync(id, HttpContext.RequestAborted);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }

        if (!result.Sync.Succeeded)
        {
            return this.ApiProblem(
                StatusCodes.Status502BadGateway,
                result.Sync.ErrorCode ?? Auth0SyncResult.ApiErrorCode,
                "Auth0RoleSyncFailed");
        }

        return Ok(new UserRolesDto
        {
            Id = id,
            Roles = result.Roles.Where(AdminManageableRoles.All.Contains).Select(r => r.ToString()).ToList(),
        });
    }

    /// <summary>
    /// Grants and revokes roles so the user's Auth0 roles match <paramref name="dto"/> exactly, among the roles an
    /// admin can manage (A1-17: Admin, PropertyOwner, LongTermLandlord, Supplier — every other current role of the
    /// user, e.g. Guest from a booking, is left untouched). Admin only.
    /// </summary>
    [HttpPut("{id}/roles")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<UserRolesDto>> UpdateRoles(string id, [FromBody] UpdateUserRolesDto dto)
    {
        var roles = new List<UserRole>();
        foreach (var name in dto.Roles.Distinct())
        {
            if (!EnumNames.TryParseDefined<UserRole>(name, out var role) || !AdminManageableRoles.All.Contains(role))
                return this.ApiProblem(
                    StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "UserRoleNotManageable", name);
            roles.Add(role);
        }

        var adminSub = GetSub();
        if (adminSub == null)
            return Unauthorized();

        RoleSetUpdateResult result;
        try
        {
            result = await userService.UpdateRolesAsync(id, roles, adminSub, HttpContext.RequestAborted);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }

        if (!result.RoleSync.Succeeded)
        {
            return this.ApiProblem(
                StatusCodes.Status502BadGateway,
                result.RoleSync.ErrorCode ?? Auth0SyncResult.ApiErrorCode,
                "Auth0RoleSyncFailed");
        }

        return Ok(new UserRolesDto
        {
            Id = id,
            Roles = result.Roles.Select(r => r.ToString()).ToList(),
            RolesGranted = result.RolesGranted.Select(r => r.ToString()).ToList(),
            RolesRevoked = result.RolesRevoked.Select(r => r.ToString()).ToList(),
        });
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

    /// <summary>
    /// Permanently removes a registered account. Admin only. Auth0 is deleted first (<c>delete:users</c>); when that
    /// fails the CasaZen row stays and the call answers 502 so the admin can retry. 422 <c>cannot_delete_self</c> for
    /// the caller's own account, <c>cannot_delete_last_admin</c> for the last active admin. The organization and its
    /// properties stay.
    /// </summary>
    [HttpDelete("{id}/account")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> DeleteAccount(string id)
    {
        var adminSub = GetSub();
        if (adminSub == null)
            return Unauthorized();

        var sync = await userService.DeleteRegisteredUserAsync(id, adminSub, HttpContext.RequestAborted);
        if (!sync.Succeeded)
        {
            return this.ApiProblem(
                StatusCodes.Status502BadGateway,
                sync.ErrorCode ?? Auth0SyncResult.ApiErrorCode,
                "UserDeleteAuth0Failed");
        }

        return NoContent();
    }

    private async Task<ActionResult<OnboardingResponseDto>> CompleteOnboardingAsync(
        OnboardingRequestDto dto,
        bool requireConsents)
    {
        if (!EnumNames.TryParseDefined<RentalType>(dto.RentalType, out var rentalType))
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "RentalTypeUnknown");

        if (!string.IsNullOrWhiteSpace(dto.PlanTier) && !PlanCatalog.TryParseTier(dto.PlanTier, out _))
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "BillingPlanTierUnknown");

        if (dto.CommercialSignup is { } commercial &&
            (commercial.Validate() is not null || commercial.RentalType != dto.RentalType))
            return BadRequest(new { code = "commercial_selection_invalid" });

        var sub = GetSub();
        if (sub == null)
            return Unauthorized();

        var (email, firstName, lastName) = AccountEmailResolver.ReadProfile(User);

        var existingUser = await userService.GetUserAsync(sub);
        var hadOrg = existingUser?.OrgId.HasValue == true;
        var hadHostOrg = hadOrg;
        if (existingUser?.OrgId is Guid existingOrgId && commercialDb is not null)
        {
            var existingOrg = await commercialDb.Orgs.FindAsync([existingOrgId], HttpContext.RequestAborted);
            hadHostOrg = existingOrg?.OrgType != OrgType.Supplier;
            if (existingOrg?.CommercialSignupJson is not null && (!requireConsents || dto.RentalType != existingUser.RentalType?.ToString()))
            {
                var authorization = HttpContext.RequestServices.GetRequiredService<IAuthorizationService>();
                var allowed = await authorization.AuthorizeAsync(User, null, Casazen.Web.Authorization.CasazenPolicies.OrgBillingAdmin);
                if (!allowed.Succeeded) return Forbid();
            }
        }


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

        var initialSelection = dto.CommercialSignup ?? (!hadHostOrg
            ? new CommercialSignup { Profile = rentalType == RentalType.LongTerm ? "long-rent" : "owner", RentalArea = rentalType == RentalType.Both ? "Both" : null }
            : null);

        var (user, rolesAssigned, roleSync) = await userService.CompleteOnboardingAsync(
            sub, rentalType, email, firstName, lastName);

        if (user.OrgId is not Guid orgId)
            return this.ApiProblem(StatusCodes.Status500InternalServerError, ProblemCodes.InternalError, "InternalServerErrorDetail");

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

        if (initialSelection is { } selection && commercialDb is not null)
        {
            var commercialOrg = await commercialDb.Orgs.FindAsync([orgId], HttpContext.RequestAborted);
            if (commercialOrg is not null && !BillingSubscriptionPolicy.BlocksNewCheckout(commercialOrg))
            {
                var savedCommercial = CommercialSignup.Read(commercialOrg.CommercialSignupJson);
                if (savedCommercial is not null && savedCommercial.Fingerprint != selection.Fingerprint)
                    return Conflict(new { code = "commercial_selection_changed" });
                commercialOrg.CommercialSignupJson = selection.Serialize();
                if (selection.Profile == "agency")
                {
                    commercialOrg.IsAgency = true;
                    commercialOrg.Name = selection.AgencyName!.Trim();
                    commercialOrg.DisplayName = commercialOrg.Name;
                }
                await commercialDb.SaveChangesAsync(HttpContext.RequestAborted);
            }
        }

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
            ConsentValidationErrorType.StaleVersion => StaleDocumentsProblem(error),
            _ => this.ApiProblem(StatusCodes.Status400BadRequest, ConsentsIncompleteCode, "ConsentsIncomplete"),
        };

    private ObjectResult StaleDocumentsProblem(ConsentValidationError error)
    {
        var problem = ApiProblemDetails.Create(HttpContext, StatusCodes.Status400BadRequest, StaleDocumentsCode, "ConsentsStale");
        problem.Extensions["staleDocuments"] = error.StaleDocuments ?? [];
        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { ApiProblemDetails.ContentType },
        };
    }

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

    private async Task<UserDetailDto> ToOwnDetailAsync(
        User user,
        IAccountEmailResolver accountEmails,
        CancellationToken cancellationToken)
    {
        var org = await ResolveOrgAsync(user);
        var detail = ToDetail(user, org);
        var onboarding = await hostOnboardingGate.GetStatusAsync(user.Id, HttpContext.RequestAborted);
        detail.OnboardingRequired = !onboarding.IsComplete;
        detail.ConsentsAccepted = onboarding.ConsentsAccepted;
        if (user.OrgId is Guid orgId && org?.OrgType != OrgType.Supplier)
            detail.HasProperties = await onboardingService.OrgHasPropertiesAsync(orgId, HttpContext.RequestAborted);
        detail.CommercialEnabledAddons = System.Text.Json.JsonSerializer.Deserialize<string[]>(org?.CommercialPaidAddonsJson ?? "[]") ?? [];
        detail.CommercialSignup = CommercialSignup.Read(org?.CommercialSignupJson);
        detail.CommercialSetupRequired = detail.CommercialSignup is not null &&
            (org?.CommercialPaymentConfirmed != true ||
             org.SubscriptionStatus is not (SubscriptionStatus.Active or SubscriptionStatus.Trialing or SubscriptionStatus.PastDue));
        detail.EmailVerified = (await accountEmails.ResolveAsync(User, user.Id, cancellationToken)).EmailVerified;
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
        SupplierOrgId = u.SupplierOrgId ?? (org?.OrgType == OrgType.Supplier ? org.Id : null),
        Org = org is null
            ? null
            : new OrgSummaryDto
            {
                Id = org.Id,
                Name = org.Name,
                IsAgency = org.IsAgency || org.PlanTier == PlanTier.Agency,
                Slug = org.Slug,
                PublicSiteUrl = string.IsNullOrWhiteSpace(org.Slug)
                    ? null
                    : publicSiteLinks.TryPublicPage($"/book/{Uri.EscapeDataString(org.Slug)}"),
                PlanTier = entitlementService.ResolveEffectiveTier(org).ToString()
            }
    };
}
