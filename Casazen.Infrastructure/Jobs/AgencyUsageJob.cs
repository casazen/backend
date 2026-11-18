using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>One property is one sellable unit (PO-13). PostgreSQL records activity transitions in the
/// inventory transaction; even an activation followed immediately by a deletion survives until billing.</summary>
public sealed class AgencyUsageJob(AppDbContext db, IStripeBillingService stripe, TimeProvider clock, ILogger<AgencyUsageJob> logger)
{
    public sealed class Contract
    {
        public required string SubscriptionId { get; init; }
        public Guid OrgId { get; init; }
        public required string CustomerId { get; init; }
        public required string[] Events { get; init; }
        public DateTime StartedAt { get; init; }
        public DateTime? EndedAt { get; init; }
        public DateTime? ReportedAt { get; init; }
        public int? ReportedUnits { get; init; }
    }

    [DisableConcurrentExecution(60)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var subscriptions = await db.Database.SqlQuery<string>($"""
            SELECT "SubscriptionId" AS "Value" FROM "AgencyUsageContracts" WHERE "EndedAt" IS NULL OR "ReportedAt" IS NULL OR "ReportedAt" < "EndedAt"
            """).ToListAsync(ct);
        List<Exception> failures = [];
        foreach (var subscriptionId in subscriptions)
        {
            try { await ReportAsync(subscriptionId, now, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Could not report Agency inventory for subscription {SubscriptionId}", subscriptionId);
                failures.Add(ex);
            }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    public Task<bool> CloseInvoicePeriodAsync(string subscriptionId, DateTime periodEnd, CancellationToken ct) =>
        periodEnd > clock.GetUtcNow().UtcDateTime ? Task.FromResult(false) : ReportAsync(subscriptionId, periodEnd, ct);

    public static Task<int> CountAsync(AppDbContext db, Guid orgId, DateTime from, DateTime until, CancellationToken ct) =>
        db.Database.SqlQuery<int>($"""
            SELECT count(DISTINCT "PropertyId")::integer AS "Value" FROM "AgencyUnitActivity"
            WHERE "OrgId" = {orgId} AND "StartedAt" < {until} AND ("EndedAt" IS NULL OR "EndedAt" > {from})
            """).SingleAsync(ct);

    private async Task<bool> ReportAsync(string subscriptionId, DateTime now, CancellationToken ct)
    {
        while (true)
        {
            // invoice.created and the recurring sweep may arrive together. Lock this subscription
            // through submission and cursor advancement, independently of every other agency.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var contract = await db.Database.SqlQuery<Contract>($"""
                SELECT * FROM "AgencyUsageContracts" WHERE "SubscriptionId" = {subscriptionId} FOR UPDATE
                """).SingleOrDefaultAsync(ct);
            if (contract is null) return false;
            var until = contract.EndedAt is { } end && end < now ? end : now;
            var cursor = contract.ReportedAt ?? contract.StartedAt;
            if (cursor >= until) return true;
            var month = new DateTime(cursor.Year, cursor.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var next = month.AddMonths(1);
            var observed = until < next ? until : next;
            var from = contract.StartedAt > month ? contract.StartedAt : month;
            int units = await CountAsync(db, contract.OrgId, from, observed, ct);

            // The last aggregator replaces the snapshot. Retrying after Stripe's 24h request-key
            // lifetime still yields the same quantity. On month rollover the final sample belongs
            // to the previous month; the cursor advances only after every selected meter succeeds.
            bool send = cursor == month || units != contract.ReportedUnits;
            if (send)
            {
                var stamp = observed == next || observed == contract.EndedAt ? observed.AddTicks(-1) : observed;
                foreach (var eventName in contract.Events)
                {
                    // A new request identifier on retry avoids duplicate-event errors. The provider's
                    // required LAST meter makes duplicate equal-valued samples idempotent.
                    await stripe.ReportAgencyUnitsAsync(contract.CustomerId, eventName, units, stamp, ct);
                }
                if (units >= AgencyUsagePricing.QuoteFrom)
                    logger.LogWarning("Agency {OrgId} reached {Units} units in {Month:yyyy-MM}; a custom quote is required", contract.OrgId, units, month);
            }
            if (!send && observed < next && observed != contract.EndedAt) return true;
            if (units >= AgencyUsagePricing.QuoteFrom)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "AgencyCustomQuotes" ("SubscriptionId","Month","Units") VALUES ({contract.SubscriptionId},{DateOnly.FromDateTime(month)},{units})
                    ON CONFLICT ("SubscriptionId","Month") DO UPDATE SET "Units" = GREATEST("AgencyCustomQuotes"."Units", EXCLUDED."Units")
                    """, ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "AgencyUsageContracts" SET "ReportedAt" = {observed}, "ReportedUnits" = {units}
                WHERE "SubscriptionId" = {contract.SubscriptionId}
                """, ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "Orgs" SET "CommercialRoomAllowance" = {units} WHERE "Id" = {contract.OrgId} AND "SubscriptionId" = {contract.SubscriptionId}
                """, ct);
            await tx.CommitAsync(ct);
            if (observed == until) return true;
        }
    }
}
