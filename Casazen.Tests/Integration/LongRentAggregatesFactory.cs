using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Tests.Unit;
using Casazen.Tests.Unit.Email;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Casazen.Tests.Integration;

/// <summary>
/// The host of the long-term aggregates (LR-01): a clock the tests control (10:00 in Rome on 9 October 2026) and an email queue that
/// records what is queued, over the usual integration host (PostgreSQL in CI, EF InMemory in a local run).
/// </summary>
public sealed class LongRentAggregatesFactory : CasazenWebApplicationFactory
{
    /// <summary>10:00 in Rome on 9 October 2026 (daylight saving time): "today" of every test is 2026-10-09.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);

    public FakeTimeProvider Clock { get; } = new(Now);

    internal RecordingEmailQueue Emails { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            RemoveAllOf<TimeProvider>(services);
            services.AddSingleton<TimeProvider>(Clock);
            RemoveAllOf<IEmailQueue>(services);
            services.AddSingleton<IEmailQueue>(Emails);
        });
    }
}

/// <summary>
/// An org of the long-term area with the leases of every shape the aggregates tell apart, on "today" 2026-10-09 (Rome). One
/// world per test class (or per test, when the test changes it): every id is new, so worlds never see each other.
/// </summary>
/// <remarks>
/// <para>Owner A owns <c>P1</c> and <c>P2</c>; the colleague B (same org, no org-wide role) owns <c>P3</c>.</para>
/// <list type="table">
/// <item><term>Active</term><description><c>P1</c>, registered, 2024-09-01 to 2028-08-31, tenant Giulia Verdi. Rent monthly, due on the 5th:
/// August unpaid (65 days overdue), September paid, October unpaid (4 days overdue), November to come.</description></item>
/// <item><term>Draft</term><description><c>P1</c>, from 2026-11-01, tenants Marco Bianchi and Anna Rossi.</description></item>
/// <item><term>Signed</term><description><c>P2</c>, from 2026-10-01, signed on 2026-09-20 (registration due 2026-10-20), tenant Luca Neri, extra-EU,
/// Questura communication not declared (due 2026-10-03: overdue).</description></item>
/// <item><term>Ended</term><description><c>P2</c>, registered, ended on 2026-06-30, tenant Marta Colucci.</description></item>
/// <item><term>Expiring</term><description><c>P2</c>, registered, ends 2026-11-30, tenant Francesco Valli. October paid (700, due 10-02), a cancelled installment of the same month.</description></item>
/// <item><term>Anonymized</term><description><c>P1</c>, registered, ends 2027-03-31, tenant anonymized (LT-12).</description></item>
/// <item><term>Transitory</term><description><c>P2</c>, registered, 2026-05-01 to 2027-02-28, tenant Sara Gallo. October (500, due 10-07) being processed.</description></item>
/// <item><term>Long</term><description><c>P1</c>, registered, 2023-06-01 to 2027-05-31 (notice by 2026-11-30), tenant Paolo De Santis.</description></item>
/// <item><term>Colleague</term><description><c>P3</c> (owner B), registered, tenant Valentina Marra. September paid, October due 10-10 (not overdue).</description></item>
/// </list>
/// </remarks>
internal sealed class LongRentWorld
{
    public const string TenantEmailSuffix = "@tenants.example";

    /// <summary>Makes every name and address of the world its own: the emails of one world are never mistaken for another's.</summary>
    public required string Tag { get; init; }

    public required string OwnerId { get; init; }

    public required string ColleagueId { get; init; }

    public required Guid OrgId { get; init; }

    public required Guid P1 { get; init; }

    public required Guid P2 { get; init; }

    public required Guid P3 { get; init; }

    public required Guid Active { get; init; }

    public required Guid Draft { get; init; }

    public required Guid Signed { get; init; }

    public required Guid Ended { get; init; }

    public required Guid Expiring { get; init; }

    public required Guid Anonymized { get; init; }

    public required Guid Transitory { get; init; }

    public required Guid Long { get; init; }

    public required Guid Colleague { get; init; }

    /// <summary>Installments by name: <c>ActiveAug</c>, <c>ActiveSep</c>, <c>ActiveOct</c>, <c>ActiveNov</c>, <c>ExpiringOct</c>, <c>ExpiringCancelled</c>, <c>TransitoryOct</c>, <c>ColleagueSep</c>, <c>ColleagueOct</c>.</summary>
    public required IReadOnlyDictionary<string, Guid> Installments { get; init; }

    public Guid Installment(string name) => Installments[name];

    public string OrgName => $"Org {OwnerId}";

    /// <summary>The email address of a tenant of this world (<c>first.last.tag@tenants.example</c>).</summary>
    public string EmailOf(string first, string last) => TenantEmail(Tag, first, last);

    public static string TenantEmail(string tag, string first, string last) => $"{first}.{last}.{tag}{TenantEmailSuffix}".ToLowerInvariant();

