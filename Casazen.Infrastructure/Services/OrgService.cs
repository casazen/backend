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

    /// <remarks>
    /// Also resolves a previous slug of the org (<see cref="OrgSlugAlias"/>, PL-04): links shared before a slug change
    /// keep working. The returned org carries its current slug, so the site can redirect to the canonical address.
    /// </remarks>
    public async Task<Org?> GetPublicBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        var orgId = await ResolveOrgIdBySlugAsync(dbContext, slug, cancellationToken);
        return orgId is null
            ? null
            : await dbContext.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId && o.IsActive, cancellationToken);
    }

    /// <summary>
    /// The org whose current or previous public slug is <paramref name="slug"/>, or <c>null</c> (PL-04, A1-23). Shared by
    /// every anonymous lookup that receives an org slug from a public link.
    /// </summary>
    internal static async Task<Guid?> ResolveOrgIdBySlugAsync(
        AppDbContext dbContext,
        string slug,
        CancellationToken cancellationToken)
    {
        var current = await dbContext.Orgs.AsNoTracking()
            .Where(o => o.Slug == slug)
            .Select(o => (Guid?)o.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (current is not null)
            return current;

        // IgnoreQueryFilters: the slug comes from a public link, the org is not known yet (anonymous or another
        // tenant's visitor); the alias only maps the link to its org, the caller then applies its own checks.
        return await dbContext.OrgSlugAliases.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.Slug == slug)
            .Select(a => (Guid?)a.OrgId)
            .FirstOrDefaultAsync(cancellationToken);
    }

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
    /// org per user). The loser of the race waits, then finds the org linked by the winner and returns it.
    /// The new org gets a neutral random slug (A1-23): never the identity-provider id; the host chooses a readable
    /// one in the org settings (<see cref="UpdateSettingsAsync"/>).
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

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            dbContext,
            cancellationToken,
            (PostgresAdvisoryLocks.Scope.OrgProvisioningUser, userId));

        // Re-read under the lock: a parallel request may have linked an org while this one waited.
        linked = await GetLinkedOrgAsync(userId, cancellationToken);
        if (linked is not null)
        {
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            return linked;
        }

        var user = await dbContext.Users.FirstAsync(u => u.Id == userId, cancellationToken);

        // PL-05 (A1-40): the previous OrgId, if any, was rejected above by GetLinkedOrgAsync because it is not a Host
        // org — a legacy link to the caller's own Supplier org (written by the supplier registration before PL-05).
        // Keep that link on SupplierOrgId (normally already set) so the supplier console keeps working, instead of
        // silently losing it once OrgId is replaced below.
        if (user.OrgId is Guid previousOrgId && user.SupplierOrgId is null)
        {
            var previousOrg = await dbContext.Orgs.AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == previousOrgId, cancellationToken);
            if (previousOrg?.OrgType == OrgType.Supplier)
                user.SupplierOrgId = previousOrgId;
        }

        var slug = await AllocateNeutralSlugAsync(cancellationToken);
        var orgName = string.IsNullOrWhiteSpace(displayName) ? Org.PlaceholderName : displayName.Trim();
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
    /// The <b>Host</b> org linked to the user in the database, or <c>null</c> when none. Reads the committed row,
    /// not a copy this context may already track, and aligns that tracked copy so the caller sees the same
    /// <c>OrgId</c>.
    /// </summary>
    /// <remarks>
    /// PL-05 (A1-40): <c>User.OrgId</c> is the host org only. A legacy row may still point at a non-Host org — the
    /// caller's own Supplier org, written by the supplier registration before PL-05. That link is never reused as the
    /// host org: the caller (<see cref="EnsureOrgForUserAsync"/>) provisions a real Host org instead, exactly as
    /// <c>OrgContextResolver</c> and <c>TenantContext</c> also refuse to treat it as the host tenant.
    /// </remarks>
    private async Task<Org?> GetLinkedOrgAsync(string userId, CancellationToken cancellationToken)
    {
        var row = await dbContext.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.OrgId })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"User {userId} must exist before org provisioning");

        if (row.OrgId is not Guid orgId)
            return null;

        var org = await dbContext.Orgs.FirstAsync(o => o.Id == orgId, cancellationToken);
        if (org.OrgType != OrgType.Host)
            return null;

        var tracked = dbContext.Users.Local.FirstOrDefault(u => u.Id == userId);
        if (tracked is not null && tracked.OrgId != orgId)
        {
            var orgIdProperty = dbContext.Entry(tracked).Property(u => u.OrgId);
            orgIdProperty.CurrentValue = orgId;
            orgIdProperty.OriginalValue = orgId;
        }

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
        BillingEInvoiceDetails? eInvoice = null,
        CancellationToken cancellationToken = default)
    {
        var org = await dbContext.Orgs.FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken);
        if (org is null)
            return null;

        org.BillingCountry = billingCountry.Trim().ToUpperInvariant();
        var normalizedVatId = string.IsNullOrWhiteSpace(vatId) ? null : vatId.Replace(" ", string.Empty).Trim();
        if (!string.Equals(org.VatId, normalizedVatId, StringComparison.Ordinal))
            org.VatIdValidatedAt = null;
        org.VatId = normalizedVatId;
        if (eInvoice is not null)
        {
            // Per field: null = unchanged (a client that does not know the field never wipes it), "" = cleared.
            if (eInvoice.SdiRecipientCode is not null)
                org.BillingSdiRecipientCode = NullIfEmpty(eInvoice.SdiRecipientCode);
            if (eInvoice.PecEmail is not null)
                org.BillingPecEmail = NullIfEmpty(eInvoice.PecEmail);
            if (eInvoice.FiscalCode is not null)
                org.BillingFiscalCode = NullIfEmpty(eInvoice.FiscalCode);
        }

        org.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return org;
    }

    /// <remarks>
    /// Slug changes run in one transaction holding advisory locks on the new and the old value (same
    /// <see cref="PostgresAdvisoryLocks.Scope.OrgSlug"/> key space, taken in a stable order): two orgs racing for the
    /// same slug never both get it, and the unique index on <c>Orgs.Slug</c> (23505 → 409) backs the check. The old
    /// slug becomes an <see cref="OrgSlugAlias"/> of the org, so shared links keep working and nobody else can take it.
    /// An unchanged slug is never revalidated: an org keeps a legacy slug until it chooses a new one.
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
        var normalizedEmail = contactEmail.Trim();
        var newSlug = IsCurrentSlug(org, slug) ? org.Slug : OrgSlugHelper.NormalizeRequired(slug);
        var previousSlug = org.Slug;
        var slugChanged = !string.Equals(previousSlug, newSlug, StringComparison.Ordinal);

        // Disposing an uncommitted transaction rolls it back, so a thrown DomainException below needs no manual
        // cleanup; null when the slug is unchanged (no lock needed) or the provider is not PostgreSQL.
        var slugLocks = new[] { previousSlug, newSlug }
            .Order(StringComparer.Ordinal)
            .Select(value => (PostgresAdvisoryLocks.Scope.OrgSlug, value))
            .ToArray();
        await using var transaction = slugChanged
            ? await PostgresAdvisoryLocks.BeginLockedTransactionAsync(dbContext, cancellationToken, slugLocks)
            : null;

        if (slugChanged)
        {
            if (await IsSlugTakenByAnotherOrgAsync(orgId, newSlug, cancellationToken))
                throw new DomainConflictException(OrgSlugHelper.TakenCode, "OrgSlugTaken");

            // IgnoreQueryFilters: the org is identified explicitly (aliases of this org only); the caller's tenant
            // filter would hide nothing here, but the update must not depend on the request's tenant context.
            var ownAliases = await dbContext.OrgSlugAliases.IgnoreQueryFilters()
                .Where(a => a.OrgId == orgId && (a.Slug == newSlug || a.Slug == previousSlug))
                .ToListAsync(cancellationToken);

            // Back to a previous slug: it is the current one again, no longer an alias.
            dbContext.OrgSlugAliases.RemoveRange(ownAliases.Where(a => a.Slug == newSlug));
            if (!string.IsNullOrEmpty(previousSlug) && ownAliases.All(a => a.Slug != previousSlug))
            {
                dbContext.OrgSlugAliases.Add(new OrgSlugAlias
                {
                    Slug = previousSlug,
                    OrgId = orgId,
                    CreatedAt = DateTime.UtcNow,
                });
            }

            org.Slug = newSlug;
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
            throw new DomainConflictException(OrgSlugHelper.TakenCode, "OrgSlugTaken");
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        return org;
    }

    public async Task<OrgSlugAvailability?> CheckSlugAvailabilityAsync(
        Guid orgId,
        string slug,
        CancellationToken cancellationToken = default)
    {
        var org = await dbContext.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken);
        if (org is null)
            return null;

        if (IsCurrentSlug(org, slug))
            return new OrgSlugAvailability(org.Slug, true, null);

        string normalized;
        try
        {
            normalized = OrgSlugHelper.NormalizeRequired(slug);
        }
        catch (DomainRuleException ex)
        {
            return new OrgSlugAvailability(OrgSlugHelper.Sanitize(slug), false, ex.Code);
        }

        if (string.Equals(normalized, org.Slug, StringComparison.Ordinal))
            return new OrgSlugAvailability(normalized, true, null);

        return await IsSlugTakenByAnotherOrgAsync(orgId, normalized, cancellationToken)
            ? new OrgSlugAvailability(normalized, false, OrgSlugHelper.TakenCode)
            : new OrgSlugAvailability(normalized, true, null);
    }

    /// <summary>The value submitted is the org's current slug as is (also a legacy slug the rules would now refuse).</summary>
    private static bool IsCurrentSlug(Org org, string? slug) =>
        string.Equals(slug?.Trim(), org.Slug, StringComparison.Ordinal);

    /// <summary>
    /// Another org uses <paramref name="slug"/>: as its current slug, as a previous slug (alias) or as its subdomain,
    /// which would shadow this org's slug-as-subdomain fallback (<see cref="GetBySubdomainOrSlugAsync"/>).
    /// </summary>
    private async Task<bool> IsSlugTakenByAnotherOrgAsync(Guid orgId, string slug, CancellationToken cancellationToken)
    {
        if (await dbContext.Orgs.AsNoTracking()
                .AnyAsync(o => o.Id != orgId && (o.Slug == slug || o.Subdomain == slug), cancellationToken))
            return true;

        // IgnoreQueryFilters: the aliases of every org reserve their value, not only the caller's.
        return await dbContext.OrgSlugAliases.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(a => a.OrgId != orgId && a.Slug == slug, cancellationToken);
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

    /// <summary>
    /// A neutral slug not used by any org, now or as a previous slug. A collision of the random part is practically
    /// impossible; the unique index on <c>Orgs.Slug</c> still refuses a duplicate.
    /// </summary>
    private async Task<string> AllocateNeutralSlugAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var candidate = OrgSlugHelper.GenerateNeutral();
            if (!await dbContext.Orgs.AnyAsync(o => o.Slug == candidate, cancellationToken) &&
                !await dbContext.OrgSlugAliases.IgnoreQueryFilters().AnyAsync(a => a.Slug == candidate, cancellationToken))
                return candidate;
        }
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
