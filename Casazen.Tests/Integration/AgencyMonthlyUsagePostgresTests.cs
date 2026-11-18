using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

public class AgencyMonthlyUsagePostgresTests
{
    [PostgresFact]
    public async Task Inventory_UsesOnePropertyAcrossPausesAndRemovals_InItsOwnAgency()
    {
        await using var database = await PostgresTestDatabase.CreateMigratedAsync();
        await using var db = database.CreateContext();
        var org = new Casazen.Core.Entities.Org { Slug = "unit-billing", IsAgency = true, Name = "Agency" };
        db.Orgs.Add(org);
        var property = new Property { OrgId = org.Id, OwnerId = "host", Name = "One sellable unit", Bedrooms = 4, Address = "Street 1", IsActive = false };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        var start = DateTime.UtcNow.Date.AddDays(-1);
        var end = start.AddDays(2);
        Assert.Equal(0, await AgencyUsageJob.CountAsync(db, org.Id, start, end, default));
        property.IsActive = true;
        await db.SaveChangesAsync();
        property.IsPaused = true;
        await db.SaveChangesAsync();
        property.IsPaused = false;
        await db.SaveChangesAsync();
        property.IsDeleted = true;
        await db.SaveChangesAsync();
        Assert.Equal(1, await AgencyUsageJob.CountAsync(db, org.Id, start, end, default));
        Assert.Equal(0, await AgencyUsageJob.CountAsync(db, Guid.NewGuid(), start, end, default));
        Assert.Equal(0, await AgencyUsageJob.CountAsync(db, org.Id, end, end.AddMonths(1), default));
    }

    [PostgresFact]
    public async Task RetryAcrossRollover_UsesLastSnapshot_ForDistinctUnitsDuringEachMonth()
    {
        await using var database = await PostgresTestDatabase.CreateMigratedAsync();
        await using var db = database.CreateContext();
        var orgId = Guid.NewGuid();
        var start = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);
        var july = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var events = new[] { AgencyUsagePricing.EventName("base"), AgencyUsagePricing.EventName("revenue") };
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AgencyUsageContracts" ("SubscriptionId","OrgId","CustomerId","Events","StartedAt")
            VALUES ('sub-test',{orgId},'cus-test',{events},{start});
            """);
        var sharedUnit = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AgencyUnitActivity" ("OrgId","PropertyId","StartedAt","EndedAt") VALUES
              ({orgId},{Guid.NewGuid()},{start.AddDays(-10)},{start}),
              ({orgId},{sharedUnit},{start},{start.AddDays(1)}),
              ({orgId},{sharedUnit},{start.AddDays(3)},NULL),
              ({orgId},{Guid.NewGuid()},{start.AddHours(1)},{july});
            """);
        var stripe = new Mock<IStripeBillingService>();
        var snapshots = new Dictionary<(string Event, int Month), int>();
        bool failOnce = true;
        stripe.Setup(s => s.ReportAgencyUnitsAsync("cus-test", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns((string customer, string name, int count, DateTime stamp, CancellationToken _) =>
            {
                if (name == events[1] && failOnce) { failOnce = false; throw new IOException("network interruption"); }
                snapshots[(name, stamp.Month)] = count;
                return Task.CompletedTask;
            });
        var clock = new FakeTimeProvider(july.AddHours(1));
        var job = new AgencyUsageJob(db, stripe.Object, clock, NullLogger<AgencyUsageJob>.Instance);
        await Assert.ThrowsAsync<AggregateException>(() => job.ExecuteAsync(default));
        // Stripe's invoice.created can retry before the next sweep. Closing the June
        // invoice flushes June's final quantity; advancing the recurring job starts July anew.
        Assert.True(await job.CloseInvoicePeriodAsync("sub-test", july, default));
        Assert.True(await job.CloseInvoicePeriodAsync("sub-test", july, default));
        Assert.False(await job.CloseInvoicePeriodAsync("no-subscription", july, default));
        await job.ExecuteAsync(default);
        await job.ExecuteAsync(default);
        foreach (var name in events)
        {
            Assert.Equal(2, snapshots[(name, 6)]);
            Assert.Equal(1, snapshots[(name, 7)]);
        }
    }
}
