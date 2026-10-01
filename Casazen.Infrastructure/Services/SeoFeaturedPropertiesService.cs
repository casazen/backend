using Casazen.Core.Services;
using Casazen.Core.Validation;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc />
public sealed class SeoFeaturedPropertiesService(AppDbContext db) : ISeoFeaturedPropertiesService
{
    public async Task<SeoFeaturedPropertiesDto?> GetAsync(string comune, CancellationToken cancellationToken = default)
    {
        var info = SignupAttributionRules.ResolveComune(comune);
        if (info is null)
            return null;

        // Public data of published properties of every org, read for an anonymous visitor: the tenant filter is lifted
        // (as the public availability does) and the visibility is the one rule of the public site, PublicListing.IsPublished
        // (active, not paused, compliance activated), plus an active org: a disabled org's pages answer 404.
        var cityName = info.Name.ToLower();
        var rows = await db.Properties
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(PublicListing.IsPublished)
            .Where(p => p.City.ToLower() == cityName && p.Org.IsActive)
            .OrderByDescending(p => p.CreatedAt)
            .ThenBy(p => p.Id)
            .Take(ISeoFeaturedPropertiesService.MaxProperties)
            .Select(p => new
            {
                p.Id,
                p.Slug,
                OrgSlug = p.Org.Slug,
                p.Name,
                p.City,
                p.Bedrooms,
                p.Bathrooms,
                p.MaxGuests,
                p.NightlyRate,
                p.PhotoUrls,
            })
            .ToListAsync(cancellationToken);

        var properties = rows
            .Select(r => new SeoFeaturedPropertyDto(
                r.Id,
                r.Slug,
                r.OrgSlug,
                r.Name,
                r.City,
                r.Bedrooms,
                r.Bathrooms,
                r.MaxGuests,
                r.NightlyRate,
                r.PhotoUrls.FirstOrDefault()))
            .ToList();

        return new SeoFeaturedPropertiesDto(info.ComuneSlug, info.Name, properties);
    }
}
