using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SU-04 on PostgreSQL: the comune a host chooses for a property (validated against the official ISTAT list, the region
/// follows it, nothing inferred from the city), the regional documents (A5-19), the CIN warning and the tourist tax by code.
/// Rows of the official sample; the CIN <c>IT058091...</c> carries the code of Roma.
/// </summary>
public class ComuneIstatPropertyIntegrationTests
    : IClassFixture<CasazenWebApplicationFactory>, IClassFixture<ComuniIstatApiIntegrationTests.EmptyListFactory>
{
    private const string CinOfRoma = "IT058091C27G5FFZDZ";

    private readonly CasazenWebApplicationFactory _factory;
    private readonly ComuniIstatApiIntegrationTests.EmptyListFactory _emptyFactory;

    public ComuneIstatPropertyIntegrationTests(
        CasazenWebApplicationFactory factory,
        ComuniIstatApiIntegrationTests.EmptyListFactory emptyFactory)
    {
        _factory = factory;
        _emptyFactory = emptyFactory;
    }

    // ---- Create ------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Create_WithAComune_StoresItsCodeAndTheRegionThatFollowsIt()
    {
        var owner = await NewOwnerAsync(_factory);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var response = await client.PostAsJsonAsync("/api/properties", Body("Como", ComuneTestData.Como));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("013075", "LOM"), (created.GetProperty("comuneIstatCode").GetString(), created.GetProperty("regionCode").GetString()));

        var read = await client.GetFromJsonAsync<JsonElement>($"/api/properties/{created.GetProperty("id").GetGuid()}");
        Assert.Equal(("013075", "LOM", "Como"), (read.GetProperty("comuneIstatCode").GetString(), read.GetProperty("regionCode").GetString(), read.GetProperty("city").GetString()));
    }

    [PostgresFact]
    public async Task Create_WithoutAComune_LeavesItNullEvenWhenTheCityIsAKnownComune()
    {
        var owner = await NewOwnerAsync(_factory);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var response = await client.PostAsJsonAsync("/api/properties", Body("Roma", comuneIstatCode: null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        // Nothing is inferred from the free-text city.
        Assert.Equal(JsonValueKind.Null, created.GetProperty("comuneIstatCode").ValueKind);
        Assert.Equal(JsonValueKind.Null, created.GetProperty("regionCode").ValueKind);
    }

    [PostgresFact]
    public async Task Create_UnknownComune_Returns422AndCreatesNothing()
    {
        var owner = await NewOwnerAsync(_factory);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var response = await client.PostAsJsonAsync("/api/properties", Body("Altrove", "999999"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("comune_istat_unknown", problem.GetProperty("code").GetString());
        Assert.Contains("999999", problem.GetProperty("detail").GetString());
        Assert.Empty(await client.GetFromJsonAsync<JsonElement[]>("/api/properties") ?? []);
    }

    [PostgresFact]
    public async Task Create_ACodeOfAComuneNoLongerInTheList_IsRefused()
    {
        var owner = await NewOwnerAsync(_factory);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");
        await SetActiveAsync(_factory, ComuneTestData.CastroLecce, false);
        try
        {
            var response = await client.PostAsJsonAsync("/api/properties", Body("Castro", ComuneTestData.CastroLecce));

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }
        finally
        {
            await SetActiveAsync(_factory, ComuneTestData.CastroLecce, true);
        }
    }

    [PostgresTheory]
    [InlineData("1272")]
    [InlineData("01272A")]
    [InlineData("0012720")]
    public async Task Create_MalformedComuneCode_Returns400(string code)
    {
        var owner = await NewOwnerAsync(_factory);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var response = await client.PostAsJsonAsync("/api/properties", Body("Torino", code));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [PostgresFact]
    public async Task Create_ListNotImported_Returns422DatasetUnavailable()
    {
        var owner = await NewOwnerAsync(_emptyFactory);
        using var client = _emptyFactory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var response = await client.PostAsJsonAsync("/api/properties", Body("Como", ComuneTestData.Como));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("comuni_dataset_unavailable", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // Without a comune the property is created as before: the list is not needed for the free-text city.
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/properties", Body("Como", comuneIstatCode: null))).StatusCode);
    }

    // ---- Update ------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Update_ChangeOfComune_ChangesTheRegionWithIt()
    {
        var (owner, propertyId) = await SeedPropertyAsync(_factory, "Como", ComuneTestData.Como, "LOM");
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var response = await client.PutAsJsonAsync($"/api/properties/{propertyId}", new { city = "Roma", comuneIstatCode = ComuneTestData.Roma });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = await ReadPropertyAsync(_factory, propertyId);
        Assert.Equal(("Roma", "058091", "LAZ"), (stored.City, stored.ComuneIstatCode, stored.RegionCode));
    }

    [PostgresFact]
    public async Task Update_CityRewrittenWithoutAComune_ClearsTheStoredComune()
    {
        var (owner, propertyId) = await SeedPropertyAsync(_factory, "Como", ComuneTestData.Como, "LOM");
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var response = await client.PutAsJsonAsync($"/api/properties/{propertyId}", new { city = "Lecco" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = await ReadPropertyAsync(_factory, propertyId);
        Assert.Equal(("Lecco", null, null), (stored.City, stored.ComuneIstatCode, stored.RegionCode));
    }

    [PostgresFact]
    public async Task Update_OtherFieldsOnly_KeepTheComune()
    {
        var (owner, propertyId) = await SeedPropertyAsync(_factory, "Como", ComuneTestData.Como, "LOM");
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        // The same city (case and spaces aside) is not a change either.
        var response = await client.PutAsJsonAsync($"/api/properties/{propertyId}", new { name = "Altro nome", city = " como " });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = await ReadPropertyAsync(_factory, propertyId);
        Assert.Equal(("013075", "LOM"), (stored.ComuneIstatCode, stored.RegionCode));
    }

    [PostgresFact]
    public async Task Update_NullClearsTheComuneAndItsRegion()
    {
        var (owner, propertyId) = await SeedPropertyAsync(_factory, "Como", ComuneTestData.Como, "LOM");
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var response = await client.PutAsJsonAsync($"/api/properties/{propertyId}", new { comuneIstatCode = (string?)null });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = await ReadPropertyAsync(_factory, propertyId);
        Assert.Equal(("Como", null, null), (stored.City, stored.ComuneIstatCode, stored.RegionCode));
    }

    [PostgresFact]
    public async Task Update_UnknownComune_Returns422AndKeepsTheStoredOne()
    {
        var (owner, propertyId) = await SeedPropertyAsync(_factory, "Como", ComuneTestData.Como, "LOM");
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var response = await client.PutAsJsonAsync($"/api/properties/{propertyId}", new { comuneIstatCode = "999999" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("013075", (await ReadPropertyAsync(_factory, propertyId)).ComuneIstatCode);
    }

    [PostgresFact]
    public async Task Update_AComuneLaterRemovedFromTheList_DoesNotBlockTheOtherEdits()
    {
        var (owner, propertyId) = await SeedPropertyAsync(_factory, "Samone", "001235", "PIE");
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");
        await SetActiveAsync(_factory, "001235", false);
        try
        {
            // The code is not changed, so it is not asked of the list again.
            var response = await client.PutAsJsonAsync($"/api/properties/{propertyId}", new { name = "Nuovo nome", comuneIstatCode = "001235" });

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal("001235", (await ReadPropertyAsync(_factory, propertyId)).ComuneIstatCode);
        }
        finally
        {
            await SetActiveAsync(_factory, "001235", true);
        }
    }

    // ---- Regional documents (A5-19) ----------------------------------------------------------------------------

    [PostgresTheory]
    [InlineData(ComuneTestData.Como, "LOM", "Ape")]
    [InlineData(ComuneTestData.Roma, "LAZ", "PropertyLicense")]
    public async Task Activation_Documents_FollowTheRegionOfTheComune(string istat, string region, string requiredHere)
    {
        var (owner, propertyId) = await SeedPropertyAsync(_factory, "Qualcosa", istat, region);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var documents = await GetStepAsync(client, propertyId, "documents");

        Assert.Equal("pending", documents.GetProperty("status").GetString());
        Assert.Contains("CinCertificate", documents.GetProperty("message").GetString());
        Assert.Contains(requiredHere, documents.GetProperty("message").GetString());
    }

    [PostgresTheory]
    [InlineData(null, null)]
    [InlineData(ComuneTestData.Napoli, "CAM")]
    public async Task Activation_Documents_NoComuneOrARegionWithoutRules_GetTheDefaultList(string? istat, string? region)
    {
        var (owner, propertyId) = await SeedPropertyAsync(_factory, "Lombardia ma niente codice", istat, region);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var documents = await GetStepAsync(client, propertyId, "documents");

        // The city is never compared with the keys of the configuration: only CinCertificate is required.
        var message = documents.GetProperty("message").GetString()!;
        Assert.Contains("CinCertificate", message);
        Assert.DoesNotContain("Ape", message);
        Assert.DoesNotContain("PropertyLicense", message);
    }

    // ---- CIN warning and notes ----------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Activation_CinOfAnotherComune_IsAWarningNeverABlocker()
    {
        var (owner, propertyId) = await SeedPropertyAsync(_factory, "Milano", ComuneTestData.Milano, "LOM", CinOfRoma);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var cin = await GetStepAsync(client, propertyId, "cin");
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/properties/{propertyId}/detail");
        var record = await client.GetFromJsonAsync<JsonElement>($"/api/properties/{propertyId}");

        Assert.Equal("complete", cin.GetProperty("status").GetString());
        Assert.Empty(cin.GetProperty("blockers").EnumerateArray());
        var warning = Assert.Single(cin.GetProperty("warnings").EnumerateArray());
        Assert.Equal(("cin", "cin_istat_comune_mismatch"), (warning.GetProperty("step").GetString(), warning.GetProperty("code").GetString()));
        Assert.Contains("058091", warning.GetProperty("message").GetString());
        Assert.Contains("015146", warning.GetProperty("message").GetString());
        Assert.True(detail.GetProperty("cinIstatMismatch").GetBoolean());
        Assert.True(record.GetProperty("cinIstatMismatch").GetBoolean());
    }

    [PostgresFact]
    public async Task Activation_CinOfTheSameComune_HasNoWarning()
    {
        var (owner, propertyId) = await SeedPropertyAsync(_factory, "Roma", ComuneTestData.Roma, "LAZ", CinOfRoma);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var cin = await GetStepAsync(client, propertyId, "cin");
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/properties/{propertyId}/detail");

        Assert.Empty(cin.GetProperty("warnings").EnumerateArray());
        Assert.False(detail.GetProperty("cinIstatMismatch").GetBoolean());
    }

    [PostgresFact]
    public async Task Activation_NoComuneChosen_NothingIsComparedAndTheHostIsToldToChooseOne()
    {
        var (owner, propertyId) = await SeedPropertyAsync(_factory, "Milano", null, null, CinOfRoma);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var cin = await GetStepAsync(client, propertyId, "cin");
        var baseData = await GetStepAsync(client, propertyId, "base-data");

        Assert.Empty(cin.GetProperty("warnings").EnumerateArray());
        var note = Assert.Single(baseData.GetProperty("warnings").EnumerateArray());
        Assert.Equal("comune_istat_missing", note.GetProperty("code").GetString());
        Assert.False(baseData.GetProperty("blockers").EnumerateArray().Any(), "choosing the comune never blocks the activation");
    }

    [PostgresFact]
    public async Task Activation_ListNotImported_NoNoteAboutChoosingAComune()
    {
        var (owner, propertyId) = await SeedPropertyAsync(_emptyFactory, "Milano", null, null, CinOfRoma);
        using var client = _emptyFactory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var baseData = await GetStepAsync(client, propertyId, "base-data");

        Assert.Empty(baseData.GetProperty("warnings").EnumerateArray());
    }

    // ---- Tourist tax by code ---------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Activation_TouristTax_ByTheCodeOfTheChosenComune_WhateverTheCityIsCalled()
    {
        // The rate of Milano carries its ISTAT code (015146): a property whose city is written differently finds it by code.
        var (owner, propertyId) = await SeedPropertyAsync(_factory, "Milano (MI)", ComuneTestData.Milano, "LOM");
        var (otherOwner, otherId) = await SeedPropertyAsync(_factory, "Milano (MI)", null, null);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");
        using var otherClient = _factory.CreateAuthenticatedClient(otherOwner, "PropertyOwner");

        var byCode = await GetStepAsync(client, propertyId, "tourist-tax");
        var byName = await GetStepAsync(otherClient, otherId, "tourist-tax");

        Assert.Equal("complete", byCode.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, byCode.GetProperty("touristTax").GetProperty("rate").ValueKind);
        // Written as "Milano (MI)" and with no comune chosen, the name does not match: the host is told there is no rate.
        Assert.Equal("warning", byName.GetProperty("status").GetString());
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------

    private static object Body(string city, string? comuneIstatCode) => new
    {
        name = "Appartamento di prova",
        address = $"Via di prova {Guid.NewGuid():N}",
        city,
        comuneIstatCode,
        bedrooms = 2,
        bathrooms = 1,
        maxGuests = 4,
        nightlyRate = 90m,
        cinCode = CinOfRoma,
    };

    private static async Task<string> NewOwnerAsync(CasazenWebApplicationFactory factory)
    {
        var owner = $"auth0|owner-{Guid.NewGuid():N}";
        await factory.SeedOrgForOwnerAsync(owner);
        return owner;
    }

    private static async Task<(string Owner, Guid PropertyId)> SeedPropertyAsync(
        CasazenWebApplicationFactory factory,
        string city,
        string? istat,
        string? region,
        string? cin = null)
    {
        var owner = $"auth0|owner-{Guid.NewGuid():N}";
        var org = await factory.SeedOrgForOwnerAsync(owner);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = new Property
        {
            OwnerId = owner,
            OrgId = org.Id,
            Name = "Appartamento di prova",
            Description = "Appartamento di prova",
            Address = $"Via di prova {Guid.NewGuid():N}",
            City = city,
            PostalCode = "20000",
            ComuneIstatCode = istat,
            RegionCode = region,
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 90m,
            CinCode = cin,
            IsActive = true,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return (owner, property.Id);
    }

    private static async Task<Property> ReadPropertyAsync(CasazenWebApplicationFactory factory, Guid propertyId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == propertyId);
    }

    private static async Task SetActiveAsync(CasazenWebApplicationFactory factory, string istatCode, bool active)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Comuni.SingleAsync(c => c.IstatCode == istatCode)).IsActive = active;
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> GetStepAsync(HttpClient client, Guid propertyId, string stepId)
    {
        var wizard = await client.GetFromJsonAsync<JsonElement>($"/api/properties/{propertyId}/compliance/activation");
        return wizard.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("id").GetString() == stepId).Clone();
    }
}
