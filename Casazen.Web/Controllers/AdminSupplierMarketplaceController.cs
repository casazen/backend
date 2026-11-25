using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/admin/suppliers/marketplace")]
[Route("api/admin/guest-supplier-orders/marketplace")]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
[PrivateAnswer]
public sealed class AdminSupplierMarketplaceController(AppDbContext db, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Marketplace([FromQuery] string? search, CancellationToken ct)
    {
        var term = search?.Trim().ToLowerInvariant();
        if (term is not { Length: >= 2 and <= 100 })
            return BadRequest();

        var suppliers = await db.SupplierProfiles.AsNoTracking()
            .Where(x => x.Status == SupplierStatus.Active && x.LegalName.ToLower().Contains(term))
            .OrderBy(x => x.LegalName).Take(30)
            .Select(x => new { x.OrgId, x.LegalName, Enabled = x.GuestPortalMarketplaceEnabled }).ToListAsync(ct);
        return Ok(suppliers);
    }

    [HttpPut("{supplierId:guid}")]
    public async Task<IActionResult> Marketplace(Guid supplierId, MarketplaceChoice request, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var updated = await db.SupplierProfiles
            .Where(x => x.OrgId == supplierId && (!request.Enabled || x.Status == SupplierStatus.Active))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.GuestPortalMarketplaceEnabled, request.Enabled)
                .SetProperty(x => x.GuestPortalMarketplaceFirstPublishedAt,
                    x => x.GuestPortalMarketplaceFirstPublishedAt ?? (request.Enabled ? (DateTime?)now : null)),
                ct);
        return updated == 0 ? NotFound() : NoContent();
    }

    public sealed record MarketplaceChoice(bool Enabled);
}
