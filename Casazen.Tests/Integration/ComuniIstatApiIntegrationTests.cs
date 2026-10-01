using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SU-04: the search of the official ISTAT comuni list (public, rate limited) and the admin import, on PostgreSQL. The test
/// database holds the 29 official rows of <see cref="ComuneTestData"/> (see the factory); the import tests use a factory
/// without them.
/// </summary>
public class ComuniIstatApiIntegrationTests : IClassFixture<CasazenWebApplicationFactory>, IClassFixture<ComuniIstatApiIntegrationTests.EmptyListFactory>
{
    private readonly CasazenWebApplicationFactory _factory;
    private readonly EmptyListFactory _emptyFactory;

    public ComuniIstatApiIntegrationTests(CasazenWebApplicationFactory factory, EmptyListFactory emptyFactory)
    {
        _factory = factory;
        _emptyFactory = emptyFactory;
    }

    /// <summary>A host where the official list was never imported.</summary>
    public sealed class EmptyListFactory : CasazenWebApplicationFactory
    {
        protected override bool SeedComuneSample => false;
    }

    // ---- Search ------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Search_Anonymous_ReturnsTheComuneWithItsProvinceAndRegion()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/comuni?q=torino");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("datasetAvailable").GetBoolean());
        var torino = Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal(
            ("001272", "L219", "Torino", "TO", "PIE", "01", "Piemonte", true),
            (
                torino.GetProperty("istatCode").GetString(),
                torino.GetProperty("cadastralCode").GetString(),
                torino.GetProperty("name").GetString(),
                torino.GetProperty("provinceCode").GetString(),
                torino.GetProperty("regionCode").GetString(),
                torino.GetProperty("regionIstatCode").GetString(),
                torino.GetProperty("regionName").GetString(),
                torino.GetProperty("isActive").GetBoolean()));
    }

    [PostgresTheory]
    [InlineData("forli", "040012")]
    [InlineData("Forlì", "040012")]
    [InlineData("reggio nell'emilia", "035033")]
    [InlineData("bozen", "021008")]
    [InlineData("H501", "058091")]
    [InlineData("F205", "015146")]
    [InlineData("048017", "048017")]
    public async Task Search_AccentsOtherLanguageAndCodes_FindTheComune(string query, string expectedIstat)
    {
        using var client = _factory.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>($"/api/comuni?q={Uri.EscapeDataString(query)}");

        Assert.Contains(body.GetProperty("items").EnumerateArray(), c => c.GetProperty("istatCode").GetString() == expectedIstat);
    }

    [PostgresFact]
    public async Task Search_QueryTooShort_Returns400ValidationError()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/comuni?q=m");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_error", problem.GetProperty("code").GetString());
        Assert.Contains("2", problem.GetProperty("errors").GetProperty("q")[0].GetString());
    }

    [PostgresFact]
    public async Task Search_LimitIsRespectedAndCapped()
    {
        using var client = _factory.CreateClient();

        var two = await client.GetFromJsonAsync<JsonElement>("/api/comuni?q=an&limit=2");
        var capped = await client.GetFromJsonAsync<JsonElement>("/api/comuni?q=an&limit=100000");
        var floor = await client.GetFromJsonAsync<JsonElement>("/api/comuni?q=an&limit=0");

        Assert.Equal(2, two.GetProperty("items").GetArrayLength());
        Assert.InRange(capped.GetProperty("items").GetArrayLength(), 3, 25);
        Assert.Equal(1, floor.GetProperty("items").GetArrayLength());
    }

    [PostgresFact]
    public async Task Search_SameNameInTwoProvinces_ListsBoth()
    {
        using var client = _factory.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/comuni?q=castro");

        Assert.Equal(
            ["BG", "LE"],
            body.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("provinceCode").GetString()!).Order());
    }

    [PostgresFact]
    public async Task GetByIstatCode_ReturnsTheComune_AndAnUnknownOneIs404WithItsCode()
    {
        using var client = _factory.CreateClient();

        var genova = await client.GetFromJsonAsync<JsonElement>("/api/comuni/010025");
        Assert.Equal(("Genova", "D969", "LIG"), (genova.GetProperty("name").GetString(), genova.GetProperty("cadastralCode").GetString(), genova.GetProperty("regionCode").GetString()));

        var unknown = await client.GetAsync("/api/comuni/999999");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("comune_istat_unknown", (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/comuni/12")).StatusCode);
    }

    [PostgresFact]
    public async Task GetByIstatCode_AComuneNoLongerInTheList_IsReturnedAsInactive()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var peglio = await db.Comuni.SingleAsync(c => c.IstatCode == "013178");
            peglio.IsActive = false;
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateClient();
        var byCode = await client.GetFromJsonAsync<JsonElement>("/api/comuni/013178");
        var search = await client.GetFromJsonAsync<JsonElement>("/api/comuni?q=peglio");

        Assert.False(byCode.GetProperty("isActive").GetBoolean());
        Assert.Equal(["PU"], search.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("provinceCode").GetString()));

        using var restore = _factory.Services.CreateScope();
        var restoreDb = restore.ServiceProvider.GetRequiredService<AppDbContext>();
        (await restoreDb.Comuni.SingleAsync(c => c.IstatCode == "013178")).IsActive = true;
        await restoreDb.SaveChangesAsync();
    }

    [PostgresFact]
    public async Task Status_NamesTheSourceAndTheReferenceDate()
    {
        using var client = _factory.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/comuni/status");

        Assert.True(body.GetProperty("datasetAvailable").GetBoolean());
        Assert.Equal("2026-02-21", body.GetProperty("referenceDate").GetString());
        Assert.Equal(ComuneTestData.SampleSourceVersion, body.GetProperty("sourceVersion").GetString());
    }

    [PostgresFact]
    public async Task Search_ListNotImported_SaysSoInsteadOfAnEmptyList()
    {
        using var client = _emptyFactory.CreateClient();

        var search = await client.GetFromJsonAsync<JsonElement>("/api/comuni?q=milano");
        var status = await client.GetFromJsonAsync<JsonElement>("/api/comuni/status");
        // Not even a query that would be refused: the list is the problem, not the question.
        var short_ = await client.GetAsync("/api/comuni?q=m");

        Assert.False(search.GetProperty("datasetAvailable").GetBoolean());
        Assert.Equal(0, search.GetProperty("items").GetArrayLength());
        Assert.False(status.GetProperty("datasetAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("referenceDate").ValueKind);
        Assert.Equal(HttpStatusCode.OK, short_.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/comuni/015146")).StatusCode);
    }

    // ---- Admin import ------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Import_NotAnAdmin_IsRefused()
    {
        using var host = _emptyFactory.CreateAuthenticatedClient($"auth0|host-{Guid.NewGuid():N}", "PropertyOwner");
        using var anonymous = _emptyFactory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await ImportAsync(host, SampleCsv())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ImportAsync(anonymous, SampleCsv())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/admin/comuni")).StatusCode);
    }

    [PostgresFact]
    public async Task Import_ValidFile_LoadsTheListLogsTheSourceAndIsIdempotent()
    {
        await using var factory = new EmptyListFactory();
        using var admin = factory.CreateAuthenticatedClient($"auth0|admin-{Guid.NewGuid():N}", "Admin");
        using var anonymous = factory.CreateClient();

        var before = await admin.GetFromJsonAsync<JsonElement>("/api/admin/comuni");
        Assert.False(before.GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("lastImport").ValueKind);

        var first = await ImportAsync(admin, SampleCsv());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((29, 29, 0, 0, 0), (Int(firstBody, "rows"), Int(firstBody, "inserted"), Int(firstBody, "updated"), Int(firstBody, "unchanged"), Int(firstBody, "deactivated")));

        // The same file again changes nothing.
        var second = await (await ImportAsync(admin, SampleCsv())).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((29, 0, 0, 29, 0), (Int(second, "rows"), Int(second, "inserted"), Int(second, "updated"), Int(second, "unchanged"), Int(second, "deactivated")));

        var status = await admin.GetFromJsonAsync<JsonElement>("/api/admin/comuni");
        Assert.True(status.GetProperty("available").GetBoolean());
        Assert.Equal((29, 29), (Int(status, "totalRows"), Int(status, "activeRows")));
        var last = status.GetProperty("lastImport");
        Assert.Equal(("AdminUpload", "2026-02-21", 64), (last.GetProperty("origin").GetString(), last.GetProperty("referenceDate").GetString(), last.GetProperty("sha256").GetString()!.Length));
        Assert.Equal("ISTAT test sample", last.GetProperty("sourceVersion").GetString());

        // The pickers see it at once.
        var search = await anonymous.GetFromJsonAsync<JsonElement>("/api/comuni?q=varenna");
        Assert.Equal("097084", Assert.Single(search.GetProperty("items").EnumerateArray()).GetProperty("istatCode").GetString());
    }

    [PostgresFact]
    public async Task Import_InvalidFile_Returns422WithTheLinesAndWritesNothing()
    {
        await using var factory = new EmptyListFactory();
        using var admin = factory.CreateAuthenticatedClient($"auth0|admin-{Guid.NewGuid():N}", "Admin");
        var csv = "Codice Regione;Codice Comune formato alfanumerico;Denominazione in italiano;Denominazione Regione;Sigla automobilistica;Codice Catastale del Comune\n"
                  + "03;999001;Prova Uno;Lombardia;MI;Z901\n"
                  + "03;99X;Prova Due;Lombardia;MI;Z902\n";

        var response = await ImportAsync(admin, csv);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("comuni_import_invalid", problem.GetProperty("code").GetString());
        var line = Assert.Single(problem.GetProperty("lines").EnumerateArray());
        Assert.Equal((3, "istat_invalid"), (Int(line, "line"), line.GetProperty("error").GetString()));
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Comuni.CountAsync());
    }

    [PostgresFact]
    public async Task Import_MissingFields_Returns400WithTheFieldsNamed()
    {
        using var admin = _emptyFactory.CreateAuthenticatedClient($"auth0|admin-{Guid.NewGuid():N}", "Admin");
        using var form = new MultipartFormDataContent { { new StringContent("2026-13-45"), "referenceDate" } };

        var response = await admin.PostAsync("/api/admin/comuni/import", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("file", out _));
        Assert.True(errors.TryGetProperty("sourceVersion", out _));
        Assert.True(errors.TryGetProperty("referenceDate", out _));
    }

    [PostgresFact]
    public async Task Import_ListOlderThanTheImportedOne_IsRefused()
    {
        await using var factory = new EmptyListFactory();
        using var admin = factory.CreateAuthenticatedClient($"auth0|admin-{Guid.NewGuid():N}", "Admin");
        Assert.Equal(HttpStatusCode.OK, (await ImportAsync(admin, SampleCsv())).StatusCode);

        var older = await ImportAsync(admin, SampleCsv(), referenceDate: "2025-12-31");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, older.StatusCode);
        var line = Assert.Single((await older.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lines").EnumerateArray());
        Assert.Equal((0, "reference_date_older_than_current"), (Int(line, "line"), line.GetProperty("error").GetString()));
    }

    [PostgresFact]
    public async Task Import_FullListWithoutAComune_DeactivatesItAndPartialDoesNot()
    {
        await using var factory = new EmptyListFactory();
        using var admin = factory.CreateAuthenticatedClient($"auth0|admin-{Guid.NewGuid():N}", "Admin");
        await ImportAsync(admin, SampleCsv());
        var withoutTorino = string.Join('\n', SampleCsv().Split('\n').Where(l => !l.Contains(";001272;")));

        var partial = await (await ImportAsync(admin, withoutTorino, partial: true)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, Int(partial, "deactivated"));

        var full = await (await ImportAsync(admin, withoutTorino)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, Int(full, "deactivated"));
        using var anonymous = factory.CreateClient();
        Assert.Empty((await anonymous.GetFromJsonAsync<JsonElement>("/api/comuni?q=torino")).GetProperty("items").EnumerateArray());
        // A stored code still says which comune it was.
        Assert.False((await anonymous.GetFromJsonAsync<JsonElement>("/api/comuni/001272")).GetProperty("isActive").GetBoolean());
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------

    private static int Int(JsonElement element, string name) => element.GetProperty(name).GetInt32();

    private static string SampleCsv() => File.ReadAllText(ComuneTestData.SamplePath, Encoding.UTF8);

    private static async Task<HttpResponseMessage> ImportAsync(
        HttpClient client,
        string csv,
        string referenceDate = "2026-02-21",
        bool partial = false)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "comuni-test.csv");
        form.Add(new StringContent("ISTAT test sample"), "sourceVersion");
        form.Add(new StringContent(referenceDate), "referenceDate");
        if (partial)
            form.Add(new StringContent("true"), "partial");
        return await client.PostAsync("/api/admin/comuni/import", form);
    }
}
