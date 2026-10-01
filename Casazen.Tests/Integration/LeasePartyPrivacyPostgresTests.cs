using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// LT-12 (A7-18, #179) on real PostgreSQL: the parties of a lease are anonymized only after the lease has ended, from
/// its end date (not its start date) plus the configured period, or the day after the end on an erasure request; nothing
/// without a configured period; a party also in another lease of the org that has not ended keeps its data; idempotent.
/// </summary>
/// <remarks>
/// The tests share one database and the retention run looks at every org: each test checks only its own rows.
/// </remarks>
public class LeasePartyPrivacyPostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string LandlordCf = "RSSMRA80A01H501U";
    private const string TenantCf = "VRDGLI85B42F205W";

    /// <summary>Properties of <see cref="Party"/> that are not personal data of the party, with the reason.</summary>
    private static readonly Dictionary<string, string> PartyNonPersonal = new()
    {
        [nameof(Party.Id)] = "pseudonymous key, kept for the signers",
        [nameof(Party.LeaseContractId)] = "foreign key",
        [nameof(Party.LeaseContract)] = "navigation",
        [nameof(Party.Role)] = "shape of the lease",
        [nameof(Party.IsExtraEU)] = "Questura history of the lease, no identity left",
        [nameof(Party.AnonymizedAt)] = "processing metadata",
    };

    private static readonly RetentionPeriodOptions TwoYears = new() { Years = 2, Source = "test: lease parties" };

    private readonly CasazenWebApplicationFactory _factory;

    public LeasePartyPrivacyPostgresTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task ApplyRetentionAsync_NoPeriodConfigured_AnonymizesNothing()
    {
        var lease = await SeedLeaseAsync(Start(2016, 1, 1), End(2019, 12, 31));

        var result = await RunRetentionAsync(new RetentionPeriodOptions(), Day(2046, 1, 1));

        Assert.False(result.RetentionConfigured);
        var stored = await LoadLeaseAsync(lease.LeaseId);
        Assert.Null(stored.PartiesAnonymizedAt);
        Assert.All(stored.Parties, p => Assert.Null(p.AnonymizedAt));
        Assert.Contains(stored.Parties, p => p.FiscalCode == TenantCf);
    }

    [PostgresFact]
    public async Task ApplyRetentionAsync_PeriodWithoutSource_AnonymizesNothing()
    {
        var lease = await SeedLeaseAsync(Start(2016, 1, 1), End(2019, 12, 31));

        var result = await RunRetentionAsync(new RetentionPeriodOptions { Years = 1 }, Day(2046, 1, 1));

        Assert.False(result.RetentionConfigured);
        Assert.Null((await LoadLeaseAsync(lease.LeaseId)).PartiesAnonymizedAt);
    }

    [PostgresFact]
    public async Task ApplyRetentionAsync_PeriodConfigured_CountsFromTheEndDateAndAppliesOnce()
    {
        // 4+4 lease renewed: start 2026, end 2034. The old rule (start + 10 years) would have hit it in 2036 while still
        // in force had it been renewed again; now it is end + period, i.e. from 2037-01-01.
        var lease = await SeedLeaseAsync(Start(2026, 1, 1), End(2034, 12, 31), withSigners: true);

        await RunRetentionAsync(TwoYears, Day(2028, 1, 2)); // start + 2 years: lease in force
        await RunRetentionAsync(TwoYears, Day(2036, 12, 31)); // end + 2 years: last day of the period
        var stored = await LoadLeaseAsync(lease.LeaseId);
        Assert.Null(stored.PartiesAnonymizedAt);
        Assert.All(stored.Parties, p => Assert.Null(p.AnonymizedAt));

        var result = await RunRetentionAsync(TwoYears, Day(2037, 1, 1));
        Assert.True(result.RetentionConfigured);
        stored = await LoadLeaseAsync(lease.LeaseId);
        Assert.NotNull(stored.PartiesAnonymizedAt);
        Assert.All(stored.Parties, AssertNoPersonalData);
        Assert.Equal([PartyRole.Landlord, PartyRole.Tenant], stored.Parties.Select(p => p.Role).Order());
        Assert.Contains(stored.Parties, p => p.IsExtraEU);

        await using (var scope = NewScope(out var db))
        {
            var signers = await db.LeaseSigners.AsNoTracking().Where(s => s.LeaseContractId == lease.LeaseId).ToListAsync();
            Assert.Equal(2, signers.Count);
            Assert.All(signers, s => Assert.Equal((null, null, null), (s.SigningUrl, s.SigningUrlExpiresAt, s.ExternalSignerId)));
        }

        // Second run: nothing changes, the markers stay as they were.
        await RunRetentionAsync(TwoYears, Day(2037, 1, 2));
        var again = await LoadLeaseAsync(lease.LeaseId);
        Assert.Equal(stored.PartiesAnonymizedAt, again.PartiesAnonymizedAt);
        Assert.Equal(
            stored.Parties.OrderBy(p => p.Id).Select(p => p.AnonymizedAt),
            again.Parties.OrderBy(p => p.Id).Select(p => p.AnonymizedAt));
    }

    [PostgresFact]
    public async Task ApplyRetentionAsync_PartyAlsoInALeaseNotEnded_KeepsThatPartyUntilItEnds()
    {
        var old = await SeedLeaseAsync(Start(2020, 1, 1), End(2023, 12, 31));
        // Same landlord (same fiscal code, written differently), new tenant, lease in force until 2031.
        var current = await SeedLeaseAsync(
            Start(2028, 1, 1), End(2031, 12, 31), ownerId: old.OwnerId, landlordCf: "rssmra80a01h501u", tenantCf: "BNCLRA90C41H501V");

        var result = await RunRetentionAsync(TwoYears, Day(2026, 1, 1));

        Assert.Equal(1, result.PartiesKept);
        var stored = await LoadLeaseAsync(old.LeaseId);
        Assert.Null(stored.PartiesAnonymizedAt);
        var landlord = stored.Parties.Single(p => p.Role == PartyRole.Landlord);
        Assert.Equal((LandlordCf, null), (landlord.FiscalCode, landlord.AnonymizedAt));
        AssertNoPersonalData(stored.Parties.Single(p => p.Role == PartyRole.Tenant));
        Assert.All((await LoadLeaseAsync(current.LeaseId)).Parties, p => Assert.Null(p.AnonymizedAt));

        // Once the other lease has ended too, the landlord of the old lease goes as well.
        await RunRetentionAsync(TwoYears, Day(2032, 1, 1));
        stored = await LoadLeaseAsync(old.LeaseId);
        Assert.NotNull(stored.PartiesAnonymizedAt);
        Assert.All(stored.Parties, AssertNoPersonalData);
    }

    [PostgresFact]
    public async Task ApplyRetentionAsync_SameFiscalCodeInAnotherOrg_DoesNotKeepTheParty()
    {
        var old = await SeedLeaseAsync(Start(2020, 1, 1), End(2023, 12, 31));
        await SeedLeaseAsync(Start(2025, 1, 1), End(2033, 12, 31)); // another owner, another org, same fiscal codes

        await RunRetentionAsync(TwoYears, Day(2026, 1, 1));

        var stored = await LoadLeaseAsync(old.LeaseId);
        Assert.NotNull(stored.PartiesAnonymizedAt);
        Assert.All(stored.Parties, AssertNoPersonalData);
    }

    [PostgresFact]
    public async Task RequestErasureAsync_LeaseNotEnded_SchedulesAndTheJobAnonymizesTheDayAfterTheEnd()
    {
        var lease = await SeedLeaseAsync(Start(2026, 9, 1), End(2030, 8, 31));

        LeaseErasureResult result;
        await using (var scope = NewScope(out var db))
            result = await NewService(db, new RetentionPeriodOptions(), Day(2026, 10, 1)).RequestErasureAsync(lease.LeaseId);

        Assert.Equal(new LeaseErasureResult(LeaseErasureStatus.Scheduled, Start(2030, 9, 1), 0), result);
        var stored = await LoadLeaseAsync(lease.LeaseId);
        Assert.True(stored.ErasureRequested);
        Assert.NotNull(stored.ErasureRequestedAt);
        Assert.Single(stored.Events, e => e.EventType == LeaseEventType.ErasureRequested);
        Assert.All(stored.Parties, p => Assert.Null(p.AnonymizedAt));

        // No retention period configured: the request alone is enough, from the day after the end date.
        await RunRetentionAsync(new RetentionPeriodOptions(), Day(2030, 8, 31));
        Assert.Null((await LoadLeaseAsync(lease.LeaseId)).PartiesAnonymizedAt);

        await RunRetentionAsync(new RetentionPeriodOptions(), Day(2030, 9, 1));
        stored = await LoadLeaseAsync(lease.LeaseId);
        Assert.NotNull(stored.PartiesAnonymizedAt);
        Assert.All(stored.Parties, AssertNoPersonalData);
    }

    [PostgresFact]
    public async Task RequestErasureAsync_EndedLease_AnonymizesNowAndIsIdempotent()
    {
        var lease = await SeedLeaseAsync(Start(2022, 1, 1), End(2025, 12, 31), withSigners: true);

        LeaseErasureResult first, second;
        await using (var scope = NewScope(out var db))
            first = await NewService(db, new RetentionPeriodOptions(), Day(2026, 10, 1)).RequestErasureAsync(lease.LeaseId);
        var afterFirst = await LoadLeaseAsync(lease.LeaseId);
        await using (var scope = NewScope(out var db))
            second = await NewService(db, new RetentionPeriodOptions(), Day(2026, 10, 2)).RequestErasureAsync(lease.LeaseId);

        Assert.Equal(new LeaseErasureResult(LeaseErasureStatus.Anonymized, null, 0), first);
        Assert.Equal(first, second);
        var stored = await LoadLeaseAsync(lease.LeaseId);
        Assert.Equal(afterFirst.PartiesAnonymizedAt, stored.PartiesAnonymizedAt);
        Assert.All(stored.Parties, AssertNoPersonalData);
        Assert.Single(stored.Events, e => e.EventType == LeaseEventType.ErasureRequested);
    }

    [PostgresFact]
    public async Task RequestErasureAsync_EndedLeaseWithAPartyOfALeaseNotEnded_AnonymizesTheOthersOnly()
    {
        var old = await SeedLeaseAsync(Start(2020, 1, 1), End(2023, 12, 31));
        await SeedLeaseAsync(Start(2026, 1, 1), End(2029, 12, 31), ownerId: old.OwnerId, tenantCf: "BNCLRA90C41H501V");

        LeaseErasureResult result;
        await using (var scope = NewScope(out var db))
            result = await NewService(db, new RetentionPeriodOptions(), Day(2026, 10, 1)).RequestErasureAsync(old.LeaseId);

        Assert.Equal(new LeaseErasureResult(LeaseErasureStatus.PartiallyAnonymized, null, 1), result);
        var stored = await LoadLeaseAsync(old.LeaseId);
        Assert.Equal(LandlordCf, stored.Parties.Single(p => p.Role == PartyRole.Landlord).FiscalCode);
        AssertNoPersonalData(stored.Parties.Single(p => p.Role == PartyRole.Tenant));
    }

    private static DateTime Start(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime End(int year, int month, int day) => Start(year, month, day);

    /// <summary>A run at 03:00 UTC of <paramref name="year"/>-<paramref name="month"/>-<paramref name="day"/> (same Rome date).</summary>
    private static DateTimeOffset Day(int year, int month, int day) => new(year, month, day, 3, 0, 0, TimeSpan.Zero);

    private async Task<LeasePartyRetentionRunResult> RunRetentionAsync(RetentionPeriodOptions period, DateTimeOffset at)
    {
        await using var scope = NewScope(out var db);
        return await NewService(db, period, at).ApplyRetentionAsync();
    }

    private static LeasePartyPrivacyService NewService(AppDbContext db, RetentionPeriodOptions period, DateTimeOffset at) =>
        new(
            db,
            Options.Create(new GdprOptions { Retention = new GdprRetentionOptions { LeaseParties = period } }),
            new FakeTimeProvider(at),
            NullLogger<LeasePartyPrivacyService>.Instance);

    private AsyncServiceScope NewScope(out AppDbContext db)
    {
        var scope = _factory.Services.CreateAsyncScope();
        db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return scope;
    }

    private async Task<LeaseContract> LoadLeaseAsync(Guid leaseId)
    {
        await using var scope = NewScope(out var db);
        return await db.LeaseContracts.AsNoTracking()
            .Include(l => l.Parties)
            .Include(l => l.Events)
            .SingleAsync(l => l.Id == leaseId);
    }

    private async Task<(Guid LeaseId, string OwnerId)> SeedLeaseAsync(
        DateTime start,
        DateTime end,
        string? ownerId = null,
        string landlordCf = LandlordCf,
        string tenantCf = TenantCf,
        bool withSigners = false)
    {
        ownerId ??= $"auth0|lt12-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(ownerId);
        await using var scope = NewScope(out var db);

        var landlord = new Party
        {
            Role = PartyRole.Landlord, FirstName = "Mario", LastName = "Rossi", FiscalCode = landlordCf,
            Citizenship = "IT", ContactEmail = "mario.rossi@example.com",
        };
        var tenant = new Party
        {
            Role = PartyRole.Tenant, FirstName = "Giulia", LastName = "Verdi", FiscalCode = tenantCf,
            Citizenship = "US", ContactEmail = "giulia.verdi@example.com", IsExtraEU = true,
        };
        var lease = new LeaseContract
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            Status = LeaseStatus.Signed,
            FiscalRegime = FiscalRegime.CedolareSecca,
            ContractType = LeaseContractType.Libero,
            TaxRegime = LeaseTaxRegime.CedolareSecca,
            StartDate = start,
            EndDate = end,
            MonthlyRent = 900m,
            Parties = [landlord, tenant],
        };
        db.LeaseContracts.Add(lease);
        if (withSigners)
        {
            foreach (var party in lease.Parties)
            {
                db.LeaseSigners.Add(new LeaseSigner
                {
                    OrgId = lease.OrgId,
                    LeaseContractId = lease.Id,
                    PartyId = party.Id,
                    Method = LeaseSignatureMethod.Provider,
                    Status = LeaseSignerStatus.Signed,
                    ExternalSignerId = $"signer-{party.Id:N}",
                    SigningUrl = $"https://sign.example.com/{party.Id:N}?token=secret",
                    SigningUrlExpiresAt = start.AddDays(7),
                    SignedAt = start,
                });
            }
        }

        await db.SaveChangesAsync();
        return (lease.Id, ownerId);
    }

    private static void AssertNoPersonalData(Party party)
    {
        foreach (var property in typeof(Party).GetProperties())
        {
            if (PartyNonPersonal.ContainsKey(property.Name))
                continue;
            var value = property.GetValue(party);
            Assert.True(
                value switch
                {
                    null => true,
                    string text => text.Length == 0
                        || text == LeasePartyPrivacyService.AnonymizedValue
                        || text == LeasePartyPrivacyService.AnonymizedEmail(party.Id),
                    _ => false,
                },
                $"Party.{property.Name} still holds '{value}' after the anonymization");
        }

        Assert.NotNull(party.AnonymizedAt);
    }
}
