using System.Data.Common;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// LR-01 on real PostgreSQL, what InMemory cannot prove: the lease list, the rent register, the agenda and the overview send the
/// same few commands whatever the number of leases and installments (no query per lease, none per row: the first tenant and the
/// rent ledger are part of the one statement), and the reminders take turns under the lock of the lease: eight at the same moment
/// send one, and a reminder that meets a payment at the same moment never reminds an installment that was paid. The data-level
/// assertions (who sees what, the numbers, the order, the emails) are the integration tests of the same names, which run on
/// PostgreSQL in CI as well.
/// </summary>
public class LongRentAggregatesPostgresTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Today = new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
    private static readonly IConfiguration NoConfiguration = new ConfigurationBuilder().Build();

    private PostgresTestDatabase? _database;
    private Guid _orgId;
    private const string OwnerId = "auth0|lr01-postgres-owner";

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        await using var db = _database.CreateContext();
        await db.Database.MigrateAsync();
        var org = new OrgEntity { Name = "Casa Rossi", Slug = $"casa-rossi-{Guid.NewGuid():N}", DisplayName = "Casa Rossi", ContactEmail = "info@casarossi.example", IsActive = true };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        _orgId = org.Id;
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    // --- One command, whatever the size -------------------------------------------------------------------

    /// <summary>Counts the commands a context sends to the server.</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => _count;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecuting(command, eventData, result);
        }
    }

    private AppDbContext NewCountingContext(CommandCounter counter) =>
        new(new DbContextOptionsBuilder<AppDbContext>(_database!.CreateOptions()).AddInterceptors(counter).Options);

    /// <summary>The commands each call sends, by name, for the owner of the properties (a restricted scope).</summary>
    private async Task<Dictionary<string, int>> CommandsPerCallAsync()
    {
        var scope = new HostScope(_orgId, OwnerId);
        var clock = new FixedTimeProvider(Now);
        var calls = new Dictionary<string, Func<AppDbContext, Task>>
        {
            ["lease list"] = db => new LeaseContractRepository(db).GetSummariesAsync(scope, new LeaseListQuery(View: LeaseListView.Active, Search: "verdi"), Today),
            ["rent register"] = db => new RentRegisterService(db, NoConfiguration, clock).GetRegisterAsync(scope, new RentRegisterQuery(new DateOnly(2026, 10, 1), RentRegisterStatus.Overdue, 1, 25)),
            ["deadlines"] = db => new LongRentAgendaService(db, clock).GetDeadlinesAsync(scope, new LongRentDeadlinesQuery(new DateOnly(2026, 10, 9), new DateOnly(2027, 1, 7))),
            ["overview"] = db => new LongRentAgendaService(db, clock).GetOverviewAsync(scope),
            ["find installments"] = async db => await new RentRegisterService(db, NoConfiguration, clock).FindInstallmentsAsync(Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToList()),
        };

        var counts = new Dictionary<string, int>();
        foreach (var (name, call) in calls)
        {
            var counter = new CommandCounter();
            await using var db = NewCountingContext(counter);
            await call(db);
            counts[name] = counter.Count;
        }

        return counts;
    }

    [PostgresFact]
    public async Task Calls_TheNumberOfCommands_DoesNotGrowWithTheNumberOfLeasesNorOfInstallments()
    {
        await AddLeasesAsync(3);
        var before = await CommandsPerCallAsync();

        // The lease list is one statement, the register the numbers of the month + the page + the org, the agenda the leases + the
        // installments, the overview five; the lookup of the ids one.
        Assert.Equal(
            new Dictionary<string, int> { ["lease list"] = 1, ["rent register"] = 3, ["deadlines"] = 2, ["overview"] = 5, ["find installments"] = 1 },
            before);

        // Ten times the leases, each with its tenants, its schedule and twelve installments.
        await AddLeasesAsync(27);
        var after = await CommandsPerCallAsync();

        Assert.Equal(before, after);
    }

    [PostgresFact]
    public async Task LeaseList_OnARealDatabase_GivesTheTenantAndTheRentOfEachLease()
    {
        var leaseIds = await AddLeasesAsync(5);
        await using var db = _database!.CreateContext();

        var rows = await new LeaseContractRepository(db)
            .GetSummariesAsync(new HostScope(_orgId, OwnerId), new LeaseListQuery(), Today);

        Assert.Equal(5, rows.Count);
        Assert.Equivalent(leaseIds, rows.Select(r => r.Id));
        Assert.All(rows, r =>
        {
            Assert.Equal("Giulia", r.TenantFirstName);
            Assert.Equal("Verdi", r.TenantLastName);
            // September and October unpaid and past due; November and December to come; the earlier months paid.
            Assert.Equal(new DateOnly(2026, 9, 5), r.NextRentDueDate);
            Assert.Equal(2, r.OverdueRentCount);
            Assert.Equal(1800m, r.OverdueRentAmount);
            Assert.Equal(34, r.OverdueDays);
        });
    }

    // --- The reminders take turns ---------------------------------------------------------------------------

    private RentBillingService NewBilling(AppDbContext db, RecordingEmailQueue emails, TimeProvider clock) => new(
        db,
        Mock.Of<IStripeService>(),
        emails,
        EmailTestHelpers.Links(),
        NoConfiguration,
        NullLogger<RentBillingService>.Instance,
        clock);

    [PostgresFact]
    public async Task Reminder_EightAtTheSameMoment_SendOneAndRefuseTheOthersAsTooSoon()
    {
        var (leaseId, installments) = await AddOneLeaseWithOpenInstallmentsAsync(1);
        var emails = new RecordingEmailQueue();
        var clock = new FixedTimeProvider(Now);

        async Task<string> TryAsync()
        {
            await using var db = _database!.CreateContext();
            try
            {
                await NewBilling(db, emails, clock).SendReminderAsync(leaseId, installments[0], "Un promemoria", CancellationToken.None);
                return "sent";
            }
            catch (DomainRuleException ex)
            {
                return ex.Code;
            }
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(TryAsync)));

        Assert.Equal(1, results.Count(r => r == "sent"));
        Assert.All(results.Where(r => r != "sent"), r => Assert.Equal("rent_reminder_too_soon", r));
        Assert.Single(emails.Snapshot());
        await using var check = _database!.CreateContext();
        var stored = await check.RentLedgerEntries.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == installments[0]);
        Assert.Equal(1, stored.ReminderCount);
        Assert.Equal(Now.UtcDateTime, stored.LastReminderAt);
    }

    [PostgresFact]
    public async Task Reminder_AndAPaymentAtTheSameMoment_NeverRemindsAnInstallmentThatWasPaid()
    {
        const int rounds = 6;
        var (leaseId, installments) = await AddOneLeaseWithOpenInstallmentsAsync(rounds);
        var clock = new FixedTimeProvider(Now);

        for (var round = 0; round < rounds; round++)
        {
            var installment = installments[round];
            var emails = new RecordingEmailQueue();

            async Task<string> RemindAsync()
            {
                await using var db = _database!.CreateContext();
                try
                {
                    await NewBilling(db, emails, clock).SendReminderAsync(leaseId, installment, null, CancellationToken.None);
                    return "sent";
                }
                catch (DomainException ex)
                {
                    return ex.Code;
                }
            }

            async Task<string> PayAsync()
            {
                await using var db = _database!.CreateContext();
                try
                {
                    await NewBilling(db, emails, clock).MarkPaidOfflineAsync(
                        leaseId, installment, new MarkRentPaidOfflineRequest(new DateOnly(2026, 10, 9), null), "auth0|lr01", CancellationToken.None);
                    return "paid";
                }
                catch (DomainException ex)
                {
                    return ex.Code;
                }
            }

            var outcomes = await Task.WhenAll(Task.Run(RemindAsync), Task.Run(PayAsync));

            // The payment always lands; the reminder either went out before it (and then it is recorded) or met the paid installment.
            Assert.Equal("paid", outcomes[1]);
            await using var check = _database!.CreateContext();
            var stored = await check.RentLedgerEntries.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == installment);
            Assert.Equal(RentLedgerStatus.Paid, stored.Status);
            if (outcomes[0] == "sent")
            {
                Assert.Single(emails.Snapshot());
                Assert.Equal(1, stored.ReminderCount);
            }
            else
            {
                Assert.Equal("rent_installment_not_payable", outcomes[0]);
                Assert.Empty(emails.Snapshot());
                Assert.Equal(0, stored.ReminderCount);
            }
        }
    }

    [PostgresFact]
    public async Task Reminder_TheTimeRecorded_IsTheOneTheDatabaseKeeps_SoTheNextOneIsAllowedExactlyAfterTheInterval()
    {
        var (leaseId, installments) = await AddOneLeaseWithOpenInstallmentsAsync(1);
        var emails = new RecordingEmailQueue();
        // 08:00:00.1234567: PostgreSQL keeps microseconds (.123456), the clock has 100 nanoseconds.
        var clock = new FakeTimeProvider(Now.AddTicks(1_234_567));

        await using (var db = _database!.CreateContext())
            await NewBilling(db, emails, clock).SendReminderAsync(leaseId, installments[0], null);

        await using var check = _database!.CreateContext();
        var stored = await check.RentLedgerEntries.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == installments[0]);
        Assert.Equal(0, stored.LastReminderAt!.Value.Ticks % 10);

        // Exactly the interval after the recorded instant: allowed. One microsecond before: not.
        clock.SetUtcNow(new DateTimeOffset(stored.LastReminderAt.Value.AddHours(24).AddTicks(-10), TimeSpan.Zero));
        await using (var db = _database.CreateContext())
        {
            var tooSoon = await Assert.ThrowsAsync<DomainRuleException>(() => NewBilling(db, emails, clock).SendReminderAsync(leaseId, installments[0], null));
            Assert.Equal("rent_reminder_too_soon", tooSoon.Code);
        }

        clock.SetUtcNow(new DateTimeOffset(stored.LastReminderAt.Value.AddHours(24), TimeSpan.Zero));
        await using (var db = _database.CreateContext())
            Assert.Equal(2, (await NewBilling(db, emails, clock).SendReminderAsync(leaseId, installments[0], null)).ReminderCount);
    }

    // --- seeding ----------------------------------------------------------------------------------------

    /// <summary>
    /// <paramref name="count"/> more leases of the owner, each with its tenant, its schedule and twelve installments of 2026: January to
    /// August paid, September to December to collect (September and October are past due on 9 October).
    /// </summary>
    private async Task<List<Guid>> AddLeasesAsync(int count)
    {
        await using var db = _database!.CreateContext();
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var property = new Property
            {
                OrgId = _orgId,
                OwnerId = OwnerId,
                Name = $"Casa {Guid.NewGuid():N}",
                Description = "Long-term",
                Address = $"Via Lunga {Guid.NewGuid():N}",
                City = "Bari",
                PostalCode = "70121",
                Bedrooms = 2,
                Bathrooms = 1,
                RentalMode = RentalMode.Long,
            };
            var lease = NewLease(property);
            db.Properties.Add(property);
            db.LeaseContracts.Add(lease);
            var schedule = NewSchedule(lease);
            db.RentSchedules.Add(schedule);
            for (var month = 1; month <= 12; month++)
                db.RentLedgerEntries.Add(NewInstallment(lease, schedule, month, month <= 8 ? RentLedgerStatus.Paid : RentLedgerStatus.Scheduled));
            ids.Add(lease.Id);
        }

        await db.SaveChangesAsync();
        return ids;
    }

    /// <summary>One lease with <paramref name="count"/> open installments (January, February… of 2026: all past due on 9 October).</summary>
    private async Task<(Guid LeaseId, List<Guid> Installments)> AddOneLeaseWithOpenInstallmentsAsync(int count)
    {
        await using var db = _database!.CreateContext();
        var property = new Property
        {
            OrgId = _orgId,
            OwnerId = OwnerId,
            Name = $"Casa {Guid.NewGuid():N}",
            Description = "Long-term",
            Address = $"Via Lunga {Guid.NewGuid():N}",
            City = "Bari",
            PostalCode = "70121",
            Bedrooms = 2,
            Bathrooms = 1,
            RentalMode = RentalMode.Long,
        };
        var lease = NewLease(property);
        var schedule = NewSchedule(lease);
        db.Properties.Add(property);
        db.LeaseContracts.Add(lease);
        db.RentSchedules.Add(schedule);
        var installments = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var entry = NewInstallment(lease, schedule, 1 + i, RentLedgerStatus.Scheduled);
            db.RentLedgerEntries.Add(entry);
            installments.Add(entry.Id);
        }

        await db.SaveChangesAsync();
        return (lease.Id, installments);
    }

    private LeaseContract NewLease(Property property)
    {
        var lease = new LeaseContract
        {
            PropertyId = property.Id,
            OrgId = _orgId,
            Status = LeaseStatus.Registered,
            StartDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2029, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 900m,
            Parties =
            [
                new Party { Role = PartyRole.Landlord, FirstName = "Mario", LastName = "Rossi", FiscalCode = "RSSMRA80A01H501U", Citizenship = "IT", ContactEmail = "mario@landlords.example" },
                new Party { Role = PartyRole.Tenant, FirstName = "Giulia", LastName = "Verdi", FiscalCode = "VRDGLI85B02F205A", Citizenship = "IT", ContactEmail = $"giulia.{Guid.NewGuid():N}@tenants.example" },
            ],
        };
        lease.SetContractTerms(LeaseContractType.Libero, LeaseTaxRegime.CedolareSecca);
        return lease;
    }

    private RentSchedule NewSchedule(LeaseContract lease) => new()
    {
        OrgId = _orgId,
        LeaseContractId = lease.Id,
        Cadence = RentCadence.Monthly,
        BillingDayOfMonth = 5,
        Amount = 900m,
        NextRunDate = new DateOnly(2026, 1, 5),
        IsActive = true,
    };

    private RentLedgerEntry NewInstallment(LeaseContract lease, RentSchedule schedule, int month, RentLedgerStatus status)
    {
        var first = new DateOnly(2026, month, 1);
        var entry = new RentLedgerEntry
        {
            OrgId = _orgId,
            LeaseContractId = lease.Id,
            RentScheduleId = schedule.Id,
            PeriodStart = first,
            PeriodEnd = first.AddMonths(1).AddDays(-1),
            DueDate = new DateOnly(2026, month, 5),
            AmountDue = 900m,
            Status = status,
        };
        if (status == RentLedgerStatus.Paid)
        {
            entry.PaidVia = RentPaymentChannel.Offline;
            entry.PaidOn = entry.DueDate;
            entry.PaidAt = Now.UtcDateTime.AddDays(-30);
        }

        return entry;
    }
}