    public static async Task<LongRentWorld> SeedAsync(CasazenWebApplicationFactory factory, bool connect = false)
    {
        var tag = Guid.NewGuid().ToString("N")[..10];
        var ownerId = $"auth0|lr01-owner-{tag}";
        var colleagueId = $"auth0|lr01-colleague-{tag}";
        var org = await factory.SeedOrgForOwnerAsync(ownerId);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var legal = scope.ServiceProvider.GetRequiredService<ILegalDocumentService>();

        if (connect)
        {
            var tracked = await db.Orgs.SingleAsync(o => o.Id == org.Id);
            tracked.StripeConnectedAccountId = $"acct_lr01_{tag}";
            tracked.ConnectChargesEnabled = true;
        }

        var colleague = new User
        {
            Id = colleagueId,
            Email = $"{Guid.NewGuid():N}@example.com",
            FirstName = "Collega",
            LastName = "Stessa Org",
            OrgId = org.Id,
            IsActive = true,
        };
        db.Users.Add(colleague);
        await HostOnboardingSeed.MarkOnboardedAsync(db, colleague, org.Id, legal);

        var p1 = NewProperty(org.Id, ownerId, "Bilocale Sparano");
        var p2 = NewProperty(org.Id, ownerId, "Trilocale Murat");
        var p3 = NewProperty(org.Id, colleagueId, "Casa Corso");
        db.Properties.AddRange(p1, p2, p3);

        var installments = new Dictionary<string, Guid>();
        LeaseContract Lease(
            Property property,
            LeaseStatus status,
            string start,
            string end,
            Party[] parties,
            LeaseContractType type = LeaseContractType.Libero,
            string? stipula = null,
            decimal rent = 900m)
        {
            var lease = new LeaseContract
            {
                PropertyId = property.Id,
                OrgId = org.Id,
                Status = status,
                StartDate = Day(start),
                EndDate = Day(end),
                MonthlyRent = rent,
                Parties = parties.ToList(),
            };
            lease.SetContractTerms(type, LeaseTaxRegime.CedolareSecca);
            if (stipula is not null)
                lease.RecordStipula(Day(stipula));
            db.LeaseContracts.Add(lease);
            return lease;
        }

        RentLedgerEntry Rent(
            RentSchedule schedule, string name, LeaseContract lease, string period, string due, decimal amount, RentLedgerStatus status)
        {
            var entry = new RentLedgerEntry
            {
                OrgId = org.Id,
                LeaseContractId = lease.Id,
                RentScheduleId = schedule.Id,
                PeriodStart = DateOnly.Parse(period),
                PeriodEnd = DateOnly.Parse(period).AddMonths(1).AddDays(-1),
                DueDate = DateOnly.Parse(due),
                AmountDue = amount,
                Status = status,
            };
            if (status == RentLedgerStatus.Paid)
            {
                entry.PaidVia = RentPaymentChannel.Offline;
                entry.PaidOn = DateOnly.Parse(due).AddDays(1);
                entry.PaidAt = LongRentAggregatesFactory.Now.UtcDateTime.AddDays(-30);
            }

            installments[name] = entry.Id;
            db.RentLedgerEntries.Add(entry);
            return entry;
        }

        RentSchedule Schedule(LeaseContract lease, decimal amount)
        {
            var schedule = new RentSchedule
            {
                OrgId = org.Id,
                LeaseContractId = lease.Id,
                Cadence = RentCadence.Monthly,
                BillingDayOfMonth = 5,
                Amount = amount,
                NextRunDate = new DateOnly(2026, 8, 5),
                IsActive = true,
            };
            db.RentSchedules.Add(schedule);
            return schedule;
        }

        Party Tenant(string first, string last, string citizenship = "IT", int position = 0) => NewTenant(tag, first, last, citizenship, position);

        var active = Lease(p1, LeaseStatus.Registered, "2024-09-01", "2028-08-31", [Landlord(), Tenant("Giulia", "Verdi")]);
        var draft = Lease(p1, LeaseStatus.Draft, "2026-11-01", "2030-10-31", [Landlord(), Tenant("Marco", "Bianchi"), Tenant("Anna", "Rossi", position: 1)]);
        var signed = Lease(
            p2, LeaseStatus.Signed, "2026-10-01", "2030-09-30", [Landlord(), Tenant("Luca", "Neri", citizenship: "US")], stipula: "2026-09-20");
        var ended = Lease(p2, LeaseStatus.Registered, "2021-07-01", "2026-06-30", [Landlord(), Tenant("Marta", "Colucci")]);
        var expiring = Lease(p2, LeaseStatus.Registered, "2022-12-01", "2026-11-30", [Landlord(), Tenant("Francesco", "Valli")], rent: 700m);
        var anonymized = Lease(
            p1, LeaseStatus.Registered, "2023-04-01", "2027-03-31", [Landlord(), AnonymizedTenant(tag)]);
        var transitory = Lease(
            p2, LeaseStatus.Registered, "2026-05-01", "2027-02-28", [Landlord(), Tenant("Sara", "Gallo")], LeaseContractType.Transitorio, rent: 500m);
        var longTerm = Lease(p1, LeaseStatus.Registered, "2023-06-01", "2027-05-31", [Landlord(), Tenant("Paolo", "De Santis")]);
        var colleagueLease = Lease(p3, LeaseStatus.Registered, "2025-01-01", "2028-12-31", [Landlord(), Tenant("Valentina", "Marra")]);

        var activeSchedule = Schedule(active, 900m);
        Rent(activeSchedule, "ActiveAug", active, "2026-08-01", "2026-08-05", 900m, RentLedgerStatus.Scheduled);
        Rent(activeSchedule, "ActiveSep", active, "2026-09-01", "2026-09-05", 900m, RentLedgerStatus.Paid);
        Rent(activeSchedule, "ActiveOct", active, "2026-10-01", "2026-10-05", 900m, RentLedgerStatus.Scheduled);
        Rent(activeSchedule, "ActiveNov", active, "2026-11-01", "2026-11-05", 900m, RentLedgerStatus.Scheduled);

        var expiringSchedule = Schedule(expiring, 700m);
        Rent(expiringSchedule, "ExpiringOct", expiring, "2026-10-01", "2026-10-02", 700m, RentLedgerStatus.Paid);
        Rent(expiringSchedule, "ExpiringCancelled", expiring, "2026-10-16", "2026-10-15", 123m, RentLedgerStatus.Cancelled);

        var transitorySchedule = Schedule(transitory, 500m);
        Rent(transitorySchedule, "TransitoryOct", transitory, "2026-10-01", "2026-10-07", 500m, RentLedgerStatus.Processing);

        var colleagueSchedule = Schedule(colleagueLease, 900m);
        Rent(colleagueSchedule, "ColleagueSep", colleagueLease, "2026-09-01", "2026-09-10", 900m, RentLedgerStatus.Paid);
        Rent(colleagueSchedule, "ColleagueOct", colleagueLease, "2026-10-01", "2026-10-10", 900m, RentLedgerStatus.Scheduled);

        await db.SaveChangesAsync();
        return new LongRentWorld
        {
            Tag = tag,
            OwnerId = ownerId,
            ColleagueId = colleagueId,
            OrgId = org.Id,
            P1 = p1.Id,
            P2 = p2.Id,
            P3 = p3.Id,
            Active = active.Id,
            Draft = draft.Id,
            Signed = signed.Id,
            Ended = ended.Id,
            Expiring = expiring.Id,
            Anonymized = anonymized.Id,
            Transitory = transitory.Id,
            Long = longTerm.Id,
            Colleague = colleagueLease.Id,
            Installments = installments,
        };
    }

