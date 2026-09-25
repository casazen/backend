using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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

    /// <remarks>
    /// Race-safe on the first access (A1-14): web, mobile and dashboard widgets provision in parallel. On
    /// PostgreSQL the check and the insert run in one transaction holding an advisory lock on the user (one
    /// org per user) and one on the base slug (users whose ids sanitize to the same slug). The loser of the
    /// race waits, then finds the org linked by the winner and returns it.
    /// </remarks>
    public async Task<Org> EnsureOrgForUserAsync(
        string userId,
        string email,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        // Fast path without locks: the user is already linked (every call after the first access).
        var linked = await GetLinkedOrgAsync(userId, cancellationToken);
        if (linked is not null)
            return linked;

        var baseSlug = BaseSlugFor(userId);
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            dbContext,
            cancellationToken,
            (PostgresAdvisoryLocks.Scope.OrgProvisioningUser, userId),
            (PostgresAdvisoryLocks.Scope.OrgSlug, baseSlug));

        // Re-read under the lock: a parallel request may have linked an org while this one waited.
        linked = await GetLinkedOrgAsync(userId, cancellationToken);
        if (linked is not null)
        {
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            return linked;
        }

        var user = await dbContext.Users.FirstAsync(u => u.Id == userId, cancellationToken);
        var slug = await AllocateUniqueSlugAsync(baseSlug, cancellationToken);
        var orgName = string.IsNullOrWhiteSpace(displayName) ? "La mia organizzazione" : displayName.Trim();
        var org = new Org
        {
            Name = orgName,
            DisplayName = orgName,
            Slug = slug,
            PlanTier = PlanTier.Starter,
            ContactEmail = email,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        dbContext.Orgs.Add(org);
        user.OrgId = org.Id;
        user.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return org;
    }

    /// <summary>
    /// The org linked to the user in the database, or <c>null</c> when none. Reads the committed row, not a copy
    /// this context may already track, and aligns that tracked copy so the caller sees the same <c>OrgId</c>.
    /// </summary>
    private async Task<Org?> GetLinkedOrgAsync(string userId, CancellationToken cancellationToken)
    {
        var row = await dbContext.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.OrgId })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"User {userId} must exist before org provisioning");

        if (row.OrgId is not Guid orgId)
            return null;

        var tracked = dbContext.Users.Local.FirstOrDefault(u => u.Id == userId);
        if (tracked is not null && tracked.OrgId != orgId)
        {
            var orgIdProperty = dbContext.Entry(tracked).Property(u => u.OrgId);
            orgIdProperty.CurrentValue = orgId;
            orgIdProperty.OriginalValue = orgId;
        }

        return await dbContext.Orgs.FirstAsync(o => o.Id == orgId, cancellationToken);
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

    /// <remarks>
    /// The slug is checked for uniqueness and written under a transaction-scoped advisory lock keyed on the
    /// candidate value (same <see cref="PostgresAdvisoryLocks.Scope.OrgSlug"/> as <see cref="EnsureOrgForUserAsync"/>),
    /// so two hosts racing for the same slug never both succeed. Name and contact email need no lock: an org row
    /// is only ever written by its own owner/admin (policy <c>OrgBillingAdmin</c>), never concurrently by design.
    /// </remarks>
    public async Task<Org?> UpdateSettingsAsync(
        Guid orgId,
        string name,
        string slug,
        string contactEmail,
        bool contactEmailPublic,
        CancellationToken cancellationToken = default)
    {
        var org = await dbContext.Orgs.FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken);
        if (org is null)
            return null;

        var trimmedName = name.Trim();
        var normalizedSlug = OrgSlugHelper.NormalizeRequired(slug);
        var normalizedEmail = contactEmail.Trim();
        var slugChanged = !string.Equals(org.Slug, normalizedSlug, StringComparison.Ordinal);

        // Disposing an uncommitted transaction rolls it back, so a thrown DomainException below needs no manual
        // cleanup; null when the slug is unchanged (no lock needed) or the provider is not PostgreSQL.
        await using var transaction = slugChanged
            ? await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                dbContext, cancellationToken, (PostgresAdvisoryLocks.Scope.OrgSlug, normalizedSlug))
            : null;

        if (slugChanged)
        {
            var taken = await dbContext.Orgs.AsNoTracking()
                .AnyAsync(o => o.Id != orgId && o.Slug == normalizedSlug, cancellationToken);
            if (taken)
                throw new DomainConflictException("org_slug_taken", "OrgSlugTaken");

            org.Slug = normalizedSlug;
        }

        org.Name = trimmedName;
        org.DisplayName = trimmedName;
        org.ContactEmail = normalizedEmail;
        org.ContactEmailPublic = contactEmailPublic;
        org.UpdatedAt = DateTime.UtcNow;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DomainConflictException("org_slug_taken", "OrgSlugTaken");
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

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

    private static string BaseSlugFor(string userId)
    {
        var baseSlug = $"org-{SanitizeSlugPart(userId)}";
        return baseSlug.Length > 90 ? baseSlug[..90] : baseSlug;
    }

    private async Task<string> AllocateUniqueSlugAsync(string baseSlug, CancellationToken cancellationToken)
    {
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
