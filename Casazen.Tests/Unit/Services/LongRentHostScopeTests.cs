using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// LR-01 on the scenario of AM-03: the lease list, the rent register, the agenda and the overview of the long-term area show each
/// caller only the properties it reaches, whatever the shape of its <see cref="HostScope"/>: the collaborator «Solo alcuni» (the
/// properties it was given), the owner and the administrators (the whole org), an account in no team (the properties it created),
/// a collaborator who was given nothing (none) and the people of another org (their own). Here on EF InMemory; the same points run
/// on PostgreSQL in <c>LongRentAggregatesPostgresTests</c>.
/// </summary>
public class LongRentHostScopeTests : IAsyncLifetime
{
    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private HostScopeWorld _world = null!;

    public async Task InitializeAsync()
    {
        _world = await HostScopeScenario.SeedAsync(_db);
        await LongRentHostScopePoints.SeedAsync(_db, _world);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public Task TheSeed_HoldsTogether_EveryKeyHasItsRowAndNoUniqueIndexIsRepeated() =>
        HostScopeScenario.AssertReferentialIntegrityAsync(_db);

    [Fact]
    public Task LeaseList_EveryCallerSeesOnlyTheLeasesOfItsProperties() => LongRentHostScopePoints.LeaseListAsync(_db, _world);

    [Fact]
    public Task RentRegister_TheRowsAndTheNumbersOfTheMonthAreOnlyThoseOfItsProperties() => LongRentHostScopePoints.RentRegisterAsync(_db, _world);

    [Fact]
    public Task Deadlines_TheAgendaHoldsOnlyTheDeadlinesOfItsProperties() => LongRentHostScopePoints.DeadlinesAsync(_db, _world);

    [Fact]
    public Task Overview_TheNumbersTheChecklistAndTheNextDeadlineAreOnlyThoseOfItsProperties() => LongRentHostScopePoints.OverviewAsync(_db, _world);
}

/// <summary>
/// What every caller of the long-term aggregates must see on the scenario of AM-03 (<see cref="HostScopeScenario"/>) once each
/// property has, besides the draft lease the scenario gives it, one registered lease (4+4, ends on 15 December 2026, 900 a month,
/// a tenant whose last name is the name of the property) with three installments: September due on the 5th and not paid (past due
/// on 9 October), October paid on its 2nd, and one due on 20 October still to come. Shared by the InMemory and the PostgreSQL tests.
/// </summary>
internal static class LongRentHostScopePoints
{
    /// <summary>Midnight of 9 October 2026, the day of <see cref="HostScopeScenario.Now"/> in Rome.</summary>
    private static readonly DateTime TodayInRome = new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);

    private static readonly IConfiguration NoConfiguration = new ConfigurationBuilder().Build();

    /// <summary>Who is who in the assertions: the scope of a caller and the properties it must see.</summary>
    private static IReadOnlyList<(string Name, HostScope Scope, Guid[] Reaches)> Callers(HostScopeWorld world) =>
    [
        ("the collaborator «Solo alcuni»", world.Restricted, [world.Granted.Id]),
        ("the owner of the org", world.OrgWide, [world.Granted.Id, world.Hidden.Id]),
        ("an account in no team that created the properties", world.OwnedByOwner, [world.Granted.Id, world.Hidden.Id]),
        ("a collaborator who was given nothing", new HostScope(world.OrgId, GrantedToUserId: "auth0|nessun-immobile"), []),
        ("a person of the other org", new HostScope(world.OtherOrgId), [world.OtherOrgProperty.Id]),
    ];

