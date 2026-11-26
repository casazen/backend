using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Services;

/// <summary>Monthly request allowance shared by manual generation and background runs for one organization.</summary>
public sealed class MarketingAiQuota(AppDbContext db, IConfiguration config, TimeProvider clock)
{
    public async Task ReserveAsync(Guid orgId, CancellationToken ct)
    {
        var cap = config.GetValue<int?>($"Marketing:Organizations:{orgId:D}:MonthlyAiRequests")
            ?? config.GetValue<int?>("Marketing:MonthlyAiRequests");
        if (cap is null) return;
        if (cap <= 0) throw Exhausted();
        var now = clock.GetUtcNow();
        var month = new DateOnly(now.Year, now.Month, 1);
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "MarketingAiUsage" ("OrgId", "Month", "Requests") VALUES ({orgId}, {month}, 1)
            ON CONFLICT ("OrgId", "Month") DO UPDATE SET "Requests" = "MarketingAiUsage"."Requests" + 1
            WHERE "MarketingAiUsage"."Requests" < {cap.Value}
            """, ct);
        if (changed == 0) throw Exhausted();
    }

    private static DomainRuleException Exhausted() => new("marketing_ai_quota_exhausted", "marketing_ai_quota_exhausted");
}
