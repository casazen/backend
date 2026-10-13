using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Features;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-02 on the real pipeline: the supplier's service catalog <c>api/supplier/services</c> (policy <c>RequireSupplier</c>, the
/// supplier org from its own link): create, read, replace, delete, publish, pause, duplicate and photos, the error contract
/// (400, 404, 409, 422 with <c>fields</c>, Italian and English messages), the limit per supplier and that a supplier never
/// reaches another's services. Runs on PostgreSQL in CI and on the in-memory fallback locally; what needs PostgreSQL is in
/// <see cref="SupplierServiceCatalogPostgresTests"/>.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class SupplierServiceCatalogIntegrationTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    private const string Url = "/api/supplier/services";

    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    // ─── Who may call it ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Catalog_Anonymous_Returns401()
    {
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Url, Body("Pulizia"))).StatusCode);
    }

    [Fact]
    public async Task Catalog_SignedInWithoutTheSupplierRole_Returns403()
    {
        using var host = factory.CreateAuthenticatedClient($"auth0|host-{Guid.NewGuid():N}", roles: "PropertyOwner");

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostAsJsonAsync(Url, Body("Pulizia"))).StatusCode);
    }

    [Fact]
    public async Task Catalog_SupplierRoleWithoutALinkedSupplierOrg_Returns404AndProvisionsNothing()
    {
        var userId = $"auth0|unlinked-{Guid.NewGuid():N}";
        using var client = factory.CreateAuthenticatedClient(userId, roles: "Supplier");

        var response = await client.GetAsync(Url);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", (await ReadAsync(response)).GetProperty("code").GetString());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Users.AnyAsync(u => u.Id == userId));
    }

    [Theory]
    [InlineData(false)] // a supplier-only account: User.SupplierOrgId, no User.OrgId (PL-05)
    [InlineData(true)] // the legacy shape: the supplier org also in User.OrgId
    public async Task Catalog_BothSupplierLinkShapes_ReachTheSuppliersOwnCatalog(bool legacyOrgIdLink)
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory, legacyOrgIdLink);
        using var client = SupplierClient(userId);

        var created = await client.PostAsJsonAsync(Url, Body("Pulizia"));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await ReadAsync(created)).GetProperty("id").GetGuid();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(orgId, (await db.SupplierServiceListings.AsNoTracking().SingleAsync(l => l.Id == id)).OrgId);
    }

    // ─── Create and read ─────────────────────────────────────────────────────────

    [Fact]
    public async Task List_ANewSupplier_IsEmptyWithTheLimit()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);

        var response = await client.GetAsync(Url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal(0, body.GetProperty("items").GetArrayLength());
        Assert.Equal(0, body.GetProperty("total").GetInt32());
        Assert.Equal(30, body.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task Create_OnlyNameAndCategory_Returns201WithAnIncompleteDraft()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);

        var response = await client.PostAsJsonAsync(Url, new { name = "Ripasso pre-arrivo", category = "cleaning" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await ReadAsync(response);
        var id = dto.GetProperty("id").GetGuid();
        Assert.Equal($"{Url}/{id}", response.Headers.Location!.AbsolutePath);
        Assert.Equal("ripasso-pre-arrivo", dto.GetProperty("slug").GetString());
        Assert.Equal("Draft", dto.GetProperty("status").GetString());
        Assert.Equal("PerJob", dto.GetProperty("priceUnit").GetString());
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("priceFromCents").ValueKind);
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("durationMinutes").ValueKind);
        Assert.False(dto.GetProperty("pricesIncludeVat").GetBoolean());
        Assert.False(dto.GetProperty("requiresQuote").GetBoolean());
        Assert.False(dto.GetProperty("publishable").GetBoolean());
        Assert.Equal(new[] { "durationMinutes", "priceFromCents" }, Strings(dto.GetProperty("missingForPublication")));
        Assert.Equal(7, dto.GetProperty("weekdays").GetArrayLength());
        Assert.Equal(0, dto.GetProperty("sortOrder").GetInt32());
        Assert.Empty(Strings(dto.GetProperty("photoUrls")));
    }

    [Fact]
    public async Task Create_AFullService_IsReturnedAsSent_AndReadBackByIdAndInTheList()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);

        var created = await client.PostAsJsonAsync(Url, new
        {
            name = "Pulizia cambio ospiti",
            category = "cleaning",
            summary = "Pulizia completa tra un ospite e l'altro.",
            description = "Riga uno.\nRiga due.",
            priceFromCents = 4500,
            priceUnit = "PerJob",
            pricesIncludeVat = true,
            requiresQuote = false,
            durationMinutes = 120,
            minNoticeHours = 12,
            weekdays = new[] { "Monday", "Tuesday", "Saturday" },
            supplements = new[]
            {
                new { code = "bagno-extra", label = "Ogni bagno in più", amountCents = 1000, per = "bathroom", max = (int?)3 },
                new { code = "biancheria", label = "Biancheria nostra, a set", amountCents = 1200, per = "set", max = (int?)null },
            },
            included = new[] { "Biancheria pulita", "Report fotografico" },
            excluded = new[] { "Lavaggio tende" },
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = await ReadAsync(created);
        var id = dto.GetProperty("id").GetGuid();
        Assert.True(dto.GetProperty("publishable").GetBoolean());
        Assert.Empty(Strings(dto.GetProperty("missingForPublication")));
        Assert.Equal(4500, dto.GetProperty("priceFromCents").GetInt32());
        Assert.True(dto.GetProperty("pricesIncludeVat").GetBoolean());
        Assert.Equal(12, dto.GetProperty("minNoticeHours").GetInt32());
        Assert.Equal(new[] { "Monday", "Tuesday", "Saturday" }, Strings(dto.GetProperty("weekdays")));
        var supplements = dto.GetProperty("supplements").EnumerateArray().ToList();
        Assert.Equal(2, supplements.Count);
        Assert.Equal("bagno-extra", supplements[0].GetProperty("code").GetString());
        Assert.Equal("Ogni bagno in più", supplements[0].GetProperty("label").GetString());
        Assert.Equal(1000, supplements[0].GetProperty("amountCents").GetInt32());
        Assert.Equal("bathroom", supplements[0].GetProperty("per").GetString());
        Assert.Equal(3, supplements[0].GetProperty("max").GetInt32());
        Assert.Equal(JsonValueKind.Null, supplements[1].GetProperty("max").ValueKind);
        Assert.Equal(new[] { "Biancheria pulita", "Report fotografico" }, Strings(dto.GetProperty("included")));
        Assert.Equal(new[] { "Lavaggio tende" }, Strings(dto.GetProperty("excluded")));

        var byId = await ReadAsync(await client.GetAsync($"{Url}/{id}"));
        Assert.Equal(id, byId.GetProperty("id").GetGuid());
        Assert.Equal("Pulizia cambio ospiti", byId.GetProperty("name").GetString());
        Assert.Equal("Riga uno.\nRiga due.", byId.GetProperty("description").GetString());

        var list = await ReadAsync(await client.GetAsync(Url));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.Equal(id, list.GetProperty("items")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Create_TheElectricalCategory_IsAccepted()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);

        var response = await client.PostAsJsonAsync(Url, new { name = "Controllo impianto", category = "electrical" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("electrical", (await ReadAsync(response)).GetProperty("category").GetString());
    }

    [Fact]
    public async Task Create_TwoServicesWithTheSameName_GetDistinctSlugs()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);

        var first = await ReadAsync(await client.PostAsJsonAsync(Url, Body("Pulizia")));
        var second = await ReadAsync(await client.PostAsJsonAsync(Url, Body("Pulizia")));

        Assert.Equal("pulizia", first.GetProperty("slug").GetString());
        Assert.Equal("pulizia-2", second.GetProperty("slug").GetString());
        Assert.Equal(1, second.GetProperty("sortOrder").GetInt32());
    }

    // ─── Create and update: the error contract ───────────────────────────────────

    [Fact]
    public async Task Create_NameMissing_Returns422NamingTheField()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);

        var missing = await client.PostAsJsonAsync(Url, new { category = "cleaning" });
        var blank = await client.PostAsJsonAsync(Url, new { name = "   ", category = "cleaning" });

        foreach (var response in new[] { missing, blank })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var problem = await ReadAsync(response);
            Assert.Equal("supplier_service_invalid", problem.GetProperty("code").GetString());
            Assert.Equal(new[] { "name" }, Strings(problem.GetProperty("fields")));
        }

        Assert.Equal(0, (await ReadAsync(await client.GetAsync(Url))).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Create_ATextOrAListOverItsLimit_Returns400ValidationError()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);

        var longName = await client.PostAsJsonAsync(Url, new { name = new string('x', 61), category = "cleaning" });
        var tooManyLines = await client.PostAsJsonAsync(
            Url, new { name = "Pulizia", category = "cleaning", included = Enumerable.Range(0, 41).Select(i => $"Voce {i}").ToArray() });

        foreach (var response in new[] { longName, tooManyLines })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("validation_error", (await ReadAsync(response)).GetProperty("code").GetString());
        }

        Assert.Equal(0, (await ReadAsync(await client.GetAsync(Url))).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Create_UnknownCategory_Returns422InvalidServiceCategory()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);

        var response = await client.PostAsJsonAsync(Url, new { name = "Pulizia", category = "Pulizie" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("invalid_service_category", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Create_InvalidSupplement_Returns422WithTheFieldsAndALocalizedMessage()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var body = new
        {
            name = "Pulizia",
            category = "cleaning",
            priceFromCents = 0,
            supplements = new[] { new { code = "Not a code", label = "Bagno", amountCents = 1000, per = "bathroom", max = (int?)null } },
        };

        var italian = await client.PostAsJsonAsync(Url, body);
        var english = await SendAsync(client, HttpMethod.Post, Url, body, language: "en");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, italian.StatusCode);
        var problem = await ReadAsync(italian);
        Assert.Equal("supplier_service_invalid", problem.GetProperty("code").GetString());
        Assert.Equal(new[] { "priceFromCents", "supplements[0].code" }, Strings(problem.GetProperty("fields")));
        Assert.Equal(
            "Alcuni dati del servizio non sono validi (campi: priceFromCents, supplements[0].code). Controllali e riprova.",
            problem.GetProperty("detail").GetString());

        var englishProblem = await ReadAsync(english);
        Assert.Equal("supplier_service_invalid", englishProblem.GetProperty("code").GetString());
        Assert.Equal(
            "Some details of the service are not valid (fields: priceFromCents, supplements[0].code). Check them and try again.",
            englishProblem.GetProperty("detail").GetString());
        Assert.Equal(0, (await ReadAsync(await client.GetAsync(Url))).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Update_ReplacesTheContent_AndAStaleOrMissingVersionIsRefused()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var created = await ReadAsync(await client.PostAsJsonAsync(Url, Body("Pulizia")));
        var id = created.GetProperty("id").GetGuid();
        var version = created.GetProperty("version").GetUInt32();

        var updated = await client.PutAsJsonAsync($"{Url}/{id}", UpdateBody("Pulizia profonda", version, priceFromCents: 12000, durationMinutes: 240));

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var dto = await ReadAsync(updated);
        Assert.Equal("Pulizia profonda", dto.GetProperty("name").GetString());
        Assert.Equal(12000, dto.GetProperty("priceFromCents").GetInt32());
        Assert.Equal(240, dto.GetProperty("durationMinutes").GetInt32());
        // A draft's slug follows its name; the status is still Draft.
        Assert.Equal("pulizia-profonda", dto.GetProperty("slug").GetString());
        Assert.Equal("Draft", dto.GetProperty("status").GetString());

        // A version nobody has ever had is stale on PostgreSQL and in memory alike.
        var stale = await client.PutAsJsonAsync($"{Url}/{id}", UpdateBody("Altro", uint.MaxValue - 7));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var problem = await ReadAsync(stale);
        Assert.Equal("supplier_service_changed", problem.GetProperty("code").GetString());
        Assert.Contains("ricarica la pagina", problem.GetProperty("detail").GetString());
        Assert.Equal("Pulizia profonda", (await ReadAsync(await client.GetAsync($"{Url}/{id}"))).GetProperty("name").GetString());

        var noVersion = await client.PutAsJsonAsync($"{Url}/{id}", new { name = "Altro", category = "cleaning" });
        Assert.Equal(HttpStatusCode.BadRequest, noVersion.StatusCode);
        Assert.Equal("validation_error", (await ReadAsync(noVersion)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Update_ARepeatedPutWithTheVersionJustReturned_Works()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var created = await ReadAsync(await client.PostAsJsonAsync(Url, Body("Pulizia")));
        var id = created.GetProperty("id").GetGuid();

        var first = await ReadAsync(await client.PutAsJsonAsync($"{Url}/{id}", UpdateBody("Uno", created.GetProperty("version").GetUInt32())));
        var second = await client.PutAsJsonAsync($"{Url}/{id}", UpdateBody("Due", first.GetProperty("version").GetUInt32()));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("Due", (await ReadAsync(second)).GetProperty("name").GetString());
    }

    // ─── Publish, pause, delete, duplicate ───────────────────────────────────────

    [Fact]
    public async Task Publish_IncompleteDraft_Returns422NotPublishableNamingWhatIsMissing_ThenWorksOnceComplete()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var created = await ReadAsync(await client.PostAsJsonAsync(Url, Body("Pulizia")));
        var id = created.GetProperty("id").GetGuid();

        var refused = await client.PostAsync($"{Url}/{id}/publish", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var problem = await ReadAsync(refused);
        Assert.Equal("supplier_service_not_publishable", problem.GetProperty("code").GetString());
        Assert.Equal(new[] { "durationMinutes", "priceFromCents" }, Strings(problem.GetProperty("fields")));
        Assert.Contains("Manca: durationMinutes, priceFromCents.", problem.GetProperty("detail").GetString());
        Assert.Equal("Draft", (await ReadAsync(await client.GetAsync($"{Url}/{id}"))).GetProperty("status").GetString());

        var version = (await ReadAsync(await client.GetAsync($"{Url}/{id}"))).GetProperty("version").GetUInt32();
        var completed = await client.PutAsJsonAsync($"{Url}/{id}", UpdateBody("Pulizia", version, priceFromCents: 4500, durationMinutes: 120));
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);

        var published = await client.PostAsync($"{Url}/{id}/publish", null);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        Assert.Equal("Active", (await ReadAsync(published)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Publish_AQuoteInPlaceOfAPrice_Works()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var created = await ReadAsync(await client.PostAsJsonAsync(Url, new
        {
            name = "Impianto elettrico",
            category = "electrical",
            durationMinutes = 180,
            requiresQuote = true,
        }));

        var published = await client.PostAsync($"{Url}/{created.GetProperty("id").GetGuid()}/publish", null);

        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        var dto = await ReadAsync(published);
        Assert.Equal("Active", dto.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("priceFromCents").ValueKind);
        Assert.True(dto.GetProperty("requiresQuote").GetBoolean());
    }

    [Fact]
    public async Task Pause_APublishedService_ThenPublishAgain_AndADraftCannotBePaused()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var complete = await ReadAsync(await client.PostAsJsonAsync(Url, Body("Pulizia", priceFromCents: 4500, durationMinutes: 120)));
        var id = complete.GetProperty("id").GetGuid();

        var draftPause = await client.PostAsync($"{Url}/{id}/pause", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, draftPause.StatusCode);
        Assert.Equal("supplier_service_cannot_pause", (await ReadAsync(draftPause)).GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"{Url}/{id}/publish", null)).StatusCode);
        var paused = await client.PostAsync($"{Url}/{id}/pause", null);
        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        Assert.Equal("Paused", (await ReadAsync(paused)).GetProperty("status").GetString());

        var again = await client.PostAsync($"{Url}/{id}/publish", null);
        Assert.Equal("Active", (await ReadAsync(again)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Update_APublishedServiceMadeIncomplete_Returns422NotPublishable()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var created = await ReadAsync(await client.PostAsJsonAsync(Url, Body("Pulizia", priceFromCents: 4500, durationMinutes: 120)));
        var id = created.GetProperty("id").GetGuid();
        var published = await ReadAsync(await client.PostAsync($"{Url}/{id}/publish", null));

        var response = await client.PutAsJsonAsync($"{Url}/{id}", UpdateBody("Pulizia", published.GetProperty("version").GetUInt32(), priceFromCents: 4500));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ReadAsync(response);
        Assert.Equal("supplier_service_not_publishable", problem.GetProperty("code").GetString());
        Assert.Equal(new[] { "durationMinutes" }, Strings(problem.GetProperty("fields")));
        Assert.Equal(120, (await ReadAsync(await client.GetAsync($"{Url}/{id}"))).GetProperty("durationMinutes").GetInt32());
    }

    [Fact]
    public async Task Delete_Returns204_ThenTheServiceIsGoneAndItsSlugIsFree()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var created = await ReadAsync(await client.PostAsJsonAsync(Url, Body("Pulizia")));
        var id = created.GetProperty("id").GetGuid();

        var deleted = await client.DeleteAsync($"{Url}/{id}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var notFound = await client.GetAsync($"{Url}/{id}");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        var problem = await ReadAsync(notFound);
        Assert.Equal("supplier_service_not_found", problem.GetProperty("code").GetString());
        Assert.Equal("Servizio non trovato: potrebbe essere stato eliminato.", problem.GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Url}/{id}")).StatusCode);
        Assert.Equal(0, (await ReadAsync(await client.GetAsync(Url))).GetProperty("total").GetInt32());

        var again = await ReadAsync(await client.PostAsJsonAsync(Url, Body("Pulizia")));
        Assert.Equal("pulizia", again.GetProperty("slug").GetString());
    }

    [Fact]
    public async Task Duplicate_Returns201WithADraftCopyAndTheLocalizedNameSuffix()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var created = await ReadAsync(await client.PostAsJsonAsync(Url, Body("Pulizia cambio ospiti", priceFromCents: 4500, durationMinutes: 120)));
        var id = created.GetProperty("id").GetGuid();
        await client.PostAsync($"{Url}/{id}/publish", null);

        var italian = await client.PostAsync($"{Url}/{id}/duplicate", null);
        var english = await SendAsync(client, HttpMethod.Post, $"{Url}/{id}/duplicate", body: null, language: "en");

        Assert.Equal(HttpStatusCode.Created, italian.StatusCode);
        var copy = await ReadAsync(italian);
        var copyId = copy.GetProperty("id").GetGuid();
        Assert.NotEqual(id, copyId);
        Assert.Equal($"{Url}/{copyId}", italian.Headers.Location!.AbsolutePath);
        Assert.Equal("Pulizia cambio ospiti (copia)", copy.GetProperty("name").GetString());
        Assert.Equal("pulizia-cambio-ospiti-copia", copy.GetProperty("slug").GetString());
        Assert.Equal("Draft", copy.GetProperty("status").GetString());
        Assert.Equal(4500, copy.GetProperty("priceFromCents").GetInt32());

        Assert.Equal(HttpStatusCode.Created, english.StatusCode);
        Assert.Equal("Pulizia cambio ospiti (copy)", (await ReadAsync(english)).GetProperty("name").GetString());

        // The original is untouched.
        Assert.Equal("Active", (await ReadAsync(await client.GetAsync($"{Url}/{id}"))).GetProperty("status").GetString());
        Assert.Equal(3, (await ReadAsync(await client.GetAsync(Url))).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Create_TheThirtyFirstService_Returns422LimitReached_AndDeletingOneMakesRoom()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        Guid first = default;
        for (var i = 0; i < SupplierServiceCatalogLimits.MaxServicesPerSupplier; i++)
        {
            var response = await client.PostAsJsonAsync(Url, Body($"Servizio {i}"));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            if (i == 0)
                first = (await ReadAsync(response)).GetProperty("id").GetGuid();
        }

        var over = await client.PostAsJsonAsync(Url, Body("Uno di troppo"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);
        var problem = await ReadAsync(over);
        Assert.Equal("supplier_service_limit_reached", problem.GetProperty("code").GetString());
        Assert.Contains("30", problem.GetProperty("detail").GetString());

        var duplicate = await client.PostAsync($"{Url}/{first}/duplicate", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
        Assert.Equal("supplier_service_limit_reached", (await ReadAsync(duplicate)).GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Url}/{first}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Url, Body("Ora c'è posto"))).StatusCode);
    }

    // ─── Photos ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Photos_AValidImage_IsStoredInTheSuppliersFolderAndListed_AndCanBeRemovedWithAPut()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var created = await ReadAsync(await client.PostAsJsonAsync(Url, Body("Pulizia")));
        var id = created.GetProperty("id").GetGuid();

        using var form = new MultipartFormDataContent();
        var png = new ByteArrayContent(PngBytes);
        png.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(png, "photos", "foto.png");
        var uploaded = await client.PostAsync($"{Url}/{id}/photos", form);

        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var dto = await ReadAsync(uploaded);
        var url = Assert.Single(Strings(dto.GetProperty("photoUrls")));
        Assert.Contains($"/suppliers/{orgId}/photos/", url);
        Assert.EndsWith(".png", url);
        Assert.Contains(Directory.GetFiles(factory.StorageRoot, "*.png", SearchOption.AllDirectories), path => path.Contains(orgId.ToString()));

        var read = await ReadAsync(await client.GetAsync($"{Url}/{id}"));
        Assert.Equal(new[] { url }, Strings(read.GetProperty("photoUrls")));

        var removed = await client.PutAsJsonAsync(
            $"{Url}/{id}", UpdateBody("Pulizia", dto.GetProperty("version").GetUInt32(), photoUrls: Array.Empty<string>()));
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Empty(Strings((await ReadAsync(removed)).GetProperty("photoUrls")));
        Assert.DoesNotContain(Directory.GetFiles(factory.StorageRoot, "*.png", SearchOption.AllDirectories), path => path.Contains(orgId.ToString()));
    }

    [Fact]
    public async Task Photos_NotAnImageOrNoFile_Returns422_AndStoresNothing()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var created = await ReadAsync(await client.PostAsJsonAsync(Url, Body("Pulizia")));
        var id = created.GetProperty("id").GetGuid();

        using var disguised = new MultipartFormDataContent();
        var text = new ByteArrayContent("non sono una foto"u8.ToArray());
        text.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        disguised.Add(text, "photos", "finta.jpg");
        var invalid = await client.PostAsync($"{Url}/{id}/photos", disguised);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        var problem = await ReadAsync(invalid);
        Assert.Equal("supplier_service_photo_invalid_type", problem.GetProperty("code").GetString());
        Assert.Contains("finta.jpg", problem.GetProperty("detail").GetString());

        using var empty = new MultipartFormDataContent();
        empty.Add(new StringContent("x"), "other");
        var none = await client.PostAsync($"{Url}/{id}/photos", empty);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, none.StatusCode);
        Assert.Equal("supplier_service_photo_none", (await ReadAsync(none)).GetProperty("code").GetString());

        Assert.DoesNotContain(Directory.GetFiles(factory.StorageRoot, "*", SearchOption.AllDirectories), path => path.Contains(orgId.ToString()));
    }

    // ─── Another supplier's services ─────────────────────────────────────────────

    [Fact]
    public async Task AnotherSuppliersService_IsNotFoundOnEveryEndpoint_AndStaysUntouched()
    {
        var (ownerId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        var (otherId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var owner = SupplierClient(ownerId);
        using var other = SupplierClient(otherId);
        var created = await ReadAsync(await owner.PostAsJsonAsync(Url, Body("Pulizia", priceFromCents: 4500, durationMinutes: 120)));
        var id = created.GetProperty("id").GetGuid();
        var published = await ReadAsync(await owner.PostAsync($"{Url}/{id}/publish", null));

        using var form = new MultipartFormDataContent();
        var png = new ByteArrayContent(PngBytes);
        png.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(png, "photos", "foto.png");

        var attempts = new Dictionary<string, HttpResponseMessage>
        {
            ["GET"] = await other.GetAsync($"{Url}/{id}"),
            ["PUT"] = await other.PutAsJsonAsync($"{Url}/{id}", UpdateBody("Rubato", published.GetProperty("version").GetUInt32())),
            ["DELETE"] = await other.DeleteAsync($"{Url}/{id}"),
            ["publish"] = await other.PostAsync($"{Url}/{id}/publish", null),
            ["pause"] = await other.PostAsync($"{Url}/{id}/pause", null),
            ["duplicate"] = await other.PostAsync($"{Url}/{id}/duplicate", null),
            ["photos"] = await other.PostAsync($"{Url}/{id}/photos", form),
        };

        foreach (var (name, response) in attempts)
        {
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{name}: {(int)response.StatusCode}");
            Assert.Equal("supplier_service_not_found", (await ReadAsync(response)).GetProperty("code").GetString());
        }

        // The other supplier sees none of it, and the owner's service is exactly as it was.
        Assert.Equal(0, (await ReadAsync(await other.GetAsync(Url))).GetProperty("total").GetInt32());
        var mine = await ReadAsync(await owner.GetAsync($"{Url}/{id}"));
        Assert.Equal("Pulizia", mine.GetProperty("name").GetString());
        Assert.Equal("Active", mine.GetProperty("status").GetString());
        Assert.Empty(Strings(mine.GetProperty("photoUrls")));
        Assert.Equal(1, (await ReadAsync(await owner.GetAsync(Url))).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task TheSameNameForTwoSuppliers_GivesTheSameSlugToEach_AndEachListsOnlyItsOwn()
    {
        var (aId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        var (bId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var a = SupplierClient(aId);
        using var b = SupplierClient(bId);

        var fromA = await ReadAsync(await a.PostAsJsonAsync(Url, Body("Pulizia")));
        var fromB = await ReadAsync(await b.PostAsJsonAsync(Url, Body("Pulizia")));

        Assert.Equal("pulizia", fromA.GetProperty("slug").GetString());
        Assert.Equal("pulizia", fromB.GetProperty("slug").GetString());
        Assert.Equal(fromA.GetProperty("id").GetGuid(), (await ReadAsync(await a.GetAsync(Url))).GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(fromB.GetProperty("id").GetGuid(), (await ReadAsync(await b.GetAsync(Url))).GetProperty("items")[0].GetProperty("id").GetGuid());
    }

    // ─── Feature flags (SP-02) ───────────────────────────────────────────────────

    [Fact]
    public async Task PublicFeatures_ListsEveryFlagWithTheTwoSupplierOnesOffByDefault()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/public/features");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal(
            FeatureFlags.All.Select(JsonNamingPolicy.CamelCase.ConvertName).Order(),
            body.EnumerateObject().Select(p => p.Name).Order());
        Assert.False(body.GetProperty("supplierShowcaseBooking").GetBoolean());
        Assert.False(body.GetProperty("supplierOnlinePayments").GetBoolean());
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private HttpClient SupplierClient(string userId) => factory.CreateAuthenticatedClient(userId, roles: "Supplier");

    internal static object Body(string name, int? priceFromCents = null, int? durationMinutes = null) =>
        new { name, category = "cleaning", priceFromCents, durationMinutes };

    private static object UpdateBody(
        string name,
        uint version,
        int? priceFromCents = null,
        int? durationMinutes = null,
        string[]? photoUrls = null) =>
        new { name, category = "cleaning", priceFromCents, durationMinutes, version, photoUrls };

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, object? body, string language)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Accept-Language", language);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToArray();
}

/// <summary>
/// The two hosts of the catalog tests start one after the other: without a PostgreSQL server the web host falls back to one
/// in-memory database with a fixed name (<c>CasazenTest</c>) and each host seeds the comuni sample into it at startup, so two
/// hosts starting together would race on the same rows. On PostgreSQL (CI) every factory has its own database.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SupplierCatalogHostsCollection
{
    public const string Name = "SupplierCatalogHosts";
}

/// <summary>The default integration factory with both SP-02 supplier flags on.</summary>
public sealed class SupplierFlagsEnabledFactory : CasazenWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Features:SupplierShowcaseBooking"] = "true",
                ["Features:SupplierOnlinePayments"] = "true",
            }));
    }
}

/// <summary>
/// SP-02: with both supplier flags on the frontend sees them (<c>GET /api/public/features</c>), and the catalog behaves exactly
/// as with them off (it never depends on a flag: every supplier has one).
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class SupplierFlagsEnabledTests(SupplierFlagsEnabledFactory factory) : IClassFixture<SupplierFlagsEnabledFactory>
{
    [Fact]
    public async Task PublicFeatures_BothSupplierFlagsOn_AreExposedAsTrue()
    {
        using var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/public/features");

        Assert.True(body.GetProperty("supplierShowcaseBooking").GetBoolean());
        Assert.True(body.GetProperty("supplierOnlinePayments").GetBoolean());
        // The flags that stay off stay off.
        Assert.False(body.GetProperty("otaPartnerApi").GetBoolean());
    }

    [Fact]
    public async Task Catalog_WorksTheSameWithTheFlagsOn()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = factory.CreateAuthenticatedClient(userId, roles: "Supplier");

        var created = await client.PostAsJsonAsync("/api/supplier/services", SupplierServiceCatalogIntegrationTests.Body("Pulizia"));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/supplier/services")).StatusCode);
    }
}
