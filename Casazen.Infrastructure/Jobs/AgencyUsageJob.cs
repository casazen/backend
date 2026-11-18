using System.Globalization;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stripe;
using Stripe.Billing;

namespace Casazen.Infrastructure.Jobs;

/// <summary>One property is one sellable unit (PO-13). PostgreSQL records activity transitions in the
/// inventory transaction; even an activation followed immediately by a deletion survives until billing.</summary>
public sealed class AgencyUsageJob(AppDbContext db, IConfiguration configuration, TimeProvider clock, ILogger<AgencyUsageJob> logger)
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

    [DisableConcurrentExecution(600)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var contracts = await db.Database.SqlQuery<Contract>($"""
            SELECT * FROM "AgencyUsageContracts" WHERE "EndedAt" IS NULL OR "ReportedAt" IS NULL OR "ReportedAt" < "EndedAt"
            """).ToListAsync(ct);
        List<Exception> failures = [];
        foreach (var contract in contracts)
        {
            try { await ReportAsync(contract, now, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Could not report Agency inventory for subscription {SubscriptionId}", contract.SubscriptionId);
                failures.Add(ex);
            }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    public static Task<int> CountAsync(AppDbContext db, Guid orgId, DateTime from, DateTime until, CancellationToken ct) =>
        db.Database.SqlQuery<int>($"""
            SELECT count(DISTINCT "PropertyId")::integer AS "Value" FROM "AgencyUnitActivity"
            WHERE "OrgId" = {orgId} AND "StartedAt" < {until} AND ("EndedAt" IS NULL OR "EndedAt" > {from})
            """).SingleAsync(ct);

    private async Task ReportAsync(Contract contract, DateTime now, CancellationToken ct)
    {
        var until = contract.EndedAt is { } end && end < now ? end : now;
        var cursor = contract.ReportedAt ?? contract.StartedAt;
        var events = new MeterEventService(new StripeClient(configuration["Stripe:SecretKey"]));
        while (cursor < until)
        {
            var month = new DateTime(cursor.Year, cursor.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var next = month.AddMonths(1);
            var observed = until < next ? until : next;
            var from = contract.StartedAt > month ? contract.StartedAt : month;
            int units = await CountAsync(db, contract.OrgId, from, observed, ct);

            // The last aggregator replaces the snapshot. Retrying after Stripe's 24h request-key
            // lifetime still yields the same quantity. On month rollover the final sample belongs
            // to the previous month; the cursor advances only after every selected meter succeeds.
            bool newMonth = contract.ReportedAt is null || contract.ReportedAt <= month || month != new DateTime(contract.ReportedAt.Value.Year, contract.ReportedAt.Value.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            if (newMonth || units != contract.ReportedUnits)
            {
                var stamp = observed == next || observed == contract.EndedAt ? observed.AddTicks(-1) : observed;
                foreach (var eventName in contract.Events)
                {
                    // A new request identifier on retry avoids duplicate-event errors. The provider's
                    // required LAST meter makes duplicate equal-valued samples idempotent.
                    await events.CreateAsync(new MeterEventCreateOptions
                    {
                        EventName = eventName, Timestamp = stamp,
                        Payload = new Dictionary<string, string> { ["stripe_customer_id"] = contract.CustomerId, ["value"] = units.ToString(CultureInfo.InvariantCulture) },
                    }, cancellationToken: ct);
                }
                if (units >= AgencyUsagePricing.QuoteFrom)
                    logger.LogWarning("Agency {OrgId} reached {Units} units in {Month:yyyy-MM}; a custom quote is required", contract.OrgId, units, month);
            }
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "AgencyUsageContracts" SET "ReportedAt" = {observed}, "ReportedUnits" = {units}
                WHERE "SubscriptionId" = {contract.SubscriptionId}
                """, ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "Orgs" SET "CommercialRoomAllowance" = {units} WHERE "Id" = {contract.OrgId} AND "SubscriptionId" = {contract.SubscriptionId}
                """, ct);
            cursor = observed;
        }
    }
}
