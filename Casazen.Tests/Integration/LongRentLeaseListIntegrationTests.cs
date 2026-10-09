using System.Net;
using System.Text.Json;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// LR-01, B3: <c>GET /api/leases</c> with its <c>view</c> and <c>q</c>, the first tenant and the state of the rent on each row, over
/// the real pipeline (PostgreSQL in CI, InMemory in a local run). The world is described in <see cref="LongRentWorld"/>: "today" is
/// 2026-10-09 in Rome.
/// </summary>
public class LongRentLeaseListIntegrationTests(LongRentAggregatesFactory factory) : IClassFixture<LongRentAggregatesFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private LongRentWorld _world = null!;

    public async Task InitializeAsync() => _world = await LongRentWorld.SeedAsync(factory);

    public Task DisposeAsync() => Task.CompletedTask;

    // --- The views --------------------------------------------------------------------------------------

    [Fact]
    public async Task List_WithoutParameters_IsTheWholeListOfTheOwner_NewestFirstAndAsBefore()
    {
        using var client = _world.OwnerClient(factory);

        var rows = await GetAsync(client, "/api/leases");

        // The owner of P1 and P2: eight leases (the colleague's lease on P3 is not theirs).
        Assert.Equal(8, rows.Count);
        Assert.DoesNotContain(rows, r => r.Id == _world.Colleague);
        Assert.All(rows.Zip(rows.Skip(1)), pair => Assert.True(pair.First.CreatedAt >= pair.Second.CreatedAt, "newest first"));
    }

    [Theory]
    [InlineData("All", 8)]
    [InlineData("all", 8)]
    [InlineData("Active", 5)]
    [InlineData("InPreparation", 2)]
    [InlineData("Expiring", 3)]
    [InlineData("Ended", 1)]
    public async Task List_View_NarrowsTheLeasesByStatusAndEndDate(string view, int expected)
    {
        using var client = _world.OwnerClient(factory);

        var rows = await GetAsync(client, $"/api/leases?view={view}");

        Assert.Equal(expected, rows.Count);
    }

    [Fact]
    public async Task List_Views_AreDerivedFromTheStatusAndTheEndDate()
    {
        using var client = _world.OwnerClient(factory);

        var active = await IdsAsync(client, "Active");
        var preparation = await IdsAsync(client, "InPreparation");
        var expiring = await IdsAsync(client, "Expiring");
        var ended = await IdsAsync(client, "Ended");

        // Registered and not ended; the leases about to expire are active as well.
        Assert.Equivalent(new[] { _world.Active, _world.Expiring, _world.Anonymized, _world.Transitory, _world.Long }, active);
        // Not registered yet, whatever the dates: the draft and the signed contract waiting for its registration.
        Assert.Equivalent(new[] { _world.Draft, _world.Signed }, preparation);
        // Six months from 9 October 2026 is 9 April 2027: 30/11/2026, 28/2/2027 and 31/3/2027 are in, 31/5/2027 is not.
        Assert.Equivalent(new[] { _world.Expiring, _world.Anonymized, _world.Transitory }, expiring);
        Assert.Equivalent(new[] { _world.Ended }, ended);
    }

    [Fact]
    public async Task List_UnknownView_Returns400WithTheCode()
    {
        using var client = _world.OwnerClient(factory);

        var response = await client.GetAsync("/api/leases?view=Terminati");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_error", (await ReadJsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task List_TheLegacyStatusParameter_IsStillIgnored_ItNeverBecameAFilter()
    {
        using var client = _world.OwnerClient(factory);

        var all = await GetAsync(client, "/api/leases");
        var withStatus = await GetAsync(client, "/api/leases?status=Draft");
        var withStatusAndView = await GetAsync(client, "/api/leases?status=Draft&view=Ended");

        // What older clients send changes nothing, as before; only `view` narrows the list.
        Assert.Equal(all.Select(r => r.Id), withStatus.Select(r => r.Id));
        Assert.Equal(_world.Ended, Assert.Single(withStatusAndView).Id);
    }

    // --- The search -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("sparano", 4)] // the property P1, any case: Active, Draft, Anonymized, Long
    [InlineData("MURAT", 4)] // P2: Signed, Ended, Expiring, Transitory
    [InlineData("bari", 8)] // the city of every property
    [InlineData("verdi", 1)]
    [InlineData("giulia verdi", 1)]
    [InlineData("Verdi Giulia", 1)]
    [InlineData("ANNA", 1)] // the second tenant of a lease is found too
    [InlineData("  de santis ", 1)]
    [InlineData("zzz-nobody", 0)]
    public async Task List_Search_FindsThePropertyOrAnyTenantWithoutRegardToCase(string q, int expected)
    {
        using var client = _world.OwnerClient(factory);

        var rows = await GetAsync(client, $"/api/leases?q={Uri.EscapeDataString(q)}");

        Assert.Equal(expected, rows.Count);
    }

    [Fact]
    public async Task List_Search_WildcardsAreJustCharacters()
    {
        using var client = _world.OwnerClient(factory);

        Assert.Empty(await GetAsync(client, $"/api/leases?q={Uri.EscapeDataString("%")}"));
        Assert.Empty(await GetAsync(client, $"/api/leases?q={Uri.EscapeDataString("_____")}"));
    }

    [Fact]
    public async Task List_Search_NeverFindsATenantWhoseDataWereAnonymized()
    {
        using var client = _world.OwnerClient(factory);

        // The anonymized tenant keeps a placeholder name ("Nome Cognome" before, an anonymized value now): nobody can look it up.
        Assert.Empty(await GetAsync(client, $"/api/leases?q={Uri.EscapeDataString("Cognome")}"));
        Assert.Empty(await GetAsync(client, $"/api/leases?q={Uri.EscapeDataString("ANON")}"));
    }

    [Fact]
    public async Task List_SearchAndViewAndProperty_CombineTheNarrowings()
    {
        using var client = _world.OwnerClient(factory);

        var rows = await GetAsync(client, $"/api/leases?propertyId={_world.P2}&view=Active&q=murat");

        Assert.Equivalent(new[] { _world.Expiring, _world.Transitory }, rows.Select(r => r.Id));
    }

    // --- The first tenant ---------------------------------------------------------------------------------

    [Fact]
    public async Task List_EachRow_CarriesTheFirstTenantByNameOnly()
    {
        using var client = _world.OwnerClient(factory);

        var response = await client.GetAsync("/api/leases");
        var body = await response.Content.ReadAsStringAsync();
        var rows = Parse(body);

        var active = rows.Single(r => r.Id == _world.Active);
        Assert.Equal("Giulia", active.TenantFirstName);
        Assert.Equal("Verdi", active.TenantLastName);
        Assert.False(active.TenantAnonymized);
        // Of two tenants, the first one entered.
        var draft = rows.Single(r => r.Id == _world.Draft);
        Assert.Equal("Marco", draft.TenantFirstName);
        Assert.Equal(3, draft.PartyCount); // the landlord and the two tenants
        // Never the fiscal code, the email nor the citizenship (D29: the name yes).
        Assert.DoesNotContain("VRDGLI85B02F205A", body, StringComparison.Ordinal);
        Assert.DoesNotContain("RSSMRA80A01H501U", body, StringComparison.Ordinal);
        Assert.DoesNotContain("@", body, StringComparison.Ordinal);
        Assert.DoesNotContain("mario", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_ATenantWhoseDataWereAnonymized_HasNoNameAndSaysSo()
    {
        using var client = _world.OwnerClient(factory);

        var row = (await GetAsync(client, "/api/leases")).Single(r => r.Id == _world.Anonymized);

        Assert.Null(row.TenantFirstName);
        Assert.Null(row.TenantLastName);
        Assert.True(row.TenantAnonymized);
        Assert.Equal(2, row.PartyCount);
    }

    // --- The rent ---------------------------------------------------------------------------------------

    [Fact]
    public async Task List_Rent_NextInstallmentAndWhatIsOverdueComeFromTheLedger()
    {
        using var client = _world.OwnerClient(factory);

        var rows = await GetAsync(client, "/api/leases");

        // August (due 5/8) and October (due 5/10) unpaid and past due; September paid; November to come.
        var active = rows.Single(r => r.Id == _world.Active);
        Assert.Equal("2026-08-05", active.NextRentDueDate);
        Assert.Equal(2, active.OverdueRentCount);
        Assert.Equal(1800m, active.OverdueRentAmount);
        Assert.Equal(65, active.OverdueDays);

        // A payment in flight is still to be collected but it is not overdue; the cancelled and the paid ones are neither.
        var transitory = rows.Single(r => r.Id == _world.Transitory);
        Assert.Equal("2026-10-07", transitory.NextRentDueDate);
        Assert.Equal(0, transitory.OverdueRentCount);
        Assert.Equal(0m, transitory.OverdueRentAmount);
        Assert.Null(transitory.OverdueDays);

        // All paid: nothing next, nothing overdue.
        var expiring = rows.Single(r => r.Id == _world.Expiring);
        Assert.Null(expiring.NextRentDueDate);
        Assert.Equal(0, expiring.OverdueRentCount);

        // No rent schedule at all.
        var draft = rows.Single(r => r.Id == _world.Draft);
        Assert.Null(draft.NextRentDueDate);
        Assert.Equal(0, draft.OverdueRentCount);
        Assert.Null(draft.OverdueDays);
    }

    [Fact]
    public async Task List_Rent_TheDayAnInstallmentFallsDue_IsNotOverdueYet()
    {
        var world = await LongRentWorld.SeedAsync(factory);
        using var client = world.OwnerClient(factory);
        factory.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero));
        try
        {
            var active = (await GetAsync(client, "/api/leases")).Single(r => r.Id == world.Active);

            // On 5 October the October installment is due today: not overdue. August still is.
            Assert.Equal(1, active.OverdueRentCount);
            Assert.Equal(900m, active.OverdueRentAmount);
            Assert.Equal(61, active.OverdueDays);
        }
        finally
        {
            factory.Clock.SetUtcNow(LongRentAggregatesFactory.Now);
        }
    }

    // --- Who sees what ----------------------------------------------------------------------------------

    [Fact]
    public async Task List_ALandlordWithLimitedScope_SeesOnlyTheLeasesOfItsProperties()
    {
        using var client = _world.ColleagueClient(factory);

        var rows = await GetAsync(client, "/api/leases");

        Assert.Equal(_world.Colleague, Assert.Single(rows).Id);
        // Not even by asking for it: a view, a search or the property of the owner.
        Assert.Empty(await GetAsync(client, "/api/leases?q=sparano"));
        Assert.Empty(await GetAsync(client, $"/api/leases?propertyId={_world.P1}"));
        Assert.Empty(await GetAsync(client, "/api/leases?q=giulia"));
    }

    [Fact]
    public async Task List_AnOrgWideManager_SeesEveryLeaseOfTheOrg()
    {
        using var client = _world.ManagerClient(factory);

        var rows = await GetAsync(client, "/api/leases");

        Assert.Equal(9, rows.Count);
        Assert.Equal(6, (await GetAsync(client, "/api/leases?view=Active")).Count);
    }

    [Fact]
    public async Task List_AnotherOrg_SeesNothingOfThisWorld()
    {
        var stranger = await LongRentWorld.SeedAsync(factory);
        using var client = stranger.ManagerClient(factory);

        var rows = await GetAsync(client, "/api/leases");

        Assert.DoesNotContain(rows, r => r.Id == _world.Active || r.Id == _world.Colleague);
        Assert.Equal(9, rows.Count);
    }

    [Fact]
    public async Task List_WithoutLogin_Returns401()
    {
        using var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/leases?view=Active")).StatusCode);
    }

    [Fact]
    public async Task List_WithoutTheLongTermRole_Returns403()
    {
        using var client = factory.CreateAuthenticatedClient(_world.OwnerId, "PropertyOwner");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/leases?view=Active")).StatusCode);
    }

    // --- helpers ----------------------------------------------------------------------------------------

    private sealed record Row(
        Guid Id,
        DateTime CreatedAt,
        int PartyCount,
        string? TenantFirstName,
        string? TenantLastName,
        bool TenantAnonymized,
        string? NextRentDueDate,
        int OverdueRentCount,
        decimal OverdueRentAmount,
        int? OverdueDays);

    private static async Task<List<Row>> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Parse(await response.Content.ReadAsStringAsync());
    }

    private static List<Row> Parse(string json) => JsonSerializer.Deserialize<List<Row>>(json, JsonOptions)!;

    private static async Task<IReadOnlyList<Guid>> IdsAsync(HttpClient client, string view) =>
        (await GetAsync(client, $"/api/leases?view={view}")).Select(r => r.Id).ToList();

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), JsonOptions);
}
