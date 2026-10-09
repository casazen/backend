using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Search;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Unit.Search;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// The web host with the global search on (<c>Features:GlobalSearch</c>) and the org team on, in an in-memory store of its own
/// when there is no PostgreSQL (the shared store of the process is written by other suites).
/// </summary>
public class GlobalSearchEnabledFactory : CasazenWebApplicationFactory
{
    protected override bool SeedComuneSample => false;

    /// <summary>The limit of the search per user and window: large, so that a test is never limited by the suites around it.</summary>
    protected virtual string SearchPermitLimit => "1000";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        var inMemoryStore = $"global-search-{Guid.NewGuid():N}";
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Features:GlobalSearch"] = "true",
                ["Features:OrgTeam"] = "true",
                ["RateLimiting:GlobalSearch:PermitLimit"] = SearchPermitLimit,
            }));
        builder.ConfigureTestServices(services =>
        {
            if (!UsesPostgreSql)
            {
                RemoveAllOf<DbContextOptions<AppDbContext>>(services);
                RemoveAllOf<IDbContextOptionsConfiguration<AppDbContext>>(services);
                services.AddDbContext<AppDbContext>(options =>
                {
                    options.UseInMemoryDatabase(inMemoryStore);
                    options.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
                });
            }
        });
    }
}

/// <summary>The same host with a limit of three searches per user and window, to see the limiter answer 429.</summary>
public sealed class GlobalSearchLimitedFactory : GlobalSearchEnabledFactory
{
    protected override string SearchPermitLimit => "3";
}

/// <summary>
/// UI-13a over the real pipeline (routes, policies, model binding, error contract, JSON): <c>GET /api/search</c> for an owner, for a
/// collaborator limited to one property and for a supplier, with the permissions of their roles; the org boundary; what the answer
/// never carries; the 404 of the flag, the 401, the 400 and the 429; and <c>PUT /api/me/last-context</c> written and read back. On
/// PostgreSQL in CI (the keys are computed by the database); on the in-memory fallback locally (the seeded roles are written first
/// and so are the keys).
/// </summary>
public class GlobalSearchHttpIntegrationTests(GlobalSearchEnabledFactory factory) : IClassFixture<GlobalSearchEnabledFactory>
{
    private static readonly SemaphoreSlim SeedLock = new(1, 1);

    private sealed record World(SearchWorld Data, string SupplierUserId)
    {
        public string OwnerId => Data.OwnerId;

        public string CollaboratorId => Data.CollaboratorId;
    }

    private HttpClient Owner(World world) => factory.CreateAuthenticatedClient(world.OwnerId, roles: "PropertyOwner");

    /// <summary>A collaborator is a person of the database only: no role in the token, nothing it could widen its reach with.</summary>
    private HttpClient Collaborator(World world) => factory.CreateAuthenticatedClient(world.CollaboratorId);

    private HttpClient Supplier(World world) => factory.CreateAuthenticatedClient(world.SupplierUserId, roles: "Supplier");

    /// <summary>
    /// On PostgreSQL the migrations seed the contexts, roles and permissions. The in-memory fallback of the host has none: write
    /// the seed data of the model, once, so the same tests run locally.
    /// </summary>
    private async Task EnsureSeedsAsync()
    {
        if (factory.UsesPostgreSql)
            return;

        await SeedLock.WaitAsync();
        try
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await db.Roles.AnyAsync())
                return;

            var model = ((IInfrastructure<IServiceProvider>)db).Instance.GetRequiredService<IDesignTimeModel>().Model;
            foreach (var entityType in model.GetEntityTypes().Where(e => e.ClrType.Name is "AppContext" or "Role" or "RolePermission"))
            {
                foreach (var row in entityType.GetSeedData())
                {
                    var entity = Activator.CreateInstance(entityType.ClrType)!;
                    foreach (var (name, value) in row)
                        entityType.ClrType.GetProperty(name)!.SetValue(entity, value);
                    db.Add(entity);
                }
            }

