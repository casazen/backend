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
/// Tourist tax step of the activation wizard on PostgreSQL (A5-06, A8-28): a warning that never blocks the activation,
/// the rate of the comune when CasaZen has one (seeded from RS-7) and the public page of the comune when it exists.
/// </summary>
public class ComplianceActivationTouristTaxIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public ComplianceActivationTouristTaxIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task GetActivation_ComuneWithoutRate_ReturnsNonBlockingWarningInItalian()
    {
        var (hostId, propertyId) = await SeedReadyPropertyAsync("Seveso");
        using var client = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");

        var step = await GetTouristTaxStepAsync(client, propertyId);

        Assert.Equal("warning", step.GetProperty("status").GetString());
        Assert.False(step.GetProperty("blocker").GetBoolean());
        Assert.StartsWith("Il comune di Seveso non ha ancora una tariffa", step.GetProperty("message").GetString());
        var tax = step.GetProperty("touristTax");
        Assert.Equal("Seveso", tax.GetProperty("city").GetString());
        Assert.Equal(JsonValueKind.Null, tax.GetProperty("rate").ValueKind);
        Assert.Equal(JsonValueKind.Null, tax.GetProperty("publicPageSlug").ValueKind);
    }

    [PostgresFact]
    public async Task GetActivation_ComuneWithoutRateInEnglish_ReturnsEnglishWarning()
    {
        var (hostId, propertyId) = await SeedReadyPropertyAsync("Seveso");
        using var client = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");

        var step = await GetTouristTaxStepAsync(client, propertyId);

        Assert.StartsWith("The municipality of Seveso has no tourist tax rate", step.GetProperty("message").GetString());
    }

    [PostgresFact]
    public async Task CompleteActivation_ComuneWithoutRate_Returns200AndActivatesProperty()
    {
        var (hostId, propertyId) = await SeedReadyPropertyAsync("Cesano Maderno");
        using var client = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");

        var response = await client.PostAsJsonAsync($"/api/properties/{propertyId}/compliance/activation/complete", new
        {
            safetyChecklist = new { smokeDetector = true, fireExtinguisher = true, gasCompliance = true },
            tosAccepted = true,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Active", body.GetProperty("complianceStatus").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Test scope without an authenticated org: read the row directly to check what the API stored.
        var property = await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == propertyId);
        Assert.Equal(PropertyComplianceStatus.Active, property.ComplianceStatus);
    }

    [PostgresFact]
    public async Task GetActivation_SeededComune_ReturnsCompleteStepWithRateAndSource()
    {
        var (hostId, propertyId) = await SeedReadyPropertyAsync("milano");
        using var client = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");

        var step = await GetTouristTaxStepAsync(client, propertyId);

        Assert.Equal("complete", step.GetProperty("status").GetString());
        Assert.False(step.GetProperty("blocker").GetBoolean());
        Assert.Equal(JsonValueKind.Null, step.GetProperty("message").ValueKind);
        var rate = step.GetProperty("touristTax").GetProperty("rate");
        Assert.Equal(9.50m, rate.GetProperty("ratePerPersonPerNight").GetDecimal());
        Assert.Equal(14, rate.GetProperty("maxNights").GetInt32());
        Assert.Equal(18, rate.GetProperty("minimumAge").GetInt32());
        Assert.StartsWith("https://www.comune.milano.it/", rate.GetProperty("sourceUrl").GetString());
        Assert.Equal("Official", rate.GetProperty("verificationLevel").GetString());
    }

    [PostgresFact]
    public async Task GetActivation_ReviewedPublicPage_ReturnsItsSlugAndIgnoresDrafts()
    {
        await SeedTouristTaxPageAsync("082053", "SIC", "tassa-soggiorno/palermo", LegalReviewStatus.Reviewed);
        await SeedTouristTaxPageAsync("013040", "LOM", "tassa-soggiorno/bellagio", LegalReviewStatus.Draft);
        var (hostId, palermoId) = await SeedReadyPropertyAsync("Palermo");
        var (_, bellagioId) = await SeedReadyPropertyAsync("Bellagio", hostId);
        using var client = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");

        var palermo = await GetTouristTaxStepAsync(client, palermoId);
        var bellagio = await GetTouristTaxStepAsync(client, bellagioId);

        Assert.Equal("warning", palermo.GetProperty("status").GetString());
        Assert.Equal("palermo", palermo.GetProperty("touristTax").GetProperty("publicPageSlug").GetString());
        Assert.Equal(JsonValueKind.Null, bellagio.GetProperty("touristTax").GetProperty("publicPageSlug").ValueKind);
    }

    [PostgresFact]
    public async Task GetActivation_CinStep_LinksTheConfiguredGuidance()
    {
        var (hostId, propertyId) = await SeedReadyPropertyAsync("Seveso");
        using var client = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");

        var steps = await GetStepsAsync(client, propertyId);

        var cin = steps.Single(s => s.GetProperty("id").GetString() == "cin");
        Assert.Equal("https://www.bdsr.it/cin", cin.GetProperty("linkUrl").GetString());
    }

    private static async Task<JsonElement> GetTouristTaxStepAsync(HttpClient client, Guid propertyId) =>
        (await GetStepsAsync(client, propertyId)).Single(s => s.GetProperty("id").GetString() == "tourist-tax");

    private static async Task<List<JsonElement>> GetStepsAsync(HttpClient client, Guid propertyId)
    {
        var response = await client.GetAsync($"/api/properties/{propertyId}/compliance/activation");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        return body.GetProperty("steps").EnumerateArray().ToList();
    }

    /// <summary>A property where every blocking step is satisfied except the ToS, given at completion.</summary>
    private async Task<(string HostId, Guid PropertyId)> SeedReadyPropertyAsync(string city, string? hostId = null)
    {
        hostId ??= $"auth0|tax-step-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(hostId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var property = new Property
        {
            OwnerId = hostId,
            OrgId = org.Id,
            Name = $"Casa {city}",
            Description = "Test",
            Address = $"Via Test {Guid.NewGuid():N}",
            City = city,
            PostalCode = "20100",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 120m,
            CinCode = "IT058091C27G5FFZDZ",
            IsActive = true,
            ComplianceStatus = PropertyComplianceStatus.Pending,
        };
        db.Properties.Add(property);
        db.PropertyDocuments.AddRange(
            new PropertyDocument
            {
                PropertyId = property.Id,
                OrgId = org.Id,
                FileName = "cin.pdf",
                StorageUrl = "documents/cin.pdf",
                DocumentType = DocumentType.CinCertificate,
                UploadedBy = hostId,
            },
            new PropertyDocument
            {
                PropertyId = property.Id,
                OrgId = org.Id,
                FileName = "safety.pdf",
                StorageUrl = "documents/safety.pdf",
                DocumentType = DocumentType.SafetyCompliance,
                UploadedBy = hostId,
            });
        await db.SaveChangesAsync();
        return (hostId, property.Id);
    }

    private async Task SeedTouristTaxPageAsync(string comuneCode, string regionCode, string slug, LegalReviewStatus status)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SeoContentPages.Add(new SeoContentPage
        {
            Slug = slug,
            ComuneCode = comuneCode,
            RegionCode = regionCode,
            PageType = SeoPageType.TouristTaxCalc,
            Title = $"Tassa di soggiorno {slug}",
            MetaDescription = "Test",
            LegalReviewStatus = status,
            PublishedAt = status == LegalReviewStatus.Reviewed ? DateTime.UtcNow : null,
        });
        await db.SaveChangesAsync();
    }
}
