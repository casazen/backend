using Casazen.Core.Authorization;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Rights of the guests on their data, exercised by the host for them (CO-15, docs/runbooks/gdpr.md), and the org's
/// fiscal data. Guest actions: guest of the caller's org only (another org's guest answers 404, TN-1), then the
/// <c>guest.read</c> / <c>guest.write</c> permission on that org (TN-3, otherwise 403). The class carries no permission
/// because the org actions need <c>property.*</c> instead: every action has its own policy.
/// </summary>
[ApiController]
[Route("api/gdpr")]
[Authorize]
public class GdprController(
    IGdprService gdprService,
    IOrgContextResolver orgContextResolver,
    IGuestAccessService guestAccessService,
    IAuthorizationService authorizationService,
    ILogger<GdprController> logger) : ControllerBase
{
    /// <summary>Consents with their versions, retention per category and status of the guest's data.</summary>
    [HttpGet("guests/{id:guid}")]
    [Authorize(Policy = CasazenPolicies.GuestRead)]
    [ProducesResponseType(typeof(GuestPrivacySummary), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetGuestPrivacy(Guid id)
    {
        if (await ResolveGuestOrgAsync(id) is not { } orgId)
            return NotFound();
        if (!await IsAllowedAsync(orgId, GuestOperations.Read))
            return Forbid();

        return Ok(await gdprService.GetGuestPrivacySummaryAsync(orgId, id, HttpContext.RequestAborted));
    }

    /// <summary>
    /// Complete, versioned export of the guest's data (art. 15 and 20 GDPR) with the identity document in clear: audited,
    /// never cached.
    /// </summary>
    [HttpGet("guests/{id:guid}/export")]
    [Authorize(Policy = CasazenPolicies.GuestRead)]
    [ProducesResponseType(typeof(GuestDataExport), StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportGuestData(Guid id)
    {
        if (await ResolveGuestOrgAsync(id) is not { } orgId)
            return NotFound();
        if (!await IsAllowedAsync(orgId, GuestOperations.Read))
            return Forbid();

        logger.LogInformation("GDPR data export requested for guest {GuestId} by user {UserId}", id, User.GetUserId());
        var export = await gdprService.ExportGuestDataAsync(orgId, id, User.GetUserId(), HttpContext.RequestAborted);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(export);
    }

    /// <summary>
    /// Erasure (art. 17 GDPR): 204, also when already erased; 409 <c>guest_has_open_bookings</c> while a stay is open.
    /// The reason is stored on the record (up to 500 characters): never put personal data in it.
    /// </summary>
    [HttpDelete("guests/{id:guid}")]
    [Authorize(Policy = CasazenPolicies.GuestWrite)]
    public async Task<IActionResult> DeleteGuestData(Guid id, [FromQuery] string reason = "User request")
    {
        if (await ResolveGuestOrgAsync(id) is not { } orgId)
            return NotFound();
        if (!await IsAllowedAsync(orgId, GuestOperations.Write))
            return Forbid();

        logger.LogInformation("GDPR deletion requested for guest {GuestId} by user {UserId}", id, User.GetUserId());
        await gdprService.EraseGuestDataAsync(orgId, id, reason, User.GetUserId(), HttpContext.RequestAborted);
        return NoContent();
    }

    /// <summary>Anonymization without deletion: 204, also when already anonymized; 409 while a stay is open.</summary>
    [HttpPost("guests/{id:guid}/anonymize")]
    [Authorize(Policy = CasazenPolicies.GuestWrite)]
    public async Task<IActionResult> AnonymizeGuestData(Guid id)
    {
        if (await ResolveGuestOrgAsync(id) is not { } orgId)
            return NotFound();
        if (!await IsAllowedAsync(orgId, GuestOperations.Write))
            return Forbid();

        logger.LogInformation("GDPR anonymization requested for guest {GuestId} by user {UserId}", id, User.GetUserId());
        await gdprService.AnonymizeGuestDataAsync(orgId, id, User.GetUserId(), HttpContext.RequestAborted);
        return NoContent();
    }

    /// <summary>
    /// Marketing consent (A5-14): the host can only withdraw it, on the guest's documented request (<c>note</c>).
    /// <c>marketingConsent: true</c> answers 422 <c>gdpr_marketing_consent_host_grant_forbidden</c>; a withdrawal without
    /// note 422 <c>gdpr_marketing_withdrawal_note_required</c>.
    /// </summary>
    [HttpPut("guests/{id:guid}/consent")]
    [Authorize(Policy = CasazenPolicies.GuestWrite)]
    public async Task<IActionResult> UpdateConsent(Guid id, [FromBody] UpdateConsentRequest request)
    {
        if (await ResolveGuestOrgAsync(id) is not { } orgId)
            return NotFound();
        if (!await IsAllowedAsync(orgId, GuestOperations.Write))
            return Forbid();

        await gdprService.UpdateMarketingConsentAsync(
            orgId, id, request.MarketingConsent, request.Note, User.GetUserId(), HttpContext.RequestAborted);
        return NoContent();
    }

    [HttpGet("org/export")]
    [Authorize(Policy = CasazenPolicies.PropertyRead)]
    public async Task<IActionResult> ExportOrgFiscal(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return NotFound();
        var data = await gdprService.ExportOrgFiscalDataAsync(orgId.Value, cancellationToken);
        return Ok(data);
    }

    [HttpPost("org/anonymize")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    public async Task<IActionResult> AnonymizeOrgFiscal(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return NotFound();
        await gdprService.AnonymizeOrgFiscalDataAsync(orgId.Value, cancellationToken);
        return NoContent();
    }

    /// <summary>The caller's org when the guest belongs to it (TN-1); null otherwise (answered as 404).</summary>
    private async Task<Guid?> ResolveGuestOrgAsync(Guid guestId)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return null;

        return await guestAccessService.IsGuestAccessibleAsync(guestId, orgId.Value, HttpContext.RequestAborted)
            ? orgId
            : null;
    }

    private async Task<bool> IsAllowedAsync(Guid orgId, HostOperationRequirement operation)
    {
        if (await authorizationService.IsAuthorizedAsync(User, HostResource.ForOrg(orgId), operation))
            return true;

        logger.LogWarning("User {UserId} denied GDPR operation {Permission} in org {OrgId}", User.GetUserId(), operation.PermissionKey, orgId);
        return false;
    }
}

/// <param name="MarketingConsent">Only <c>false</c> is accepted from the host (withdrawal).</param>
/// <param name="Note">The guest's request to withdraw (date, channel): required; stored up to 500 characters.</param>
public record UpdateConsentRequest(bool MarketingConsent, string? Note = null);