            await db.SaveChangesAsync();
        }
        finally
        {
            SeedLock.Release();
        }
    }

    /// <summary>An org as the onboarding leaves it, its owner and a collaborator limited to the first property, with all the data of the scenario.</summary>
    private async Task<World> NewWorldAsync()
    {
        await EnsureSeedsAsync();
        var (ownerId, orgId) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        var collaboratorId = await OrgTeamHttp.AddMemberAsync(factory, orgId, OrgRole.Collaborator, ["short-rent"]);

        using var scope = factory.Services.CreateScope();
        // The owner lets short stays and long-term leases: both areas, as an owner who chose «Entrambi» at the onboarding.
        await scope.ServiceProvider.GetRequiredService<IUserContextMembershipService>().GrantAsync(ownerId, [UserRole.LongTermLandlord]);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var data = await SearchScenario.SeedDataAsync(db, orgId, ownerId, collaboratorId, computeKeys: !factory.UsesPostgreSql);

        // «Solo alcuni»: the first property only (the grant is in the data; the scope of the person is here).
        var member = await db.OrgMembers.IgnoreQueryFilters().SingleAsync(m => m.UserId == collaboratorId);
        member.PropertyScope = PropertyScope.Selected;

        // A person who works as a supplier: the plumbers (the Supplier role of the token, the link of the account to the supplier org).
        var supplierUserId = $"auth0|fornitore-{Guid.NewGuid():N}";
        db.Users.Add(new User
        {
            Id = supplierUserId,
            Email = $"{Guid.NewGuid():N}@fornitori.example.com",
            FirstName = "Idraulico",
            LastName = "Rossi",
            Role = UserRole.Supplier,
            SupplierOrgId = data.PlumbersOrgId,
            IsActive = true,
        });
        await db.SaveChangesAsync();
        return new World(data, supplierUserId);
    }

    private static async Task<JsonElement> SearchAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/search?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The names of people: never kept by a cache nor the history of the browser.
        Assert.True(response.Headers.CacheControl is { NoStore: true, Private: true }, "Cache-Control: private, no-store");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static IReadOnlyList<string> Types(JsonElement answer) =>
        answer.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("type").GetString()!).ToList();

    private static IReadOnlyList<JsonElement> Items(JsonElement answer, string type) =>
        answer.GetProperty("groups").EnumerateArray().Where(g => g.GetProperty("type").GetString() == type)
            .SelectMany(g => g.GetProperty("items").EnumerateArray()).ToList();

    private static IReadOnlyList<Guid> Ids(JsonElement answer, string type) =>
        Items(answer, type).Select(i => i.GetProperty("id").GetGuid()).ToList();

    // ─── The owner ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Owner_FindsItsDataInEveryGroup_WithTheDestinationOfTheArea()
    {
        var world = await NewWorldAsync();
        using var owner = Owner(world);

        var rossi = await SearchAsync(owner, "q=rossi");
        Assert.Equal(["booking", "guest", "supplier"], Types(rossi));
        Assert.Equal([world.Data.MariaRossi.Id, world.Data.MarioRossi.Id], Ids(rossi, "guest"));
        Assert.Equal(
            [world.Data.StayMarioAtCasaBella.Id, world.Data.StayMarioAtTrullo.Id, world.Data.StayMaria.Id],
            Ids(rossi, "booking"));
        Assert.Equal([world.Data.PlumbersOrgId], Ids(rossi, "supplier"));

        var property = Assert.Single(Items(await SearchAsync(owner, "q=trullo"), "property"));
        Assert.Equal(world.Data.Trullo.Id, property.GetProperty("id").GetGuid());
        Assert.Equal("Trullo Bianco", property.GetProperty("title").GetString());
        Assert.Equal("Alberobello", property.GetProperty("subtitle").GetString());
        Assert.Equal("short-rent.property", property.GetProperty("destination").GetString());

        // The long-term property and its lease: the owner holds the permissions of the long-term area through the roles of its org.
        var loft = await SearchAsync(owner, "q=loft");
        Assert.Equal(world.Data.LoftNavigli.Id, Assert.Single(Items(loft, "property")).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Owner_CaseAccentsAndPrefixes_DoNotMatter_AndTheLimitIsPerGroup()
    {
        var world = await NewWorldAsync();
        using var owner = Owner(world);

        Assert.Equal([world.Data.Jose.Id], Ids(await SearchAsync(owner, "q=M%C3%9CLLER"), "guest"));
        Assert.Equal([world.Data.CasaBella.Id], Ids(await SearchAsync(owner, "q=forl%C3%AC"), "property"));
        Assert.Equal([world.Data.StayJose.Id], Ids(await SearchAsync(owner, "q=7k3m9-pq2xv"), "booking"));

        var limited = await SearchAsync(owner, "q=rossi&limit=1");
        Assert.All(limited.GetProperty("groups").EnumerateArray(), group => Assert.True(group.GetProperty("items").GetArrayLength() <= 1));
        Assert.True(limited.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("type").GetString() == "guest").GetProperty("hasMore").GetBoolean());

        // An excessive limit is cut to twenty; a missing one is five; nothing to find is an empty list, not an error.
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/search?q=rossi&limit=100000")).StatusCode);
        Assert.Empty((await SearchAsync(owner, "q=zzzzzz")).GetProperty("groups").EnumerateArray());
    }

    [Fact]
    public async Task Owner_TheAnswerNeverCarriesWhatMustNotLeave()
    {
        var world = await NewWorldAsync();
        using var owner = Owner(world);

        foreach (var term in new[] { "rossi", "jose", "muller", "example", "carla", "7k3m9-pq2xv", "verdi", "idraulica" })
        {
            var response = await owner.GetAsync($"/api/search?q={Uri.EscapeDataString(term)}");
            var json = await response.Content.ReadAsStringAsync();

            // Not the phone, the document, the whole e-mail, the city or the address of a guest, nor the fiscal code and e-mail of a party.
            foreach (var secret in new[]
                     {
                         "+391234567890", "+390612345678", "+39333111222", "AX1234567", "CA00000AA", "jose.muller@example.com",
                         "maria.rossi@example.com", "mario.rossi@example.com", "carla@example.com", "Monaco", "RSSMRA80A01H501Z",
                         "giulia.verdi@example.com", "ANONYMIZED", "deleted.local",
                     })
            {
                Assert.DoesNotContain(secret, json);
            }

            // Only the five fields of the contract on a result.
            using var document = JsonDocument.Parse(json);
            foreach (var group in document.RootElement.GetProperty("groups").EnumerateArray())
            {
                Assert.Equal(["hasMore", "items", "type"], group.EnumerateObject().Select(p => p.Name).Order().ToArray());
                foreach (var item in group.GetProperty("items").EnumerateArray())
                    Assert.Equal(["destination", "id", "subtitle", "title", "type"], item.EnumerateObject().Select(p => p.Name).Order().ToArray());
            }
        }
    }

    [Fact]
    public async Task Owner_AGuestIsShownWithAMaskedEmail()
    {
        var world = await NewWorldAsync();
        using var owner = Owner(world);

        var guest = Assert.Single(Items(await SearchAsync(owner, "q=jose"), "guest"));

        Assert.Equal("José Müller", guest.GetProperty("title").GetString());
        Assert.Equal("j***@example.com", guest.GetProperty("subtitle").GetString());
        Assert.Equal("short-rent.guest", guest.GetProperty("destination").GetString());
    }

    // ─── The org boundary ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Org_AnotherOwnerSeesNothingOfThisOrg_AndTheOtherWayRound()
    {
        var world = await NewWorldAsync();
        var (otherOwnerId, otherOrgId) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var property = new Property
            {
                OrgId = otherOrgId,
                OwnerId = otherOwnerId,
                Name = "Trullo Segreto",
                Address = $"Via Segreta {Guid.NewGuid():N}",
                City = "Ostuni",
                PostalCode = "72017",
                Bedrooms = 1,
                Bathrooms = 1,
                MaxGuests = 2,
                NightlyRate = 90m,
                IsActive = true,
            };
            db.Properties.Add(property);
            db.Guests.Add(new Guest { OrgId = otherOrgId, FirstName = "Maria", LastName = "Rossi", Email = "maria@segreto.example.com" });
            await db.SaveChangesAsync();
            if (!factory.UsesPostgreSql)
                await SearchKeysForTests.ComputeAsync(db);
        }

        using var owner = Owner(world);
        using var other = factory.CreateAuthenticatedClient(otherOwnerId, roles: "PropertyOwner");

        // The same words in both orgs: each owner finds its own and not a row of the other.
        var mine = await SearchAsync(owner, "q=trullo");
        Assert.Equal([world.Data.Trullo.Id], Ids(mine, "property"));
        var theirs = await SearchAsync(other, "q=trullo");
        Assert.Equal("Trullo Segreto", Assert.Single(Items(theirs, "property")).GetProperty("title").GetString());
        Assert.DoesNotContain(world.Data.Trullo.Id, Ids(theirs, "property"));

        var theirGuests = await SearchAsync(other, "q=rossi");
        var theirGuest = Assert.Single(Items(theirGuests, "guest"));
        Assert.Equal("Maria Rossi", theirGuest.GetProperty("title").GetString());
        Assert.Empty(Items(theirGuests, "booking"));
        Assert.Empty(Items(theirGuests, "supplier"));
    }

    // ─── The collaborator limited to one property ───────────────────────────────────────────────────────

    [Fact]
    public async Task Collaborator_LimitedToOneProperty_FindsOnlyThatProperty_AndWhatBelongsToIt()
    {
        var world = await NewWorldAsync();
        using var collaborator = Collaborator(world);

        Assert.Equal([world.Data.Trullo.Id], Ids(await SearchAsync(collaborator, "q=trullo"), "property"));
        Assert.Empty((await SearchAsync(collaborator, "q=casa")).GetProperty("groups").EnumerateArray());
        Assert.Empty((await SearchAsync(collaborator, "q=forli")).GetProperty("groups").EnumerateArray());

        // Mario stayed at both properties, Maria only at the one it cannot reach: Maria is not found, nor her stay.
        var rossi = await SearchAsync(collaborator, "q=rossi");
        Assert.Equal([world.Data.MarioRossi.Id], Ids(rossi, "guest"));
        Assert.Equal([world.Data.StayMarioAtTrullo.Id], Ids(rossi, "booking"));
        Assert.Empty(Items(rossi, "supplier"));
        Assert.Empty(Items(await SearchAsync(collaborator, "q=A1B2C3D4E5"), "booking"));
        Assert.Empty(Items(await SearchAsync(collaborator, "q=carla"), "guest"));
    }

    [Fact]
    public async Task Collaborator_HoldsNoPermissionOnLeases_SoThereIsNoLeaseGroup_EvenForItsProperty()
    {
        var world = await NewWorldAsync();
        using var collaborator = Collaborator(world);

        // The lease of the first property exists and is in the scope, but lease.read is a permission of the long-term area.
        var answer = await SearchAsync(collaborator, "q=neri");
        Assert.Empty(answer.GetProperty("groups").EnumerateArray());
    }

    [Fact]
    public async Task Collaborator_SeesTheRequestsAndTheSuppliersOfItsPropertyOnly()
    {
        var world = await NewWorldAsync();
        using var collaborator = Collaborator(world);

        Assert.Equal([world.Data.Cleaning.Id], Ids(await SearchAsync(collaborator, "q=pulizia"), "service-request"));
        Assert.Equal([world.Data.CleanersOrgId], Ids(await SearchAsync(collaborator, "q=splendore"), "supplier"));
        Assert.Empty((await SearchAsync(collaborator, "q=idraulica")).GetProperty("groups").EnumerateArray());
    }

    [Fact]
    public async Task Collaborator_GivenNothing_FindsNothingAtAll()
    {
        var world = await NewWorldAsync();
        using var collaborator = Collaborator(world);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PropertyMemberAccesses.RemoveRange(await db.PropertyMemberAccesses.IgnoreQueryFilters()
                .Where(a => a.UserId == world.CollaboratorId).ToListAsync());
            await db.SaveChangesAsync();
        }

        foreach (var term in new[] { "trullo", "rossi", "jose", "pulizia", "splendore", "7k3m9-pq2xv" })
            Assert.Empty((await SearchAsync(collaborator, $"q={term}")).GetProperty("groups").EnumerateArray());
    }

    // ─── The supplier ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Supplier_FindsItsInbox_NotTheDataOfTheHostsItWorksFor()
    {
        var world = await NewWorldAsync();
        using var supplier = Supplier(world);

        var plumbing = await SearchAsync(supplier, "q=plumbing");
        Assert.Equal(["supplier-request"], Types(plumbing));
        var request = Assert.Single(Items(plumbing, "supplier-request"));
        Assert.Equal(world.Data.Plumbing.Id, request.GetProperty("id").GetGuid());
        Assert.Equal("Forlì", request.GetProperty("subtitle").GetString());
        Assert.Equal("supplier.request", request.GetProperty("destination").GetString());

        // The host's properties, guests and stays are not its data, whatever the words: it holds no permission on them.
        foreach (var term in new[] { "trullo", "casa", "rossi", "jose", "7k3m9-pq2xv", "verdi" })
            Assert.DoesNotContain(Types(await SearchAsync(supplier, $"q={term}")), type => type is not "supplier-request");

        // The inbox of another supplier is not here.
        Assert.Empty((await SearchAsync(supplier, "q=pulizia")).GetProperty("groups").EnumerateArray());
    }

    // ─── What the endpoint refuses ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Anonymous_Is401()
    {
        using var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/search?q=rossi")).StatusCode);
    }

    [Theory]
    [InlineData("", "search_query_too_short")]
    [InlineData("q=a", "search_query_too_short")]
    [InlineData("q=%20a%20", "search_query_too_short")]
    [InlineData("q=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "search_query_too_long")]
    public async Task ATermOutOfRange_Is400_WithAStableCode(string query, string code)
    {
        await EnsureSeedsAsync();
        var (ownerId, _) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        using var owner = factory.CreateAuthenticatedClient(ownerId, roles: "PropertyOwner");

        await OrgTeamHttp.AssertProblemAsync(await owner.GetAsync($"/api/search?{query}"), HttpStatusCode.BadRequest, code);
    }

    [Fact]
    public async Task ALimitThatIsNotANumber_Is400()
    {
        await EnsureSeedsAsync();
        var (ownerId, _) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        using var owner = factory.CreateAuthenticatedClient(ownerId, roles: "PropertyOwner");

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/search?q=rossi&limit=molti")).StatusCode);
    }

    [Fact]
    public async Task OnlyGetIsAnswered()
    {
        await EnsureSeedsAsync();
        var (ownerId, _) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        using var owner = factory.CreateAuthenticatedClient(ownerId, roles: "PropertyOwner");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await owner.PostAsJsonAsync("/api/search", new { q = "rossi" })).StatusCode);
    }

    [Fact]
    public async Task ACallerWithNoPermissionAtAll_GetsAnEmptyAnswer_NotAnError()
    {
        // A signed-in user who finished no onboarding holds no host context, and is no supplier.
        using var nobody = factory.CreateAuthenticatedClient($"auth0|nessuno-{Guid.NewGuid():N}");

        var answer = await SearchAsync(nobody, "q=rossi");

        Assert.Empty(answer.GetProperty("groups").EnumerateArray());
    }

    // ─── The last area used ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LastContext_IsStored_AndGivenBackByTheContexts()
    {
        await EnsureSeedsAsync();
        var (ownerId, _) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        using var owner = factory.CreateAuthenticatedClient(ownerId, roles: "PropertyOwner");

        var before = await (await owner.GetAsync("/api/me/contexts")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, before.GetProperty("lastUsedContextKey").ValueKind);

        var put = await owner.PutAsJsonAsync("/api/me/last-context", new { contextKey = "short-rent" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("short-rent", (await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lastUsedContextKey").GetString());

        var after = await (await owner.GetAsync("/api/me/contexts")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("short-rent", after.GetProperty("lastUsedContextKey").GetString());
        var keys = after.GetProperty("contexts").EnumerateArray().Select(c => c.GetProperty("contextKey").GetString()!).ToList();
        Assert.Contains("short-rent", keys);

        // Another area of the list replaces it; the same one twice is fine.
        var other = keys.First(k => k != "short-rent");
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/me/last-context", new { contextKey = other })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/me/last-context", new { contextKey = other.ToUpperInvariant() })).StatusCode);
        var final = await (await owner.GetAsync("/api/me/contexts")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(other, final.GetProperty("lastUsedContextKey").GetString());
    }

    [Fact]
    public async Task LastContext_AnAreaTheCallerCannotEnter_Is422_AndNothingChanges()
    {
        await EnsureSeedsAsync();
        var (ownerId, _) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        using var owner = factory.CreateAuthenticatedClient(ownerId, roles: "PropertyOwner");
        await owner.PutAsJsonAsync("/api/me/last-context", new { contextKey = "short-rent" });

        foreach (var key in new[] { "admin", "supplier", "nonexistent" })
        {
            await OrgTeamHttp.AssertProblemAsync(
                await owner.PutAsJsonAsync("/api/me/last-context", new { contextKey = key }),
                HttpStatusCode.UnprocessableEntity,
                "context_not_accessible");
        }

        var after = await (await owner.GetAsync("/api/me/contexts")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("short-rent", after.GetProperty("lastUsedContextKey").GetString());
    }

    [Fact]
    public async Task LastContext_NoKey_Is400_AndAnonymousIs401()
    {
        await EnsureSeedsAsync();
        var (ownerId, _) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);
        using var owner = factory.CreateAuthenticatedClient(ownerId, roles: "PropertyOwner");
        using var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/me/last-context", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/me/last-context", new { contextKey = new string('x', 65) })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync("/api/me/last-context", new { contextKey = "short-rent" })).StatusCode);
    }
}

