using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.GuestPortal;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/properties/{propertyId:guid}/guest-house-guide")]
[Authorize(Policy = CasazenPolicies.GuestPortalManage)]
[FeatureGate(FeatureFlags.GuestPortal)]
[PrivateAnswer]
public sealed class GuestHouseGuideController(
    AppDbContext db, IHostResourceLookup resources, IAuthorizationService authorization, GuestHouseGuideStorage guides) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid propertyId, CancellationToken ct)
    {
        var scope = await resources.ForPropertyAsync(propertyId, ct);
        if (scope is null || !await authorization.IsAuthorizedAsync(User, scope, PropertyOperations.Write))
            return NotFound();

        var json = await db.Properties.Where(p => p.Id == propertyId && p.OrgId == scope.OrgId)
            .Select(p => p.GuestHouseGuideJson).SingleAsync(ct);
        return Ok(guides.Read(propertyId, json));
    }

    [HttpPut]
    [RequestSizeLimit(65_536)]
    public async Task<IActionResult> Put(Guid propertyId, GuestHouseGuide guide, CancellationToken ct)
    {
        var scope = await resources.ForPropertyAsync(propertyId, ct);
        if (scope is null || !await authorization.IsAuthorizedAsync(User, scope, PropertyOperations.Write))
            return NotFound();

        var json = guides.Protect(propertyId, guide);
        var changed = await db.Properties.Where(p => p.Id == propertyId && p.OrgId == scope.OrgId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.GuestHouseGuideJson, json), ct);
        return changed == 1 ? NoContent() : NotFound();
    }
}
