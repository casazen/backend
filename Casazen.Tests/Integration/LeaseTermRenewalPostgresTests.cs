using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Migrations;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// LR-14 on real PostgreSQL, the one rule where a mistake costs data: a lease renews by itself <b>once</b>, whatever the number of runs
/// of the job, and a lease that was already over when CasaZen came to know it is <b>never</b> renewed (decision of the product owner,
/// 17 October 2026: no retroactive renewal). Two registered libero leases are written by SQL at the schema right before
/// <c>AddLeaseTermLifecycle</c>, as the code of the time wrote them: one ended in 2021, one ends at the end of 2093. The migration is
/// applied; then a third one is written by SQL the way a server that did not know the new columns writes it (a deploy in progress), with a
/// contract that ended in 2089. The job runs three times with the clock past the end of the second one. The two that were over stay
/// concluded (their end, the day they were recorded as ended, no event), the running one goes forward by exactly one period of 48 months
/// with one <c>TermRenewed</c> event, and running the backfill again changes none. A contract signed after its last day (an old one
/// imported) is concluded at the signature, so the job finds it not due.
/// </summary>
public class LeaseTermRenewalPostgresTests : IAsyncLifetime
{
    private static readonly DateTime SeedAt = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Renewal_ARunRepeatedThreeTimes_AddsOnePeriodOnly_AndALeaseThatWasAlreadyOverWhenItCameInIsNeverRenewed()
    {
        var database = _database!;
        var org = Guid.NewGuid();
        var property = Guid.NewGuid();
        var ended = Guid.NewGuid();
        var running = Guid.NewGuid();
        var late = Guid.NewGuid();
        var endedLast = new DateTime(2021, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        var lateLast = new DateTime(2089, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        var runningFirstEnd = new DateTime(2093, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        var runningRenewedEnd = new DateTime(2097, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        await using (var db = database.CreateContext())
        {
            db.GetService<IMigrator>().Migrate(PreviousMigration(db));
            await LegacyOrgRows.InsertAsync(db, new OrgEntity
            {
                Id = org,
                Name = "Org LR-14",
                Slug = $"lr14-{org:N}",
                DisplayName = "Org LR-14",
                ContactEmail = "lr14@example.com",
                PlanTier = PlanTier.Starter,
                IsActive = true,
            });
            await LegacyPropertyRows.InsertAsync(db, new Property
            {
                Id = property,
                OrgId = org,
                OwnerId = "auth0|lr14",
                Name = "Casa LR-14",
                Description = "LR-14",
                Address = "Via LR-14 " + property.ToString("N"),
                City = "Bari",
                PostalCode = "70121",
                Bedrooms = 2,
                Bathrooms = 1,
                MaxGuests = 0,
                NightlyRate = 0,
                UpdatedAt = SeedAt,
            });
            await InsertLeaseAsync(db, org, property, ended, new DateTime(2018, 1, 1, 0, 0, 0, DateTimeKind.Utc), endedLast);
            await InsertLeaseAsync(db, org, property, running, new DateTime(2090, 1, 1, 0, 0, 0, DateTimeKind.Utc), runningFirstEnd);

            await db.Database.MigrateAsync();

            // Written after the migration by a server that did not know the new columns (they keep the starting value of the database,
            // -infinity), with a contract that is already over: it came in over, so it is not renewed.
            await InsertLeaseAsync(db, org, property, late, new DateTime(2086, 1, 1, 0, 0, 0, DateTimeKind.Utc), lateLast);
        }

        // The clock is past the end of the second lease; the first and the third ended long before.
        var clock = new FakeTimeProvider(new DateTimeOffset(2094, 6, 15, 12, 0, 0, TimeSpan.Zero));
        var runs = new List<LeaseTermRenewalRun>();
        for (var i = 0; i < 3; i++)
        {
            await using var db = database.CreateContext();
            var service = new LeaseTermService(db, new Mock<IRentBillingService>().Object, NullLogger<LeaseTermService>.Instance, clock);
            runs.Add(await service.RenewDueAsync());
        }

        Assert.Equal(new LeaseTermRenewalRun(false, 1, 1, 0), runs[0]);
        Assert.Equal(new LeaseTermRenewalRun(false, 0, 0, 0), runs[1]);
        Assert.Equal(new LeaseTermRenewalRun(false, 0, 0, 0), runs[2]);

        await AssertStateAsync();

        // The backfill is safe to run again: the lease that renewed is not recorded as ended, the ones that ended are not touched.
        await using (var db = database.CreateContext())
            await db.Database.ExecuteSqlRawAsync(AddLeaseTermLifecycle.BackfillSql);
        await AssertStateAsync();

        // The signature is a door too: a contract signed after its last day (an old one imported) is concluded at once, so it is never due.
        var imported = new LeaseContract
        {
            ContractType = LeaseContractType.Libero,
            Status = LeaseStatus.Signed,
            StartDate = new DateTime(2018, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = endedLast,
        };
        LeaseRenewal.ConcludeIfEnded(imported, clock.TodayInRome());
        Assert.Equal(endedLast, imported.TerminatedOn);
        Assert.False(LeaseRenewal.IsDue(imported, clock.TodayInRome()));

        async Task AssertStateAsync()
        {
            await using var db = database.CreateContext();
            var leases = await db.LeaseContracts.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(l => l.Id);
            var events = await db.LeaseEvents.AsNoTracking().ToListAsync();

            // Ended before the migration: concluded on its last day, never renewed, no event.
            Assert.Equal(endedLast, leases[ended].EndDate);
            Assert.Equal(endedLast, leases[ended].CurrentTermEndDate);
            Assert.Equal(endedLast, leases[ended].TerminatedOn);
            Assert.DoesNotContain(events, e => e.LeaseContractId == ended);

            // Written after the migration with its contract already over: the repair of the job concludes it, it is never renewed, no event.
            Assert.Equal(lateLast, leases[late].EndDate);
            Assert.Equal(lateLast, leases[late].CurrentTermEndDate);
            Assert.Equal(lateLast, leases[late].TerminatedOn);
            Assert.DoesNotContain(events, e => e.LeaseContractId == late);

            // Running at the migration: the contract's end stays, the current period goes forward by one period, once.
            Assert.Equal(runningFirstEnd, leases[running].EndDate);
            Assert.Equal(runningRenewedEnd, leases[running].CurrentTermEndDate);
            Assert.Null(leases[running].TerminatedOn);
            var renewed = Assert.Single(events, e => e.LeaseContractId == running);
            Assert.Equal(LeaseEventType.TermRenewed, renewed.EventType);
            Assert.Equal("2097-12-31", renewed.Payload);
        }
    }

    private static Task InsertLeaseAsync(AppDbContext db, Guid org, Guid property, Guid id, DateTime start, DateTime end) =>
        LegacyLeaseRows.InsertAsync(db, new LeaseContract
        {
            Id = id,
            PropertyId = property,
            OrgId = org,
            Status = LeaseStatus.Registered,
            StartDate = start,
            EndDate = end,
            MonthlyRent = 800m,
            CreatedAt = SeedAt,
            UpdatedAt = SeedAt,
        });

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_AddLeaseTermLifecycle", StringComparison.Ordinal));
        Assert.True(index > 0, "AddLeaseTermLifecycle migration not found.");
        return all[index - 1];
    }
}
