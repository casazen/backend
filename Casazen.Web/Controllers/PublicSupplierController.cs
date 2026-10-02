using System.Text.Json;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/public/suppliers")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.PublicRead)]
public class PublicSupplierController : ControllerBase
{
    private readonly AppDbContext _db;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IComuneDirectory _comuneDirectory;

    public PublicSupplierController(AppDbContext db, IComuneDirectory comuneDirectory)
    {
        _db = db;
        _comuneDirectory = comuneDirectory;
    }

    [HttpGet("{slug}")]
    public async Task<ActionResult> GetBySlug(string slug, CancellationToken ct)
    {
        var profile = await _db.SupplierProfiles
            .FirstOrDefaultAsync(sp => sp.ShowcaseSlug == slug && sp.Status == Core.Entities.Enums.SupplierStatus.Active, ct);

        if (profile is null)
            return NotFound(new { error = "Supplier not found" });

        // The comuni chosen from the official list are shown by name (SU-04), then what the supplier wrote.
        var listed = await _comuneDirectory.GetByIstatCodesAsync(SupplierComuniView.IstatCodes(profile), ct);

        var today = TimeProvider.System.TodayInRomeAsDateOnly();
        var availability = await _db.SupplierAvailability
            .Where(sa => sa.OrgId == profile.OrgId && sa.Date >= today)
            .OrderBy(sa => sa.Date)
            .Take(14)
            .Select(sa => new { sa.Date, sa.Available })
            .ToListAsync(ct);

        return Ok(new
        {
            slug = profile.ShowcaseSlug,
            legalName = profile.LegalName,
            categories = JsonSerializer.Deserialize<string[]>(profile.CategoriesJson, JsonOpts) ?? [],
            comuni = SupplierComuniView.Names(profile, listed),
            bio = profile.Bio,
            photoUrls = JsonSerializer.Deserialize<string[]>(profile.PhotoUrlsJson, JsonOpts) ?? [],
            calendarSyncType = profile.CalendarSyncType.ToString(),
            availability,
        });
    }
}
