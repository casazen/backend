using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/marketing/guests/{profileId:guid}/categories")]
[FeatureGate(FeatureFlags.CrmMarketing)]
[Authorize(Policy = CasazenPolicies.Marketing)]
public sealed class MarketingGuestCategoriesController(MarketingGuestCategoryService categories, IOrgContextResolver organizations,
    IHostScopeResolver scopes) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<MarketingCategories>> Get(Guid profileId, CancellationToken ct) =>
        await ScopeAsync(ct) is not { } scope ? Forbid() : Ok(await categories.ReadAsync(scope, profileId, ct));

    [HttpPut]
    public async Task<IActionResult> Edit(Guid profileId, MarketingCategoryEdit edit, CancellationToken ct)
    {
        if (await ScopeAsync(ct) is not { } scope) return Forbid();
        await categories.EditAsync(scope, profileId, edit, ct);
        return NoContent();
    }

    [HttpPost("suggest")]
    [AiRateLimit]
    public async Task<IActionResult> Suggest(Guid profileId, CancellationToken ct) =>
        await ScopeAsync(ct) is not { } scope ? Forbid() : Ok(new { suggestedInterests = await categories.SuggestAsync(scope, profileId, ct) });

    private async Task<HostScope?> ScopeAsync(CancellationToken ct) =>
        await organizations.GetOrProvisionOrgIdAsync(ct) is { } orgId ? await scopes.ResolveHostScopeAsync(User, orgId, ct) : null;
}
