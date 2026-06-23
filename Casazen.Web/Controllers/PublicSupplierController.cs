using System.Text.Json;
using Casazen.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/public/suppliers")]
[AllowAnonymous]
public class PublicSupplierController : ControllerBase
{
    private readonly AppDbContext _db;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public PublicSupplierController(AppDbContext db) => _db = db;

    [HttpGet("{slug}")]
    public async Task<ActionResult> GetBySlug(string slug, CancellationToken ct)
    {
        var profile = await _db.SupplierProfiles
            .FirstOrDefaultAsync(sp => sp.ShowcaseSlug == slug && sp.Status == Core.Entities.Enums.SupplierStatus.Active, ct);

        if (profile is null)
            return NotFound(new { error = "Supplier not found" });

        var availability = await _db.SupplierAvailability
            .Where(sa => sa.OrgId == profile.OrgId && sa.Date >= DateOnly.FromDateTime(DateTime.UtcNow))
            .OrderBy(sa => sa.Date)
            .Take(14)
            .Select(sa => new { sa.Date, sa.Available })
            .ToListAsync(ct);

        return Ok(new
        {
            slug = profile.ShowcaseSlug,
            legalName = profile.LegalName,
            categories = JsonSerializer.Deserialize<string[]>(profile.CategoriesJson, JsonOpts) ?? [],
            comuni = JsonSerializer.Deserialize<string[]>(profile.ComuniJson, JsonOpts) ?? [],
            bio = profile.Bio,
            photoUrls = JsonSerializer.Deserialize<string[]>(profile.PhotoUrlsJson, JsonOpts) ?? [],
            calendarSyncType = profile.CalendarSyncType.ToString(),
            availability,
        });
    }
}
