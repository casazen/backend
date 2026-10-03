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
/// SU-04 on PostgreSQL: the comuni of a supplier profile (chosen from the official ISTAT list, validated), the matching of
/// suppliers with a property by ISTAT code (A4-12) and the invite of an admin. Rows of the official sample.
/// </summary>
public class ComuneIstatSupplierIntegrationTests
    : IClassFixture<CasazenWebApplicationFactory>, IClassFixture<ComuniIstatApiIntegrationTests.EmptyListFactory>
{
    private readonly CasazenWebApplicationFactory _factory;
    private readonly ComuniIstatApiIntegrationTests.EmptyListFactory _emptyFactory;

    public ComuneIstatSupplierIntegrationTests(
        CasazenWebApplicationFactory factory,
        ComuniIstatApiIntegrationTests.EmptyListFactory emptyFactory)
    {
        _factory = factory;
        _emptyFactory = emptyFactory;
    }

    // ---- Profile -----------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task UpdateProfile_ComuneIstatCodes_AreValidatedStoredAndDescribedWithTheirRegion()
    {
        var supplier = await SeedSupplierAsync(_factory);
        using var client = _factory.CreateAuthenticatedClient(supplier.UserId, "Supplier");

        var response = await client.PutAsJsonAsync("/api/supplier/profile", new { comuneIstatCodes = new[] { ComuneTestData.Como, ComuneTestData.Roma, ComuneTestData.Como } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["013075", "058091"], profile.GetProperty("comuneIstatCodes").EnumerateArray().Select(c => c.GetString()));
        var described = profile.GetProperty("operatingComuni").EnumerateArray().ToList();
        Assert.Equal(
            [("Como", "CO", "LOM"), ("Roma", "RM", "LAZ")],
            described.Select(c => (c.GetProperty("name").GetString(), c.GetProperty("provinceCode").GetString(), c.GetProperty("regionCode").GetString())));
        // What was written as text is not touched.
        Assert.Equal(["Cesano Maderno"], profile.GetProperty("comuni").EnumerateArray().Select(c => c.GetString()));

        var read = await client.GetFromJsonAsync<JsonElement>("/api/supplier/profile");
        Assert.Equal(2, read.GetProperty("operatingComuni").GetArrayLength());
    }

    [PostgresFact]
    public async Task UpdateProfile_UnknownCode_Returns422AndKeepsTheStoredComuni()
    {
        var supplier = await SeedSupplierAsync(_factory, chosen: [ComuneTestData.Como]);
        using var client = _factory.CreateAuthenticatedClient(supplier.UserId, "Supplier");

        var response = await client.PutAsJsonAsync("/api/supplier/profile", new { comuneIstatCodes = new[] { ComuneTestData.Roma, "999999" }, bio = "Non deve essere salvata" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("comune_istat_unknown", problem.GetProperty("code").GetString());
        Assert.Contains("999999", problem.GetProperty("detail").GetString());
        var stored = await ReadProfileAsync(_factory, supplier.OrgId);
        Assert.Equal("[\"013075\"]", stored.ComuneIstatCodesJson);
        Assert.NotEqual("Non deve essere salvata", stored.Bio);
    }

    [PostgresFact]
    public async Task UpdateProfile_MoreThanTheLimit_Returns422()
    {
        var supplier = await SeedSupplierAsync(_factory);
        using var client = _factory.CreateAuthenticatedClient(supplier.UserId, "Supplier");

        var response = await client.PutAsJsonAsync("/api/supplier/profile", new { comuneIstatCodes = Enumerable.Range(100000, 101).Select(i => i.ToString()).ToArray() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("comuni_too_many", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [PostgresFact]
    public async Task UpdateProfile_CodesOmittedOrEmpty_KeepOrClearTheChosenComuni()
    {
        var supplier = await SeedSupplierAsync(_factory, chosen: [ComuneTestData.Como]);
        using var client = _factory.CreateAuthenticatedClient(supplier.UserId, "Supplier");

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/supplier/profile", new { bio = "Solo la bio" })).StatusCode);
        Assert.Equal("[\"013075\"]", (await ReadProfileAsync(_factory, supplier.OrgId)).ComuneIstatCodesJson);

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/supplier/profile", new { comuneIstatCodes = Array.Empty<string>() })).StatusCode);
        Assert.Equal("[]", (await ReadProfileAsync(_factory, supplier.OrgId)).ComuneIstatCodesJson);
    }

    [PostgresFact]
    public async Task UpdateProfile_ListNotImported_ACodeIsRefusedButTheTextStillSaves()
    {
        var supplier = await SeedSupplierAsync(_emptyFactory);
        using var client = _emptyFactory.CreateAuthenticatedClient(supplier.UserId, "Supplier");

        var refused = await client.PutAsJsonAsync("/api/supplier/profile", new { comuneIstatCodes = new[] { ComuneTestData.Como } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("comuni_dataset_unavailable", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // Free text keeps working (as before the list); an empty list of codes needs no list.
        var saved = await client.PutAsJsonAsync("/api/supplier/profile", new { comuni = new[] { "Roma", "Milano" }, comuneIstatCodes = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(["Roma", "Milano"], JsonSerializer.Deserialize<string[]>((await ReadProfileAsync(_emptyFactory, supplier.OrgId)).ComuniJson)!);
    }

    [PostgresFact]
    public async Task Activation_ServicesStep_DoesNotMissComuniWithOnlyComuniChosenFromTheList()
    {
        var supplier = await SeedSupplierAsync(_factory, written: [], chosen: [ComuneTestData.Como]);
        using var client = _factory.CreateAuthenticatedClient(supplier.UserId, "Supplier");

        var activation = await client.GetFromJsonAsync<JsonElement>("/api/supplier/profile/activation");

        // SU-05: categories and comuni are the "services" step; a comune chosen from the list satisfies the comuni requirement.
        var step = activation.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("id").GetString() == "services");
        Assert.NotEqual("comuni_missing", step.GetProperty("blocker").GetString());
    }

    [PostgresFact]
    public async Task PublicShowcase_ListsTheChosenComuniByNameThenWhatWasWritten()
    {
        var supplier = await SeedSupplierAsync(_factory, written: ["Seveso"], chosen: [ComuneTestData.Como]);
        string slug;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            slug = $"fornitore-{Guid.NewGuid():N}";
            var profile = await db.SupplierProfiles.SingleAsync(p => p.OrgId == supplier.OrgId);
            profile.ShowcaseSlug = slug;
            profile.Status = SupplierStatus.Active;
            await db.SaveChangesAsync();
        }

        using var anonymous = _factory.CreateClient();
        var page = await anonymous.GetFromJsonAsync<JsonElement>($"/api/public/suppliers/{slug}");

        Assert.Equal(["Como", "Seveso"], page.GetProperty("comuni").EnumerateArray().Select(c => c.GetString()));
    }

    // ---- Matching with a property (A4-12) -----------------------------------------------------------------------

    [PostgresFact]
    public async Task Suppliers_ForAPropertyWithAComune_AreMatchedByCode_NotByTheWrittenCity()
    {
        var host = await SeedHostWithPropertyAsync("Milano (MI)", ComuneTestData.Milano);
        var inMilano = await SeedActiveSupplierAsync(_factory, chosen: [ComuneTestData.Milano]);
        // The old invite code of the audit: F205 was mapped to Firenze, it is Milano.
        var oldCode = await SeedActiveSupplierAsync(_factory, written: ["F205"]);
        var inFirenze = await SeedActiveSupplierAsync(_factory, chosen: [ComuneTestData.Firenze]);
        var writtenFirenze = await SeedActiveSupplierAsync(_factory, written: ["Firenze", "D612"]);
        using var client = _factory.CreateAuthenticatedClient(host.Owner, "PropertyOwner");

        var response = await client.GetFromJsonAsync<JsonElement>($"/api/suppliers?propertyId={host.PropertyId}&category=cleaning");

        var found = response.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("orgId").GetGuid()).ToList();
        Assert.Contains(inMilano.OrgId, found);
        Assert.Contains(oldCode.OrgId, found);
        Assert.DoesNotContain(inFirenze.OrgId, found);
        Assert.DoesNotContain(writtenFirenze.OrgId, found);
        // The comuni chosen from the list are shown by name.
        var milano = response.GetProperty("items").EnumerateArray().Single(s => s.GetProperty("orgId").GetGuid() == inMilano.OrgId);
        Assert.Equal(["Milano"], milano.GetProperty("comuni").EnumerateArray().Select(c => c.GetString()));
    }

    [PostgresFact]
    public async Task Suppliers_ByAnIstatCodeInTheQuery_FindTheComune()
    {
        var host = await SeedHostWithPropertyAsync("Torino", null);
        var inTorino = await SeedActiveSupplierAsync(_factory, chosen: [ComuneTestData.Torino]);
        var inGenova = await SeedActiveSupplierAsync(_factory, chosen: [ComuneTestData.Genova]);
        using var client = _factory.CreateAuthenticatedClient(host.Owner, "PropertyOwner");

        var response = await client.GetFromJsonAsync<JsonElement>($"/api/suppliers?comune={ComuneTestData.Torino}&category=cleaning");

        var found = response.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("orgId").GetGuid()).ToList();
        Assert.Contains(inTorino.OrgId, found);
        // 010025 is Genova, not Torino (the old registry said Torino).
        Assert.DoesNotContain(inGenova.OrgId, found);
    }

    // ---- Admin invite ------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Invite_ACadastralCode_IsStoredAsTheIstatCodeOfTheComune()
    {
        using var admin = _factory.CreateAuthenticatedClient($"auth0|admin-{Guid.NewGuid():N}", "Admin");
        var email = $"invited-{Guid.NewGuid():N}@test.com";

        var response = await admin.PostAsJsonAsync("/api/admin/suppliers/invite", new { email, comuneCode = "F205" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("015146", (await db.SupplierInviteRecords.SingleAsync(i => i.Email == email)).ComuneCode);
    }

    [PostgresFact]
    public async Task Invite_ACodeThatIsNotAComune_Returns422AndCreatesNothing()
    {
        using var admin = _factory.CreateAuthenticatedClient($"auth0|admin-{Guid.NewGuid():N}", "Admin");
        var email = $"invited-{Guid.NewGuid():N}@test.com";

        var response = await admin.PostAsJsonAsync("/api/admin/suppliers/invite", new { email, comuneCode = "999999" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("comune_istat_unknown", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        using var scope = _factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().SupplierInviteRecords.AnyAsync(i => i.Email == email));
    }

    [PostgresFact]
    public async Task Invite_ListNotImported_TheCodeStaysAsWritten()
    {
        using var admin = _emptyFactory.CreateAuthenticatedClient($"auth0|admin-{Guid.NewGuid():N}", "Admin");
        var email = $"invited-{Guid.NewGuid():N}@test.com";

        var response = await admin.PostAsJsonAsync("/api/admin/suppliers/invite", new { email, comuneCode = "F205" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var scope = _emptyFactory.Services.CreateScope();
        Assert.Equal("F205", (await scope.ServiceProvider.GetRequiredService<AppDbContext>().SupplierInviteRecords.SingleAsync(i => i.Email == email)).ComuneCode);
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------

    private sealed record SeededSupplier(string UserId, Guid OrgId);

    private static async Task<SeededSupplier> SeedSupplierAsync(
        CasazenWebApplicationFactory factory,
        string[]? written = null,
        string[]? chosen = null,
        bool active = false)
    {
        var userId = $"auth0|supplier-{Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new Casazen.Core.Entities.Org
        {
            Name = "Fornitore di prova",
            Slug = $"supplier-{Guid.NewGuid():N}"[..30],
            DisplayName = "Fornitore di prova",
            ContactEmail = $"{Guid.NewGuid():N}@test.com",
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        db.Orgs.Add(org);
        db.Users.Add(new User { Id = userId, Email = org.ContactEmail, FirstName = "Test", LastName = "Supplier", OrgId = org.Id, IsActive = true });
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = org.Id,
            Email = org.ContactEmail,
            LegalName = "Fornitore di prova",
            Phone = "+39 06 999999",
            Status = active ? SupplierStatus.Active : SupplierStatus.Pending,
            CategoriesJson = "[\"cleaning\"]",
            ComuniJson = JsonSerializer.Serialize(written ?? ["Cesano Maderno"]),
            ComuneIstatCodesJson = JsonSerializer.Serialize(chosen ?? []),
        });
        await db.SaveChangesAsync();
        return new SeededSupplier(userId, org.Id);
    }

    private static Task<SeededSupplier> SeedActiveSupplierAsync(
        CasazenWebApplicationFactory factory,
        string[]? written = null,
        string[]? chosen = null) =>
        SeedSupplierAsync(factory, written ?? [], chosen, active: true);

    private async Task<(string Owner, Guid PropertyId)> SeedHostWithPropertyAsync(string city, string? istat)
    {
        var owner = $"auth0|owner-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(owner);
        using var scope = _factory.Services.CreateScope();
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
            RegionCode = istat is null ? null : "LOM",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 80m,
            IsActive = true,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return (owner, property.Id);
    }

    private static async Task<SupplierProfile> ReadProfileAsync(CasazenWebApplicationFactory factory, Guid orgId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.SupplierProfiles.AsNoTracking().SingleAsync(p => p.OrgId == orgId);
    }
}
