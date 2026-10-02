using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

/// <summary>Public showcase of an active supplier, behind <c>/fornitori/{slug}</c> of the web app (SU-13, A4-16).</summary>
[ApiController]
[Route("api/public/suppliers")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.PublicRead)]
public class PublicSupplierController(
    AppDbContext db,
    IComuneDirectory comuneDirectory,
    ISupplierService supplierService) : ControllerBase
{
    /// <summary>
    /// The showcase of the active supplier with this slug (looked up lowercase). 404 <c>not_found</c> for an unknown slug and
    /// for a supplier that is not active (pending or suspended): the page says "does not exist" for both. The answer carries
    /// <c>X-Robots-Tag: noindex</c>: the showcase is not indexable in v0 (as the page, see the runbook).
    /// </summary>
    [HttpGet("{slug}")]
    [ProducesResponseType(typeof(SupplierShowcaseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierShowcaseDto>> GetBySlug(string slug, CancellationToken ct)
    {
        Response.Headers["X-Robots-Tag"] = "noindex";

        var normalized = SupplierShowcaseSlug.Normalize(slug);
        var profile = await db.SupplierProfiles.AsNoTracking()
            .FirstOrDefaultAsync(sp => sp.ShowcaseSlug == normalized && sp.Status == SupplierStatus.Active, ct);

        if (profile is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierShowcaseNotFound");

        var listed = await comuneDirectory.GetByIstatCodesAsync(SupplierComuniView.IstatCodes(profile), ct);
        var today = TimeProvider.System.TodayInRomeAsDateOnly();
        var availability = await supplierService.GetAvailabilityAsync(profile.OrgId, today, today.AddDays(13), ct);

        return Ok(SupplierShowcaseMapper.ToDto(profile, listed, availability));
    }
}
