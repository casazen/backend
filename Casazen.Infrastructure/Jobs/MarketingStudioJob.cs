using Casazen.Core.Services;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Jobs;

public sealed class MarketingStudioJob(AppDbContext db, IBackgroundJobClient queue, IBackgroundTenantScope tenant,
    MarketingStudio studio, IFeatureFlags flags, TimeProvider clock)
{
    [DisableConcurrentExecution(300)]
    public async Task DispatchAsync(CancellationToken ct)
    {
        if (!flags.IsEnabled(FeatureFlags.CrmMarketing)) return;
        var now = clock.GetUtcNow().UtcDateTime;
        var due = await db.MarketingStudioSettings.Where(s => s.NextNewsletterAt <= now || s.NextEventSearchAt <= now)
            .Select(s => new { s.OrgId, Newsletter = s.NextNewsletterAt <= now, Events = s.NextEventSearchAt <= now }).ToListAsync(ct);
        foreach (var row in due)
        {
            if (row.Newsletter) queue.Enqueue<MarketingStudioJob>(j => j.RunAsync(row.OrgId, false, CancellationToken.None));
            if (row.Events) queue.Enqueue<MarketingStudioJob>(j => j.RunAsync(row.OrgId, true, CancellationToken.None));
        }
    }

    [DisableConcurrentExecution("MarketingStudioJob:{0}:{1}", 300)]
    public async Task RunAsync(Guid orgId, bool events, CancellationToken ct)
    {
        if (!flags.IsEnabled(FeatureFlags.CrmMarketing)) return;
        tenant.EnterOrg(orgId);
        var org = await db.Orgs.SingleOrDefaultAsync(o => o.Id == orgId, ct);
        if (org == null || !CommercialSignup.HasCrmMarketing(org)) return;
        var settings = await db.MarketingStudioSettings.SingleOrDefaultAsync(s => s.OrgId == orgId, ct);
        if (settings == null) return;
        var now = clock.GetUtcNow().UtcDateTime;
        if (events && settings.NextEventSearchAt <= now) await studio.DiscoverAsync(settings, ct);
        if (!events && settings.NextNewsletterAt <= now) await studio.NewsletterAsync(settings, ct);
    }
}
