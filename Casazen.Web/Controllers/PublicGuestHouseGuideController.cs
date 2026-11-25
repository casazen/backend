using Casazen.Core.Features;
using Casazen.Core.GuestPortal;
using Casazen.Infrastructure.Data;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/public/welcome/{token}/guide")]
[AllowAnonymous]
[FeatureGate(FeatureFlags.GuestPortal)]
[EnableRateLimiting(RateLimitPolicies.PublicMarketingConsent)]
[PrivateAnswer]
public sealed class PublicGuestHouseGuideController(AppDbContext db, GuestHouseGuideStorage guides) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(string token, CancellationToken ct)
    {
        if (!GuestPortalRules.IsWellFormedToken(token))
            return NotFound();

        var property = await (
            from link in db.PropertyWelcomeLinks.IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            join home in db.Properties.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]) on link.PropertyId equals home.Id
            where link.Token == token && link.RevokedAt == null && home.OrgId == link.OrgId && home.Org.IsActive && home.IsActive
            select new { home.Id, home.GuestHouseGuideJson, home.HouseRules, home.Org.Slug })
            .SingleOrDefaultAsync(ct);
        if (property is null)
            return NotFound();

        return Ok(new
        {
            guide = guides.Read(property.Id, property.GuestHouseGuideJson).ForVisitor(false),
            property.HouseRules,
            orgSlug = property.Slug
        });
    }
}