    public static async Task SeedAsync(AppDbContext db, HostScopeWorld world)
    {
        foreach (var property in new[] { world.Granted, world.Hidden, world.OtherOrgProperty })
        {
            var lease = new LeaseContract
            {
                OrgId = property.OrgId,
                PropertyId = property.Id,
                Status = LeaseStatus.Registered,
                StartDate = HostScopeScenario.Day(2025, 1, 1),
                EndDate = HostScopeScenario.Day(2026, 12, 15),
                MonthlyRent = 900m,
                Parties =
                [
                    new Party { Role = PartyRole.Landlord, FirstName = "Mario", LastName = "Rossi", FiscalCode = "RSSMRA80A01H501U", Citizenship = "IT", ContactEmail = "mario@landlords.example" },
                    new Party { Role = PartyRole.Tenant, FirstName = "Giulia", LastName = property.Name, FiscalCode = "VRDGLI85B02F205A", Citizenship = "IT", ContactEmail = $"giulia.{Guid.NewGuid():N}@tenants.example" },
                ],
            };
            lease.SetContractTerms(LeaseContractType.Libero, LeaseTaxRegime.CedolareSecca);
            var schedule = new RentSchedule
            {
                OrgId = property.OrgId,
                LeaseContractId = lease.Id,
                Cadence = RentCadence.Monthly,
                BillingDayOfMonth = 5,
                Amount = 900m,
                NextRunDate = new DateOnly(2026, 11, 5),
                IsActive = true,
            };
            db.LeaseContracts.Add(lease);
            db.RentSchedules.Add(schedule);
            db.RentLedgerEntries.AddRange(
                Installment(property, lease, schedule, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 5), RentLedgerStatus.Scheduled),
                Installment(property, lease, schedule, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2), RentLedgerStatus.Paid),
                Installment(property, lease, schedule, new DateOnly(2026, 11, 1), new DateOnly(2026, 10, 20), RentLedgerStatus.Scheduled));
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static RentLedgerEntry Installment(
        Property property, LeaseContract lease, RentSchedule schedule, DateOnly period, DateOnly due, RentLedgerStatus status)
    {
        var entry = new RentLedgerEntry
        {
            OrgId = property.OrgId,
            LeaseContractId = lease.Id,
            RentScheduleId = schedule.Id,
            PeriodStart = period,
            PeriodEnd = period.AddMonths(1).AddDays(-1),
            DueDate = due,
            AmountDue = 900m,
            Status = status,
        };
        if (status == RentLedgerStatus.Paid)
        {
            entry.PaidVia = RentPaymentChannel.Offline;
            entry.PaidOn = due;
            entry.PaidAt = HostScopeScenario.Now.UtcDateTime.AddDays(-5);
        }

        return entry;
    }

    // --- The points -------------------------------------------------------------------------------------

    public static async Task LeaseListAsync(AppDbContext db, HostScopeWorld world)
    {
        var repository = new LeaseContractRepository(db);
        var failures = new Failures();
        var everyProperty = new[] { world.Granted, world.Hidden, world.OtherOrgProperty };

        foreach (var (name, scope, reaches) in Callers(world))
        {
            var all = await repository.GetSummariesAsync(scope, new LeaseListQuery(), TodayInRome);
            failures.Expect(SameSet(all.Select(l => l.PropertyId), reaches), $"{name}: the lease list shows {Describe(all.Select(l => l.PropertyId), world)}");
            // The draft of the scenario and the registered lease of this seed, for each property.
            failures.Expect(all.Count == 2 * reaches.Length, $"{name}: {all.Count} leases, expected {2 * reaches.Length}");

            var active = await repository.GetSummariesAsync(scope, new LeaseListQuery(View: LeaseListView.Active), TodayInRome);
            failures.Expect(
                active.Count == reaches.Length && active.All(l => l.Status == LeaseStatus.Registered && l.TenantFirstName == "Giulia"),
                $"{name}: the active view has {active.Count} leases, expected {reaches.Length} registered ones with their first tenant");
            failures.Expect(
                active.All(l => l.TenantLastName == everyProperty.Single(p => p.Id == l.PropertyId).Name && l.OverdueRentCount == 1 && l.OverdueRentAmount == 900m),
                $"{name}: a registered lease carries its first tenant and its one overdue installment");

            // A property the caller does not reach can neither be asked for by its id nor be found by its name or by the name of its tenant.
            foreach (var other in everyProperty.Where(p => !reaches.Contains(p.Id)))
            {
                var byId = await repository.GetSummariesAsync(scope, new LeaseListQuery(PropertyId: other.Id), TodayInRome);
                failures.Expect(byId.Count == 0, $"{name}: asking for the property {other.Name} by id gives {byId.Count} leases");
                var byName = await repository.GetSummariesAsync(scope, new LeaseListQuery(Search: other.Name), TodayInRome);
                failures.Expect(byName.Count == 0, $"{name}: looking for {other.Name} finds {byName.Count} leases");
            }
        }

        failures.Done();
    }

