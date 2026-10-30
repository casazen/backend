using System.Net;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// LR-09: the tenants of the long-term area over the real pipeline (PostgreSQL in CI, InMemory in a local run), on the world of
/// <see cref="LongRentWorld"/> ("today" is 2026-10-09 in Rome) with one change: every tenant gets a fiscal code of its own (the world gives
/// them all the same one, which would make them a single person). Three rules where a mistake costs data: what a caller reaches and
/// what the pages carry, what the copy of a person's data holds, and what the erasure request may remove. Who may call the export and the
/// erasure is the table of <c>OrgRoleActionMatrixTests</c>.
/// </summary>
public class LongRentTenantsIntegrationTests(LongRentAggregatesFactory factory) : IClassFixture<LongRentAggregatesFactory>, IAsyncLifetime
{
    private const string LandlordFiscalCode = "RSSMRA80A01H501U";
    private const string LandlordEmail = "mario.rossi@landlords.example";

    private LongRentWorld _world = null!;

    public async Task InitializeAsync()
    {
        _world = await LongRentWorld.SeedAsync(factory);
        await GiveEachTenantItsOwnFiscalCodeAsync(_world);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // --- What the caller reaches, and what the pages carry ----------------------------------------------------------

    [Fact]
    public async Task List_StaysInTheReachOfTheCaller_AndNeverCarriesAFiscalCodeOrAClearEmail()
    {
        using var owner = _world.OwnerClient(factory);
        using var colleague = _world.ColleagueClient(factory);

        var ownerBody = await owner.GetStringAsync("/api/long-rent/tenants?pageSize=100");
        var colleagueBody = await colleague.GetStringAsync("/api/long-rent/tenants?pageSize=100");

        // The owner of P1 and P2 sees their tenants (the co-tenants of a draft too) and not the colleague's tenant of P3, and vice versa.
        var owners = NamesIn(ownerBody);
        Assert.Contains("Giulia Verdi", owners);
        Assert.Contains("Marta Colucci", owners);
        Assert.Contains("Marco Bianchi", owners);
        Assert.DoesNotContain("Valentina Marra", owners);
        Assert.Equal(["Valentina Marra"], NamesIn(colleagueBody));

        // The tenant whose data were anonymized is no longer a person of the page.
        Assert.DoesNotContain(owners, name => name.Contains("Cognome", StringComparison.Ordinal));

        // The fiscal code is never on the page, the e-mail only masked, and the id is not made of either.
        foreach (var body in new[] { ownerBody, colleagueBody })
        {
            Assert.DoesNotContain("CF-", body, StringComparison.Ordinal);
            Assert.DoesNotContain(_world.EmailOf("Giulia", "Verdi"), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(_world.EmailOf("Valentina", "Marra"), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(LandlordFiscalCode, body, StringComparison.Ordinal);
            Assert.DoesNotContain(LandlordEmail, body, StringComparison.Ordinal);
        }

        Assert.Contains("g***@tenants.example", ownerBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sheet_AndExport_OfAPersonOutOfTheCallersReachOrOfAnotherOrg_Are404()
    {
        using var owner = _world.OwnerClient(factory);
        var colleaguesTenant = await PartyIdAsync(_world, "Valentina");
        var other = await LongRentWorld.SeedAsync(factory);
        await GiveEachTenantItsOwnFiscalCodeAsync(other);
        var otherOrgsTenant = await PartyIdAsync(other, "Giulia");

        foreach (var id in new[] { colleaguesTenant, otherOrgsTenant, Guid.NewGuid() })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/long-rent/tenants/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/long-rent/tenants/{id}/export")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/long-rent/tenants/{id}/erasure-request", null)).StatusCode);
        }
    }

    // --- The copy of a person's data --------------------------------------------------------------------------------

    [Fact]
    public async Task Export_HoldsTheDataOfThePersonAndOfNobodyElse_UnderANeutralFileName()
    {
        using var owner = _world.OwnerClient(factory);
        // Marco Bianchi is a tenant of the draft with Anna Rossi, and the landlord is its other party.
        var marco = await PartyIdAsync(_world, "Marco");

        var response = await owner.GetAsync($"/api/long-rent/tenants/{marco}/export");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("dati-personali.json", response.Content.Headers.ContentDisposition?.ToString(), StringComparison.Ordinal);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);

        var body = await response.Content.ReadAsStringAsync();
        // His own data, in clear.
        Assert.Contains(FiscalCodeOf("Marco"), body, StringComparison.Ordinal);
        Assert.Contains(_world.EmailOf("Marco", "Bianchi"), body, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(body);
        var lease = Assert.Single(document.RootElement.GetProperty("leases").EnumerateArray());
        Assert.Equal(_world.Draft, lease.GetProperty("leaseId").GetGuid());
        Assert.Equal("Marco", lease.GetProperty("party").GetProperty("firstName").GetString());
        // Nothing of the co-tenant, nothing of the landlord.
        Assert.DoesNotContain(FiscalCodeOf("Anna"), body, StringComparison.Ordinal);
        Assert.DoesNotContain(_world.EmailOf("Anna", "Rossi"), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(LandlordFiscalCode, body, StringComparison.Ordinal);
        Assert.DoesNotContain(LandlordEmail, body, StringComparison.Ordinal);
        Assert.DoesNotContain("Mario", body, StringComparison.Ordinal);
    }

    // --- The erasure request ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Erasure_RemovesTheDataOfALeaseThatEnded_AndOnlySchedulesTheOneThatRuns()
    {
        using var owner = _world.OwnerClient(factory);
        var marta = await PartyIdAsync(_world, "Marta");
        var giulia = await PartyIdAsync(_world, "Giulia");

        // Marta's lease ended on 2026-06-30: her data go now. The landlord is also a party of leases that run, so he stays.
        var ended = await ErasureAsync(owner, marta);
        var endedLease = Assert.Single(ended.GetProperty("leases").EnumerateArray());
        Assert.Equal(_world.Ended, endedLease.GetProperty("leaseId").GetGuid());
        Assert.Equal("PartiallyAnonymized", endedLease.GetProperty("status").GetString());
        var stored = await LoadAsync(_world.Ended);
        var tenant = stored.Parties.Single(p => p.Role == PartyRole.Tenant);
        Assert.NotNull(tenant.AnonymizedAt);
        Assert.Equal(LeasePartyPrivacyService.AnonymizedValue, tenant.FirstName);
        Assert.Equal(LeasePartyPrivacyService.AnonymizedValue, tenant.FiscalCode);
        Assert.Null(stored.Parties.Single(p => p.Role == PartyRole.Landlord).AnonymizedAt);
        Assert.Single(stored.Events, e => e.EventType == LeaseEventType.ErasureRequested);

        // Giulia's lease runs until 2028-08-31: nothing is removed, the request waits for the day after it ends.
        var running = await ErasureAsync(owner, giulia);
        var runningLease = Assert.Single(running.GetProperty("leases").EnumerateArray());
        Assert.Equal(_world.Active, runningLease.GetProperty("leaseId").GetGuid());
        Assert.Equal("Scheduled", runningLease.GetProperty("status").GetString());
        Assert.Equal(new DateTime(2028, 9, 1, 0, 0, 0, DateTimeKind.Utc), runningLease.GetProperty("anonymizationFrom").GetDateTime().ToUniversalTime());
        var untouched = await LoadAsync(_world.Active);
        var kept = untouched.Parties.Single(p => p.Role == PartyRole.Tenant);
        Assert.Null(kept.AnonymizedAt);
        Assert.Equal("Giulia", kept.FirstName);
        Assert.Equal(FiscalCodeOf("Giulia"), kept.FiscalCode);
        Assert.Null(untouched.PartiesAnonymizedAt);

        // Asked again, nothing changes (idempotent), and the person who is gone is no longer a person of the page.
        var again = await ErasureAsync(owner, giulia);
        Assert.Equal("Scheduled", Assert.Single(again.GetProperty("leases").EnumerateArray()).GetProperty("status").GetString());
        Assert.Single((await LoadAsync(_world.Active)).Events, e => e.EventType == LeaseEventType.ErasureRequested);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/long-rent/tenants/{marta}")).StatusCode);
    }

    // --- Helpers ----------------------------------------------------------------------------------------------------

    private static string FiscalCodeOf(string firstName) => $"CF-{firstName.ToUpperInvariant()}";

    private static List<string> NamesIn(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => $"{item.GetProperty("firstName").GetString()} {item.GetProperty("lastName").GetString()}")
            .ToList();
    }

    private static async Task<JsonElement> ErasureAsync(HttpClient client, Guid tenantId)
    {
        var response = await client.PostAsync($"/api/long-rent/tenants/{tenantId}/erasure-request", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>The world gives every tenant the same fiscal code: here each one gets its own, <c>CF-FIRSTNAME</c>.</summary>
    private async Task GiveEachTenantItsOwnFiscalCodeAsync(LongRentWorld world)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenants = await db.Parties
            .Where(p => p.Role == PartyRole.Tenant && p.AnonymizedAt == null && p.LeaseContract.OrgId == world.OrgId)
            .ToListAsync();
        foreach (var tenant in tenants)
            tenant.FiscalCode = FiscalCodeOf(tenant.FirstName);
        await db.SaveChangesAsync();
    }

    private async Task<Guid> PartyIdAsync(LongRentWorld world, string firstName)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Parties
            .AsNoTracking()
            .Where(p => p.Role == PartyRole.Tenant && p.FirstName == firstName && p.LeaseContract.OrgId == world.OrgId)
            .Select(p => p.Id)
            .SingleAsync();
    }

    private async Task<LeaseContract> LoadAsync(Guid leaseId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.LeaseContracts
            .AsNoTracking()
            .Include(l => l.Parties)
            .Include(l => l.Events)
            .SingleAsync(l => l.Id == leaseId);
    }
}