    /// <summary>The owner of P1 and P2: only the properties it owns.</summary>
    public HttpClient OwnerClient(CasazenWebApplicationFactory factory) =>
        factory.CreateAuthenticatedClient(OwnerId, "LongTermLandlord");

    /// <summary>A colleague of the same org with no org-wide role: only the properties it owns (P3).</summary>
    public HttpClient ColleagueClient(CasazenWebApplicationFactory factory) =>
        factory.CreateAuthenticatedClient(ColleagueId, "LongTermLandlord");

    /// <summary>A property manager of the org: every property.</summary>
    public HttpClient ManagerClient(CasazenWebApplicationFactory factory) =>
        factory.CreateAuthenticatedClient(ColleagueId, "LongTermLandlord,PropertyManager");

    public static DateTime Day(string isoDate) => DateTime.SpecifyKind(DateTime.Parse(isoDate, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);

    private static Property NewProperty(Guid orgId, string ownerId, string name) => new()
    {
        OwnerId = ownerId,
        OrgId = orgId,
        Name = name,
        Description = "Long-term property of the aggregates tests",
        Address = $"Via Lunga {Guid.NewGuid():N}",
        City = "Bari",
        PostalCode = "70121",
        Bedrooms = 2,
        Bathrooms = 1,
        RentalMode = RentalMode.Long,
        IsActive = true,
    };

    private static Party Landlord() => new()
    {
        Role = PartyRole.Landlord,
        FirstName = "Mario",
        LastName = "Rossi",
        FiscalCode = "RSSMRA80A01H501U",
        Citizenship = "IT",
        ContactEmail = "mario.rossi@landlords.example",
    };

    private static Party NewTenant(string tag, string first, string last, string citizenship = "IT", int position = 0) => new()
    {
        Role = PartyRole.Tenant,
        Position = position,
        FirstName = first,
        LastName = last,
        FiscalCode = "VRDGLI85B02F205A",
        Citizenship = citizenship,
        ContactEmail = TenantEmail(tag, first, last),
        IsExtraEU = !string.Equals(citizenship, "IT", StringComparison.Ordinal),
    };

    private static Party AnonymizedTenant(string tag)
    {
        var party = NewTenant(tag, "Nome", "Cognome");
        Casazen.Infrastructure.Services.LeasePartyPrivacyService.AnonymizeParty(party, LongRentAggregatesFactory.Now.UtcDateTime.AddDays(-20));
        return party;
    }
}