    public static async Task RentRegisterAsync(AppDbContext db, HostScopeWorld world)
    {
        var register = new RentRegisterService(db, NoConfiguration, new FixedTimeProvider(HostScopeScenario.Now));
        var failures = new Failures();

        foreach (var (name, scope, reaches) in Callers(world))
        {
            var n = reaches.Length;

            // October: the installment paid on the 2nd and the one due on the 20th, for each property.
            var october = await register.GetRegisterAsync(scope, new RentRegisterQuery(new DateOnly(2026, 10, 1)));
            failures.Expect(SameSet(october.Items.Select(i => i.PropertyId), reaches), $"{name}: the register of October shows {Describe(october.Items.Select(i => i.PropertyId), world)}");
            failures.Expect(october.Total == 2 * n, $"{name}: {october.Total} installments in October, expected {2 * n}");
            failures.Expect(october.Counters.Expected == new RentCounter(2 * n, 1800m * n), $"{name}: expected in October is {october.Counters.Expected}");
            failures.Expect(october.Counters.Collected == new RentCounter(n, 900m * n), $"{name}: collected in October is {october.Counters.Collected}");
            failures.Expect(october.Counters.Pending == new RentCounter(n, 900m * n), $"{name}: pending in October is {october.Counters.Pending}");
            failures.Expect(october.Counters.Overdue == new RentCounter(0, 0m), $"{name}: overdue in October is {october.Counters.Overdue}");

            // September: the installment not paid, past due, for each property.
            var september = await register.GetRegisterAsync(scope, new RentRegisterQuery(new DateOnly(2026, 9, 1), RentRegisterStatus.Overdue));
            failures.Expect(
                september.Total == n && september.Items.Count == n && september.Items.All(i => i.IsOverdue && i.DaysOverdue == 34),
                $"{name}: {september.Total} overdue installments in September, expected {n}");
            failures.Expect(september.Counters.Overdue == new RentCounter(n, 900m * n), $"{name}: overdue in September is {september.Counters.Overdue}");

            // The installments of the scope are the ones it can remind; the others are not found.
            var found = await register.FindInstallmentsAsync(october.Items.Concat(september.Items).Select(i => i.Id).ToList());
            failures.Expect(found.Count == 3 * n, $"{name}: {found.Count} installments found by id, expected {3 * n}");
        }

        failures.Done();
    }

