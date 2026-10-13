using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IDomainRecheckService"/>
/// <remarks>
/// What is due: a domain that is not verified yet, every <see cref="PublicHostOptions.RecheckPendingMinutes"/>, until
/// <see cref="PublicHostOptions.MaxPendingDays"/> after the host set it (after that only "Verifica ora" checks it); a verified
/// domain, every <see cref="PublicHostOptions.RecheckVerifiedHours"/>. Only orgs whose effective tier allows the custom domain
/// are checked (an unpaid one is not served anyway). At most <see cref="PublicHostOptions.RecheckBatchSize"/> domains per run,
/// the longest-unchecked first.
/// </remarks>
public sealed class DomainRecheckService(
    AppDbContext dbContext,
    IDomainVerificationService verification,
    IEntitlementService entitlementService,
    IVercelDomainsClient vercel,
    PublicSiteLinks publicSiteLinks,
    IOptions<PublicHostOptions> options,
    TimeProvider timeProvider,
    ILogger<DomainRecheckService> logger) : IDomainRecheckService
{
    public async Task<DomainRecheckSummary> RunAsync(CancellationToken cancellationToken = default)
    {
        var (removed, removalsFailed) = await ProcessRemovalsAsync(cancellationToken);
        var (checkedCount, nowVerified, noLongerVerified) = await RecheckDomainsAsync(cancellationToken);
        return new DomainRecheckSummary(checkedCount, nowVerified, noLongerVerified, removed, removalsFailed);
    }

    // ─── Removals ───────────────────────────────────────────────────────────────────────────────────

    private async Task<(int Removed, int Failed)> ProcessRemovalsAsync(CancellationToken cancellationToken)
    {
        await EnqueueRemovalsOfInactiveOrgsAsync(cancellationToken);
        if (!vercel.IsConfigured)
            return (0, 0);

        var removed = 0;
        var failed = 0;
        var pending = await dbContext.PendingDomainRemovals
            .OrderBy(r => r.LastAttemptAt)
            .Take(options.Value.RecheckBatchSize)
            .ToListAsync(cancellationToken);

        foreach (var removal in pending)
        {
            try
            {
                // Never the web app's own domain, whatever the queue says: that would take the app off its Vercel project.
                if (publicSiteLinks.IsPublicSiteHost(removal.Domain))
                {
                    dbContext.PendingDomainRemovals.Remove(removal);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    logger.LogWarning("A removal of the web app's own domain was dropped from the queue");
                    continue;
                }

                // Another org may hold the same domain by now (set again after it was dropped): it must stay on the project.
                var inUse = await dbContext.Orgs.AsNoTracking().AnyAsync(
                    o => o.PublicHostMode == PublicHostMode.CustomDomain && o.CustomDomain == removal.Domain && o.IsActive,
                    cancellationToken);
                if (inUse)
                {
                    dbContext.PendingDomainRemovals.Remove(removal);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    continue;
                }

                var result = await vercel.RemoveDomainAsync(removal.Domain, cancellationToken);
                removal.Attempts++;
                removal.LastAttemptAt = timeProvider.GetUtcNow().UtcDateTime;

                // Not found = already gone from the project: done as well.
                if (result.Status is VercelCallStatus.Ok or VercelCallStatus.NotFound)
                {
                    dbContext.PendingDomainRemovals.Remove(removal);
                    removed++;
                }
                else
                {
                    removal.LastError = result.Status.ToString();
                    failed++;
                }

                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogError(ex, "Removal of a custom domain from the Vercel project failed unexpectedly");
                dbContext.ChangeTracker.Clear();
            }
        }

        return (removed, failed);
    }

    /// <summary>An org that was deactivated no longer serves its domain: it leaves the Vercel project too.</summary>
    private async Task EnqueueRemovalsOfInactiveOrgsAsync(CancellationToken cancellationToken)
    {
        var inactive = await dbContext.Orgs
            .Where(o => !o.IsActive && o.DomainVercelAddedAt != null && o.CustomDomain != null)
            .Take(options.Value.RecheckBatchSize)
            .ToListAsync(cancellationToken);

        foreach (var org in inactive)
        {
            await DomainRemovalQueue.EnqueueAsync(dbContext, org.CustomDomain!, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
            org.DomainVercelAddedAt = null;
        }

        if (inactive.Count > 0)
            await dbContext.SaveChangesAsync(cancellationToken);
    }

    // ─── Checks ─────────────────────────────────────────────────────────────────────────────────────

    private async Task<(int Checked, int NowVerified, int NoLongerVerified)> RecheckDomainsAsync(CancellationToken cancellationToken)
    {
        var hostOptions = options.Value;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var pendingCutoff = now.AddMinutes(-Math.Max(1, hostOptions.RecheckPendingMinutes));
        var verifiedCutoff = now.AddHours(-Math.Max(1, hostOptions.RecheckVerifiedHours));
        var giveUpBefore = now.AddDays(-Math.Max(1, hostOptions.MaxPendingDays));

        // Orgs are not tenant-owned: the periodic job reads them all, on purpose.
        var candidates = await dbContext.Orgs
            .Where(o => o.IsActive
                        && o.PublicHostMode == PublicHostMode.CustomDomain
                        && o.CustomDomain != null
                        && o.DomainVerificationToken != null)
            .Where(o =>
                (o.DomainVerificationStatus == DomainVerificationStatus.Verified
                 && (o.DomainCheckedAt == null || o.DomainCheckedAt <= verifiedCutoff))
                || (o.DomainVerificationStatus != DomainVerificationStatus.Verified
                    && (o.DomainCheckedAt == null || o.DomainCheckedAt <= pendingCutoff)
                    && (o.DomainConfiguredAt ?? o.UpdatedAt) >= giveUpBefore))
            .OrderBy(o => o.DomainCheckedAt)
            .Take(hostOptions.RecheckBatchSize)
            .ToListAsync(cancellationToken);

        var checkedCount = 0;
        var nowVerified = 0;
        var noLongerVerified = 0;

        foreach (var org in candidates)
        {
            try
            {
                // Same rule as everywhere else: the effective tier, so a lapsed plan is not checked (nor served).
                if (entitlementService.ResolveEffectiveTier(org) is not (PlanTier.Pro or PlanTier.Scale))
                    continue;

                var before = org.DomainVerificationStatus;
                var result = await verification.VerifyAsync(org, cancellationToken);
                checkedCount++;
                if (before != DomainVerificationStatus.Verified && result.Status == DomainVerificationStatus.Verified)
                    nowVerified++;
                if (before == DomainVerificationStatus.Verified && result.Status != DomainVerificationStatus.Verified)
                    noLongerVerified++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One org never stops the run: its domain is checked again at the next one.
                logger.LogError(ex, "Periodic domain check of org {OrgId} failed", org.Id);
                dbContext.ChangeTracker.Clear();
            }
        }

        if (checkedCount > 0)
            logger.LogInformation(
                "Domain recheck: {Checked} checked, {Verified} now verified, {Demoted} no longer verified",
                checkedCount, nowVerified, noLongerVerified);

        return (checkedCount, nowVerified, noLongerVerified);
    }
}

/// <summary>Writes the domains that must leave the Vercel project (BK-17), one row per domain.</summary>
public static class DomainRemovalQueue
{
    /// <summary>Queues <paramref name="domain"/> for removal; a domain already queued is left as it is. The caller saves.</summary>
    public static async Task EnqueueAsync(AppDbContext dbContext, string domain, DateTime now, CancellationToken cancellationToken)
    {
        if (await dbContext.PendingDomainRemovals.AnyAsync(r => r.Domain == domain, cancellationToken))
            return;

        dbContext.PendingDomainRemovals.Add(new PendingDomainRemoval { Domain = domain, RequestedAt = now });
    }
}
