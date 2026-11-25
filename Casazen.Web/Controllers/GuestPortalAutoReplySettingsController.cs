using System.ComponentModel.DataAnnotations;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/guest-portal/auto-reply-settings")]
[Authorize(Policy = CasazenPolicies.GuestPortalManage)]
[FeatureGate(FeatureFlags.GuestPortal)]
[FeatureGate(FeatureFlags.GuestPortalAutoReplies)]
[PrivateAnswer]
public sealed class GuestPortalAutoReplySettingsController(
    IOrgContextResolver orgs, IAssistantRulesService rules, IAuthorizationService authorization) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        if (await orgs.GetOrProvisionOrgIdAsync(cancellationToken) is not { } orgId)
            return Forbid();

        var current = await rules.GetAsync(orgId, cancellationToken);
        var canManage = await authorization.AuthorizeAsync(User, CasazenPolicies.OrgBillingAdmin);
        return Ok(new { current.Enabled, CanManage = canManage.Succeeded });
    }

    [HttpPut]
    [Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
    public async Task<IActionResult> Set(Settings request, CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { Length: > 0 } userId
            || await orgs.GetOrProvisionOrgIdAsync(cancellationToken) is not { } orgId)
        {
            return Forbid();
        }

        await rules.SetEnabledAsync(orgId, request.Enabled!.Value, userId, cancellationToken);
        return NoContent();
    }

    public sealed record Settings([property: Required] bool? Enabled);
}