    public static async Task DeadlinesAsync(AppDbContext db, HostScopeWorld world)
    {
        var agenda = new LongRentAgendaService(db, new FixedTimeProvider(HostScopeScenario.Now));
        var failures = new Failures();
        var window = new LongRentDeadlinesQuery(new DateOnly(2026, 10, 9), new DateOnly(2027, 1, 7));

        foreach (var (name, scope, reaches) in Callers(world))
        {
            var n = reaches.Length;
            var result = await agenda.GetDeadlinesAsync(scope, window);

            failures.Expect(SameSet(result.Items.Select(i => i.PropertyId), reaches), $"{name}: the agenda shows {Describe(result.Items.Select(i => i.PropertyId), world)}");
            // For each property: the installment of September (overdue), the one of 20 October and the end of the lease on 15 December.
            failures.Expect(result.Items.Count == 3 * n, $"{name}: {result.Items.Count} deadlines, expected {3 * n}");
            failures.Expect(result.Items.Count(i => i.Type == LongRentDeadlineType.Rent && i.IsOverdue) == n, $"{name}: overdue installments in the agenda");
            failures.Expect(result.Items.Count(i => i.Type == LongRentDeadlineType.Rent && !i.IsOverdue) == n, $"{name}: installments to come in the agenda");
            failures.Expect(result.Items.Count(i => i.Type == LongRentDeadlineType.LeaseEnd) == n, $"{name}: ends of lease in the agenda");
            failures.Expect(!result.Truncated, $"{name}: the agenda says it was cut");
        }

        failures.Done();
    }

    public static async Task OverviewAsync(AppDbContext db, HostScopeWorld world)
    {
        var agenda = new LongRentAgendaService(db, new FixedTimeProvider(HostScopeScenario.Now));
        var failures = new Failures();

        foreach (var (name, scope, reaches) in Callers(world))
        {
            var n = reaches.Length;
            var overview = await agenda.GetOverviewAsync(scope);

            // The registered lease of each property is active and ends within six months; its draft is waiting for the signature.
            failures.Expect(
                overview.Leases == new LongRentLeaseCounters(Active: n, Expiring: n, InPreparation: n, ToSign: n, ToRegister: 0, Ended: 0),
                $"{name}: leases are {overview.Leases}");
            failures.Expect(overview.Rents.Expected == new RentCounter(2 * n, 1800m * n), $"{name}: the rent of the month expected is {overview.Rents.Expected}");
            failures.Expect(overview.Rents.Collected == new RentCounter(n, 900m * n), $"{name}: the rent of the month collected is {overview.Rents.Collected}");

            var overdue = overview.Checklist.SingleOrDefault(c => c.Kind == LongRentChecklistKind.RentOverdue);
            failures.Expect(
                n == 0 ? overdue is null : overdue is { Count: var count, Amount: var amount } && count == n && amount == 900m * n,
                $"{name}: the overdue rent of the checklist is {overdue}");
            var toSign = overview.Checklist.SingleOrDefault(c => c.Kind == LongRentChecklistKind.LeaseToSign);
            failures.Expect(n == 0 ? toSign is null : toSign?.Count == n, $"{name}: the leases to sign of the checklist are {toSign}");

            failures.Expect(
                n == 0 ? overview.NextDeadline is null : overview.NextDeadline is { } next && reaches.Contains(next.PropertyId) && next.Date == new DateOnly(2026, 10, 20),
                $"{name}: the next deadline is {overview.NextDeadline}");
        }

        failures.Done();
    }

    // --- Helpers ----------------------------------------------------------------------------------------

    private static bool SameSet(IEnumerable<Guid> seen, Guid[] expected) => seen.ToHashSet().SetEquals(expected);

    private static string Describe(IEnumerable<Guid> propertyIds, HostScopeWorld world)
    {
        var names = new Dictionary<Guid, string>
        {
            [world.Granted.Id] = world.Granted.Name,
            [world.Hidden.Id] = world.Hidden.Name,
            [world.OtherOrgProperty.Id] = world.OtherOrgProperty.Name,
        };
        var seen = propertyIds.Distinct().Select(id => names.GetValueOrDefault(id, id.ToString())).Order().ToList();
        return seen.Count == 0 ? "nothing" : string.Join(", ", seen);
    }

    /// <summary>Every point runs all the callers and reports all that is wrong together, so one run tells everything.</summary>
    private sealed class Failures
    {
        private readonly List<string> _items = [];

        public void Expect(bool holds, string whenNot)
        {
            if (!holds)
                _items.Add(whenNot);
        }

        public void Done() => Assert.True(_items.Count == 0, string.Join(Environment.NewLine, _items));
    }
}
