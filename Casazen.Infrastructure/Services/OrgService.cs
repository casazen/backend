using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Org tenant access and MVP plan management (US-004 extension).
/// </summary>
public partial class OrgService(AppDbContext dbContext) : IOrgService
{
    public Task<Org?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    public async Task<Org?> GetByUserIdAsync(string userId, CancellationToken cancellationToken = default)
    {
        var orgId = await dbContext.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.OrgId)
            .FirstOrDefaultAsync(cancellationToken);

        return orgId is null
            ? null
            : await dbContext.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken);
    }

    public Task<Org?> GetPublicBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
        dbContext.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Slug == slug && o.IsActive, cancellationToken);

    public Task<Org?> GetByVerifiedCustomDomainAsync(string host, CancellationToken cancellationToken = default) =>
        dbContext.Orgs.AsNoTracking().FirstOrDefaultAsync(o =>
            o.CustomDomain == host &&
            o.DomainVerificationStatus == DomainVerificationStatus.Verified &&
            o.PublicHostMode == PublicHostMode.CustomDomain &&
            o.IsActive,
            cancellationToken);

    public async Task<Org?> GetBySubdomainOrSlugAsync(string label, CancellationToken cancellationToken = default)
    {
        var bySubdomain = await dbContext.Orgs.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Subdomain == label && o.IsActive, cancellationToken);
        if (bySubdomain is not null)
            return bySubdomain;

        return await dbContext.Orgs.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Subdomain == null && o.Slug == label && o.IsActive, cancellationToken);
    }

    public async Task<Org> EnsureOrgForUserAsync(
        string userId,
        string email,
        string displayName,
        PlanTier planTier,
        CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new InvalidOperationException($"User {userId} must exist before org provisioning");

        if (user.OrgId.HasValue)
        {
            var existing = await dbContext.Orgs.FirstAsync(o => o.Id == user.OrgId.Value, cancellationToken);
            return existing;
        }

        var slug = await AllocateUniqueSlugAsync(userId, cancellationToken);
        var orgName = string.IsNullOrWhiteSpace(displayName) ? "La mia organizzazione" : displayName.Trim();
        var org = new Org
        {
            Name = orgName,
            DisplayName = orgName,
            Slug = slug,
            PlanTier = planTier,
            ContactEmail = email,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        dbContext.Orgs.Add(org);
        user.OrgId = org.Id;
        user.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return org;
    }

    public async Task<Org?> UpdatePlanTierAsync(
        Guid orgId,
        PlanTier planTier,
        CancellationToken cancellationToken = default)
    {
        var org = await dbContext.Orgs.FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken);
        if (org is null)
            return null;

        org.PlanTier = planTier;
        org.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return org;
    }

    public Task<Org?> GetByStripeCustomerIdAsync(string stripeCustomerId, CancellationToken cancellationToken = default) =>
        dbContext.Orgs.AsNoTracking()
            .FirstOrDefaultAsync(o => o.StripeCustomerId == stripeCustomerId, cancellationToken);

    public async Task<Org?> UpdateBillingProfileAsync(
        Guid orgId,
        string billingCountry,
        string? vatId,
        DateTime? vatValidatedAt,
        CancellationToken cancellationToken = default)
    {
        var org = await dbContext.Orgs.FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken);
        if (org is null)
            return null;

        org.BillingCountry = billingCountry.Trim().ToUpperInvariant();
        org.VatId = string.IsNullOrWhiteSpace(vatId) ? null : vatId.Replace(" ", string.Empty).Trim();
        org.VatIdValidatedAt = vatValidatedAt;
        org.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return org;
    }

    public async Task<IReadOnlyDictionary<Guid, Org>> GetByIdsAsync(
        IEnumerable<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0)
            return new Dictionary<Guid, Org>();

        var orgs = await dbContext.Orgs.AsNoTracking()
            .Where(o => idList.Contains(o.Id))
            .ToListAsync(cancellationToken);

        return orgs.ToDictionary(o => o.Id);
    }

    private async Task<string> AllocateUniqueSlugAsync(string userId, CancellationToken cancellationToken)
    {
        var baseSlug = $"org-{SanitizeSlugPart(userId)}";
        if (baseSlug.Length > 90)
            baseSlug = baseSlug[..90];

        var candidate = baseSlug;
        var suffix = 0;
        while (await dbContext.Orgs.AnyAsync(o => o.Slug == candidate, cancellationToken))
        {
            suffix++;
            candidate = $"{baseSlug}-{suffix}";
        }

        return candidate;
    }

    private static string SanitizeSlugPart(string value)
    {
        var sanitized = SlugSanitizer().Replace(value.ToLowerInvariant(), "-");
        sanitized = sanitized.Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "user" : sanitized;
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex SlugSanitizer();
}