/// <summary>UI-13a with the default configuration (<c>Features:GlobalSearch</c> off): the search does not exist, the last area still works.</summary>
public class GlobalSearchFlagOffIntegrationTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    [Fact]
    public async Task Search_WithTheFlagOff_IsA404_LikeARouteThatDoesNotExist_BeforeAuthentication()
    {
        using var anonymous = factory.CreateClient();
        using var signedIn = factory.CreateAuthenticatedClient($"auth0|chiunque-{Guid.NewGuid():N}", roles: "PropertyOwner");

        foreach (var client in new[] { anonymous, signedIn })
        {
            var response = await client.GetAsync("/api/search?q=rossi");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("not_found", problem.GetProperty("code").GetString());
        }

        // Not even a bad term is looked at.
        Assert.Equal(HttpStatusCode.NotFound, (await signedIn.GetAsync("/api/search")).StatusCode);
    }

    [Fact]
    public async Task PublicFeatures_TellsTheSearchIsOff()
    {
        using var anonymous = factory.CreateClient();

        var features = await (await anonymous.GetAsync("/api/public/features")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(features.GetProperty("globalSearch").GetBoolean());
    }

    [Fact]
    public async Task LastContext_IsNotBehindTheFlag()
    {
        using var signedIn = factory.CreateAuthenticatedClient($"auth0|chiunque-{Guid.NewGuid():N}");

        // The route exists with the flag off: 422 (this account holds no context), not 404.
        var response = await signedIn.PutAsJsonAsync("/api/me/last-context", new { contextKey = "short-rent" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }
}

/// <summary>UI-13a: the limiter of the search answers 429 after the limit of the user, and does not touch another user.</summary>
public class GlobalSearchRateLimitIntegrationTests(GlobalSearchLimitedFactory factory) : IClassFixture<GlobalSearchLimitedFactory>
{
    [Fact]
    public async Task Search_AfterTheLimit_Is429WithRetryAfter_ForThatUserOnly()
    {
        using var anna = factory.CreateAuthenticatedClient($"auth0|anna-{Guid.NewGuid():N}");
        using var bruno = factory.CreateAuthenticatedClient($"auth0|bruno-{Guid.NewGuid():N}");

        for (var call = 0; call < 3; call++)
            Assert.Equal(HttpStatusCode.OK, (await anna.GetAsync("/api/search?q=rossi")).StatusCode);

        var limited = await anna.GetAsync("/api/search?q=rossi");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
        Assert.Equal("rate_limited", (await limited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // Two users behind the same address do not share the budget.
        Assert.Equal(HttpStatusCode.OK, (await bruno.GetAsync("/api/search?q=rossi")).StatusCode);
    }
}
