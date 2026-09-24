using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// CO-07 (A5-21) on PostgreSQL, through the API: the D.L. 145/2023 art. 13-ter safety checklist
/// (<c>/api/properties/{id}/compliance/safety-checklist</c>) and the activation it blocks. An all-electric home declares
/// gas and CO detectors "not applicable" instead of lying; a home with gas needs both detectors; the compliant systems
/// are asked only when the rental is run as a business; smoke detector and emergency instructions never block.
/// </summary>
public class SafetyChecklistPostgresIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private static readonly string PastDay = DateTime.UtcNow.AddDays(-20).ToString("yyyy-MM-dd");

    private readonly CasazenWebApplicationFactory _factory;

    public SafetyChecklistPostgresIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task AllElectricHomeWithExtinguisher_GasAndCoNotApplicable_ActivationSucceeds()
    {
        var (hostId, propertyId) = await SeedPropertyReadyExceptSafetyAsync();
        using var client = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");

        var before = await CompleteAsync(client, propertyId);
        Assert.Equal(HttpStatusCode.Conflict, before.StatusCode);
        Assert.Contains("safety_extinguishers_missing", BlockerCodes(await ReadJsonAsync(before)));

        var saved = await SaveAsync(client, propertyId, Checklist(
            entrepreneurial: false,
            hasGasSupply: false,
            appliances: [],
            items:
            [
                new { code = "FireExtinguishers", answer = "Present", quantity = 1, location = "Ingresso", checkedOn = PastDay },
                new { code = "BdsrDeclaration", answer = "Present", checkedOn = PastDay },
                new { code = "SmokeDetector", answer = "Missing" },
            ]));

        Assert.True(saved.GetProperty("isComplete").GetBoolean());
        Assert.Empty(saved.GetProperty("blockers").EnumerateArray());
        Assert.Equal(1, saved.GetProperty("minimumExtinguishers").GetInt32());
        foreach (var code in new[] { "GasDetector", "CoDetector" })
        {
            var item = Item(saved, code);
            Assert.Equal("NotApplicable", item.GetProperty("status").GetString());
            Assert.Equal("NoGasNoCombustion", item.GetProperty("notApplicableReason").GetString());
        }

        Assert.Equal("Optional", Item(saved, "SmokeDetector").GetProperty("requirement").GetString());

        var completed = await CompleteAsync(client, propertyId);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(PropertyComplianceStatus.Active, (await LoadPropertyAsync(propertyId)).ComplianceStatus);
    }

    [PostgresFact]
    public async Task HomeWithGasWithoutDetectors_ActivationReturns409WithDetectorBlockers()
    {
        var (hostId, propertyId) = await SeedPropertyReadyExceptSafetyAsync();
        using var client = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");

        var saved = await SaveAsync(client, propertyId, Checklist(
            entrepreneurial: false,
            hasGasSupply: true,
            appliances: ["GasHob", "Boiler"],
            items:
            [
                new { code = "FireExtinguishers", answer = "Present", quantity = 1 },
                new { code = "BdsrDeclaration", answer = "Present" },
            ]));
        Assert.Equal(
            ["safety_gas_detector_missing", "safety_co_detector_missing"],
            saved.GetProperty("blockers").EnumerateArray().Select(b => b.GetProperty("code").GetString()));

        var response = await CompleteAsync(client, propertyId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await ReadJsonAsync(response);
        Assert.Equal("property_activation_blocked", problem.GetProperty("code").GetString());
        Assert.Equal("Pending", problem.GetProperty("complianceStatus").GetString());
        Assert.Equal(["safety"], problem.GetProperty("incompleteBlockers").EnumerateArray().Select(s => s.GetString()));
        var blockers = problem.GetProperty("blockers").EnumerateArray().ToList();
        Assert.Equal(["safety_gas_detector_missing", "safety_co_detector_missing"], blockers.Select(b => b.GetProperty("code").GetString()));
        Assert.All(blockers, b => Assert.Equal("safety", b.GetProperty("step").GetString()));
        Assert.StartsWith("Rilevatore di gas combustibili", blockers[0].GetProperty("message").GetString());
        Assert.Equal(PropertyComplianceStatus.Pending, (await LoadPropertyAsync(propertyId)).ComplianceStatus);
    }

    [PostgresFact]
    public async Task NotEntrepreneurial_CompliantSystemsNotRequired_ButRequiredForABusiness()
    {
        var (hostId, propertyId) = await SeedPropertyReadyExceptSafetyAsync();
        using var client = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");
        object[] items =
        [
            new { code = "FireExtinguishers", answer = "Present", quantity = 1 },
            new { code = "BdsrDeclaration", answer = "Present" },
        ];

        var privateRental = await SaveAsync(client, propertyId, Checklist(false, false, [], items));
        var business = await SaveAsync(client, propertyId, Checklist(true, false, [], items));

        var systems = Item(privateRental, "SystemsCompliance");
        Assert.Equal("NotApplicable", systems.GetProperty("status").GetString());
        Assert.Equal("NotEntrepreneurial", systems.GetProperty("notApplicableReason").GetString());
        Assert.True(privateRental.GetProperty("isComplete").GetBoolean());

        Assert.Equal("Required", Item(business, "SystemsCompliance").GetProperty("requirement").GetString());
        Assert.Equal(
            ["safety_systems_compliance_missing"],
            business.GetProperty("blockers").EnumerateArray().Select(b => b.GetProperty("code").GetString()));
    }

    [PostgresFact]
    public async Task SaveChecklist_InvalidValue_Returns422WithStableCode()
    {
        var (hostId, propertyId) = await SeedPropertyReadyExceptSafetyAsync();
        using var client = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");

        var response = await client.PutAsJsonAsync(
            $"/api/properties/{propertyId}/compliance/safety-checklist",
            Checklist(false, false, [], [new { code = "FireExtinguishers", answer = "Present" }]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("safety_extinguisher_quantity_required", (await ReadJsonAsync(response)).GetProperty("code").GetString());
    }

    [PostgresFact]
    public async Task SafetyChecklist_PropertyOfAnotherOrg_Returns404()
    {
        var (_, propertyId) = await SeedPropertyReadyExceptSafetyAsync();
        var (otherHost, _) = await SeedPropertyReadyExceptSafetyAsync();
        using var client = _factory.CreateAuthenticatedClient(otherHost, "PropertyOwner");

        var get = await client.GetAsync($"/api/properties/{propertyId}/compliance/safety-checklist");
        var put = await client.PutAsJsonAsync(
            $"/api/properties/{propertyId}/compliance/safety-checklist",
            Checklist(false, false, [], []));

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.PropertySafetyChecklists.IgnoreQueryFilters().AnyAsync(c => c.PropertyId == propertyId));
    }

    private static object Checklist(bool entrepreneurial, bool hasGasSupply, string[] appliances, object[] items) => new
    {
        facts = new
        {
            entrepreneurial,
            hasGasSupply,
            combustionAppliances = appliances,
            floorCount = 1,
            floorAreasSqm = new[] { 75.5m },
        },
        items,
        confirm = true,
    };

    private static Task<HttpResponseMessage> CompleteAsync(HttpClient client, Guid propertyId) =>
        client.PostAsJsonAsync($"/api/properties/{propertyId}/compliance/activation/complete", new { tosAccepted = true });

    private static async Task<JsonElement> SaveAsync(HttpClient client, Guid propertyId, object body)
    {
        var response = await client.PutAsJsonAsync($"/api/properties/{propertyId}/compliance/safety-checklist", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private static JsonElement Item(JsonElement checklist, string code) =>
        checklist.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("code").GetString() == code);

    private static List<string?> BlockerCodes(JsonElement problem) =>
        problem.GetProperty("blockers").EnumerateArray().Select(b => b.GetProperty("code").GetString()).ToList();

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<Property> LoadPropertyAsync(Guid propertyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Test scope without an authenticated org: read the row directly to check what the API stored.
        return await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == propertyId);
    }

    /// <summary>Base data, valid CIN and required documents in place: only the safety checklist and the ToS are left.</summary>
    private async Task<(string HostId, Guid PropertyId)> SeedPropertyReadyExceptSafetyAsync()
    {
        var hostId = $"auth0|safety-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(hostId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = new Property
        {
            OwnerId = hostId,
            OrgId = org.Id,
            Name = "Casa elettrica",
            Description = "Test",
            Address = $"Via Test {Guid.NewGuid():N}",
            City = "Seveso",
            PostalCode = "20822",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 120m,
            CinCode = "IT058091C27G5FFZDZ",
            IsActive = true,
            ComplianceStatus = PropertyComplianceStatus.Pending,
        };
        db.Properties.Add(property);
        db.PropertyDocuments.Add(new PropertyDocument
        {
            PropertyId = property.Id,
            OrgId = org.Id,
            FileName = "cin.pdf",
            StorageUrl = "documents/cin.pdf",
            DocumentType = DocumentType.CinCertificate,
            UploadedBy = hostId,
        });
        await db.SaveChangesAsync();
        return (hostId, property.Id);
    }
}
