using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.GuestPortal;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/properties/{propertyId:guid}/guest-portal-setup")]
[Authorize(Policy = CasazenPolicies.GuestPortalManage)]
[FeatureGate(FeatureFlags.GuestPortal)]
[PrivateAnswer]
public sealed class GuestPortalSetupController(
    AppDbContext db, IHostResourceLookup resources, IAuthorizationService authorization, GuestHouseGuideStorage guides, GuestPortalSetupSuggestions suggestions)
    : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid propertyId, CancellationToken ct)
    {
        var scope = await resources.ForPropertyAsync(propertyId, ct);
        if (scope is null || !await authorization.IsAuthorizedAsync(User, scope, PropertyOperations.Write))
            return NotFound();

        var property = await db.Properties.Where(p => p.Id == propertyId && p.OrgId == scope.OrgId)
            .Where(PropertyRentalModeRules.IsShortRent)
            .Select(p => new { p.GuestPortalSetupJson, p.GuestHouseGuideJson })
            .SingleOrDefaultAsync(ct);
        if (property is null)
            return NotFound();

        var org = await db.Orgs.SingleAsync(o => o.Id == scope.OrgId, ct);
        return Ok(View(propertyId, GuestPortalSetup.Read(property.GuestPortalSetupJson), property.GuestHouseGuideJson,
            GuestPortalBranding.From(org)));
    }

    [HttpPost("suggestions/{kind}")]
    public async Task<IActionResult> Suggest(Guid propertyId, string kind, CancellationToken ct, [FromQuery] bool fast = false)
    {
        var scope = await resources.ForPropertyAsync(propertyId, ct);
        if (scope is null || !await authorization.IsAuthorizedAsync(User, scope, PropertyOperations.Write))
            return NotFound();

        var property = await db.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == propertyId && p.OrgId == scope.OrgId, ct);
        if (property is null)
            return NotFound();

        var result = await suggestions.GenerateAsync(property, kind, ct, fast);
        return result is null ? NoContent() : Ok(result);
    }

    [HttpPut("{step}")]
    public async Task<IActionResult> Save(Guid propertyId, string step, SetupChoice request, CancellationToken ct)
    {
        var scope = await resources.ForPropertyAsync(propertyId, ct);
        if (scope is null || !await authorization.IsAuthorizedAsync(User, scope, PropertyOperations.Write))
            return NotFound();

        var query = db.Properties.Where(p => p.Id == propertyId && p.OrgId == scope.OrgId)
            .Where(PropertyRentalModeRules.IsShortRent);
        var property = await query.Select(p => new { p.GuestPortalSetupJson, p.GuestHouseGuideJson }).SingleOrDefaultAsync(ct);
        if (property is null)
            return NotFound();

        var setup = GuestPortalSetup.Read(property.GuestPortalSetupJson);
        var next = step switch
        {
            "brand" => setup with { BrandConfirmed = true },
            "guide" when HasGuide(propertyId, property.GuestHouseGuideJson) => setup with { GuideConfirmed = true },
            "extras" when request.Choice is true => setup with { ExtrasConfirmed = true },
            "suppliers" when request.Choice is { } value => setup with { RegionalSuppliers = value },
            "chat" when request.Choice is true => setup with { ChatConfirmed = true },
            "reviews" when request.Choice is { } value => setup with { ReviewsEnabled = value },
            "ota" when request.Choice is { } value => setup with { HidePaidServicesOnAirbnb = value, OtaRulesAcknowledged = true },
            _ => null,
        };
        if (next is null)
            return BadRequest();

        // A step cannot overwrite an update saved by a concurrent editor.
        var changed = await query.Where(p => p.GuestPortalSetupJson == property.GuestPortalSetupJson)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.GuestPortalSetupJson, next.Write()), ct);
        if (changed == 0)
            return Conflict();

        var org = await db.Orgs.SingleAsync(o => o.Id == scope.OrgId, ct);
        return Ok(View(propertyId, next, property.GuestHouseGuideJson, GuestPortalBranding.From(org)));
    }

    private object View(Guid propertyId, GuestPortalSetup setup, string? guide, GuestPortalBranding brand)
    {
        var hasGuide = HasGuide(propertyId, guide);
        return new { setup, brand, hasGuide, complete = setup.IsComplete(hasGuide) };
    }

    private bool HasGuide(Guid propertyId, string? value)
    {
        var guide = guides.Read(propertyId, value);
        return new[] { guide.Arrival, guide.House, guide.Departure, guide.Contacts, guide.LocalTips, guide.Access, guide.Wifi }
            .Any(text => !string.IsNullOrWhiteSpace(text));
    }

    public sealed record SetupChoice(bool? Choice);
}
