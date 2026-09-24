using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// LT-05 (A7-06) over the real pipeline on PostgreSQL: a landlord with only the long-rent context (onboarding
/// "Locazioni di lungo periodo") manages its properties and their APE, without reaching the short-stay endpoints; an
/// org-wide PropertyManager creates and reads leases on the org's properties (TN-3); another org sees nothing.
/// </summary>
public class LongRentPropertyAccessIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string LongTermLandlord = "LongTermLandlord";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CasazenWebApplicationFactory _factory;

    public LongRentPropertyAccessIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task PropertiesAndDocuments_AsLongTermLandlordOnly_Return200()
    {
        var landlord = UniqueUser("solo");
        var property = await _factory.SeedPropertyAsync(landlord);
        using var client = _factory.CreateAuthenticatedClient(landlord, LongTermLandlord);

        var list = await client.GetAsync("/api/properties");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains(property.Id, await ReadIdsAsync(list));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/properties/{property.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/orgs/me/entitlement")).StatusCode);

        var upload = await client.PostAsync($"/api/properties/{property.Id}/documents", ApeForm());
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var documentId = (await ReadJsonAsync(upload)).GetProperty("id").GetGuid();

        var documents = await client.GetAsync($"/api/properties/{property.Id}/documents");
        Assert.Equal(HttpStatusCode.OK, documents.StatusCode);
        var ape = Assert.Single((await ReadJsonAsync(documents)).EnumerateArray());
        // The lease form decides "APE on file" from this field: it must be in the list (A7-06).
        Assert.Equal("Ape", ape.GetProperty("documentType").GetString());
        Assert.False(ape.TryGetProperty("storageUrl", out _));

        // Private bucket (FD-07): the file comes back only through the authenticated endpoint.
        var download = await client.GetAsync($"/api/properties/{property.Id}/documents/{documentId}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(await download.Content.ReadAsByteArrayAsync())[..4]);
    }

    [PostgresFact]
    public async Task CreateAndUpdate_AsLongTermLandlordOnly_WithoutShortStayData_Succeed()
    {
        var landlord = UniqueUser("create");
        await _factory.SeedOrgForOwnerAsync(landlord);
        using var client = _factory.CreateAuthenticatedClient(landlord, LongTermLandlord);

        // A long-term property has no nightly rate nor short-stay guests.
        var create = await client.PostAsJsonAsync("/api/properties", PropertyBody("Bilocale Monza"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var id = (await ReadJsonAsync(create)).GetProperty("id").GetGuid();

        var update = await client.PutAsJsonAsync($"/api/properties/{id}", PropertyBody("Bilocale Monza centro"));
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var saved = await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == id);
        Assert.Equal(landlord, saved.OwnerId);
        Assert.Equal("Bilocale Monza centro", saved.Name);
        Assert.Equal(0m, saved.NightlyRate);
        Assert.Equal(Casazen.Core.Entities.Enums.PropertyComplianceStatus.Pending, saved.ComplianceStatus);
    }

    [PostgresTheory]
    [InlineData("GET", "/api/bookings")]
    [InlineData("POST", "/api/bookings")]
    [InlineData("GET", "/api/bookings/calendar?propertyId={property}&startDate=2026-10-01&endDate=2026-10-31")]
    [InlineData("GET", "/api/properties/{property}/ical/status")]
    [InlineData("POST", "/api/properties/{property}/ical/import-url")]
    [InlineData("GET", "/api/properties/{property}/ical/export-url")]
    [InlineData("GET", "/api/properties/{property}/detail")]
    [InlineData("GET", "/api/properties/{property}/images")]
    [InlineData("GET", "/api/properties/{property}/compliance/activation")]
    [InlineData("GET", "/api/properties/{property}/compliance/safety-checklist")]
    [InlineData("GET", "/api/pricing-adapter/config/{property}")]
    [InlineData("GET", "/api/payments")]
    [InlineData("GET", "/api/guests")]
    [InlineData("GET", "/api/alloggiati/summary")]
    public async Task ShortStayEndpoints_AsLongTermLandlordOnly_Return403(string method, string route)
    {
        var landlord = UniqueUser("short");
        var property = await _factory.SeedPropertyAsync(landlord);
        using var client = _factory.CreateAuthenticatedClient(landlord, LongTermLandlord);
        var url = route.Replace("{property}", property.Id.ToString());

        var response = method == "GET"
            ? await client.GetAsync(url)
            : await client.PostAsJsonAsync(url, new { importUrl = "https://example.com/cal.ics" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [PostgresFact]
    public async Task Lease_AsPropertyManagerOfTheOrg_CreatesAndReadsLeaseOnColleaguesProperty()
    {
        var owner = UniqueUser("owner");
        var manager = UniqueUser("manager");
        var property = await _factory.SeedPropertyAsync(owner);
        await AddUserToOrgAsync(owner, manager);
        using var client = _factory.CreateAuthenticatedClient(manager, $"{LongTermLandlord},PropertyManager");

        // The manager sees the org's properties (to pick one in the lease form) and their documents.
        Assert.Contains(property.Id, await ReadIdsAsync(await client.GetAsync("/api/properties")));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/properties/{property.Id}/documents")).StatusCode);

        var create = await client.PostAsJsonAsync("/api/leases", LeaseBody(property.Id));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var leaseId = (await ReadJsonAsync(create)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/leases/{leaseId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/leases/{leaseId}/rli/advisory")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/leases/{leaseId}/rli/checklist")).StatusCode);
        Assert.Contains(leaseId, await ReadIdsAsync(await client.GetAsync("/api/leases")));
    }

    [PostgresFact]
    public async Task Lease_AsSameOrgLandlordWithoutOrgWideRole_Returns403OnColleaguesProperty()
    {
        var owner = UniqueUser("owner2");
        var colleague = UniqueUser("colleague");
        var property = await _factory.SeedPropertyAsync(owner);
        await AddUserToOrgAsync(owner, colleague);
        using var client = _factory.CreateAuthenticatedClient(colleague, LongTermLandlord);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/properties/{property.Id}/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/leases", LeaseBody(property.Id))).StatusCode);
        Assert.DoesNotContain(property.Id, await ReadIdsAsync(await client.GetAsync("/api/properties")));
    }

    [PostgresFact]
    public async Task PropertyAndLease_AsLandlordOfAnotherOrg_Return404Or403()
    {
        var owner = UniqueUser("owner3");
        var outsider = UniqueUser("outsider");
        var property = await _factory.SeedPropertyAsync(owner);
        await _factory.SeedOrgForOwnerAsync(outsider);
        using var ownerClient = _factory.CreateAuthenticatedClient(owner, LongTermLandlord);
        var leaseId = (await ReadJsonAsync(await ownerClient.PostAsJsonAsync("/api/leases", LeaseBody(property.Id))))
            .GetProperty("id").GetGuid();
        using var client = _factory.CreateAuthenticatedClient(outsider, $"{LongTermLandlord},PropertyManager");

        var responses = new[]
        {
            await client.GetAsync($"/api/properties/{property.Id}"),
            await client.GetAsync($"/api/properties/{property.Id}/documents"),
            await client.PostAsync($"/api/properties/{property.Id}/documents", ApeForm()),
            await client.PutAsJsonAsync($"/api/properties/{property.Id}", PropertyBody("Presa")),
            await client.PostAsJsonAsync("/api/leases", LeaseBody(property.Id)),
            await client.GetAsync($"/api/leases/{leaseId}"),
            await client.GetAsync($"/api/leases/{leaseId}/rli/advisory"),
            await client.GetAsync($"/api/properties/{property.Id}/canone-concordato/attestation-guidance"),
        };

        Assert.All(responses, r => Assert.True(
            r.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden,
            $"{r.RequestMessage!.Method} {r.RequestMessage.RequestUri} answered {(int)r.StatusCode} to another org."));
        Assert.DoesNotContain(property.Id, await ReadIdsAsync(await client.GetAsync("/api/properties")));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.LeaseContracts.IgnoreQueryFilters().CountAsync(l => l.PropertyId == property.Id));
        Assert.False(await db.PropertyDocuments.IgnoreQueryFilters().AnyAsync(d => d.PropertyId == property.Id));
        Assert.NotEqual("Presa", (await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == property.Id)).Name);
    }

    private static string UniqueUser(string role) => $"auth0|lt05-{role}-{Guid.NewGuid():N}";

    private static MultipartFormDataContent ApeForm()
    {
        var form = new MultipartFormDataContent();
        var pdf = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj <<>> endobj\n%%EOF"));
        pdf.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(pdf, "file", "ape.pdf");
        form.Add(new StringContent("Ape"), "documentType");
        return form;
    }

    private static object PropertyBody(string name) => new
    {
        name,
        description = "Bilocale per locazione a lungo termine",
        address = $"Via Italia {Guid.NewGuid():N}",
        city = "Monza",
        postalCode = "20900",
        bedrooms = 2,
        bathrooms = 1,
        maxGuests = 0,
        nightlyRate = 0,
        isActive = true,
    };

    private static object LeaseBody(Guid propertyId) => new
    {
        propertyId,
        fiscalRegime = "CedolareSecca",
        startDate = "2026-11-01T00:00:00Z",
        endDate = "2030-10-31T00:00:00Z",
        monthlyRent = 900m,
        parties = new object[]
        {
            new { role = "Landlord", firstName = "Mario", lastName = "Rossi", fiscalCode = "RSSMRA80A01H501Z", citizenship = "IT", contactEmail = "mario@example.com" },
            new { role = "Tenant", firstName = "Giulia", lastName = "Verdi", fiscalCode = "VRDGLI85B02F205X", citizenship = "IT", contactEmail = "giulia@example.com" },
        },
    };

    private async Task AddUserToOrgAsync(string ownerId, string userId)
    {
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Id = userId,
            Email = $"{Guid.NewGuid():N}@example.com",
            FirstName = "Collega",
            LastName = "Stessa Org",
            OrgId = org.Id,
            IsActive = true,
        };
        db.Users.Add(user);
        // A colleague who completed the onboarding for the org (PL-02): the checks are about ownership.
        await HostOnboardingSeed.MarkOnboardedAsync(db, user, org.Id, scope.ServiceProvider.GetRequiredService<ILegalDocumentService>());
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), JsonOptions);

    private static async Task<IReadOnlyList<Guid>> ReadIdsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadJsonAsync(response)).EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
    }
}
