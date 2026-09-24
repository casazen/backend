using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// FD-21 / D11 (A8-01, A8-16 AI part): AI supplier discovery is behind <c>Features:AiSupplierDiscovery</c>, off by
/// default. <c>match-supplier</c> answers 404 like a missing route and nothing reaches the AI provider or the web search.
/// </summary>
public class AiSupplierDiscoveryFeatureFlagTests(AiProviderMockFactory factory) : IClassFixture<AiProviderMockFactory>
{
    [Fact]
    public async Task MatchSupplier_FlagOffAsPropertyOwner_Returns404AndDoesNotCallProvider()
    {
        var property = await factory.SeedPropertyAsync();
        using var client = factory.CreateAuthenticatedClient();
        factory.AiProviderMock.Invocations.Clear();
        factory.WebSearchMock.Invocations.Clear();

        var response = await client.PostAsJsonAsync(
            "/api/service-requests/match-supplier",
            new { propertyId = property.Id, category = "cleaning", urgency = "Normal", notes = "Ospite Mario Rossi" });

        await OtaPartnerApiFeatureFlagTests.AssertNotFoundProblemAsync(response);
        factory.AiProviderMock.Verify(
            p => p.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        factory.WebSearchMock.Verify(w => w.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MatchSupplier_FlagOffAnonymous_Returns404NotUnauthorized()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/service-requests/match-supplier",
            new { propertyId = Guid.NewGuid(), category = "cleaning" });

        await OtaPartnerApiFeatureFlagTests.AssertNotFoundProblemAsync(response);
    }

    [Fact]
    public async Task PublicFeatures_Default_ReturnsAiSupplierDiscoveryOff()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/public/features");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("aiSupplierDiscovery").GetBoolean());
    }

    [Fact]
    public async Task Subprocessors_StubProvider_DoesNotListAnAiProvider()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/legal/subprocessors");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("DeepSeek", body, StringComparison.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("2026-06-v1", json.RootElement.GetProperty("version").GetString());
    }
}

/// <summary>
/// FD-21 with <c>Features:AiSupplierDiscovery</c> on: the AI match exists again, with the guards of A8-01 / A8-15
/// (category allowlist, per-user rate limit, no host notes in the prompt, budget error surfaced as 422).
/// </summary>
public class AiSupplierDiscoveryFlagOnTests(AiSupplierDiscoveryEnabledFactory factory)
    : IClassFixture<AiSupplierDiscoveryEnabledFactory>
{
    private const string MatchUrl = "/api/service-requests/match-supplier";

    [Fact]
    public async Task MatchSupplier_FlagOnWithHostNotes_PromptContainsNoNotesNorSupplierName()
    {
        var owner = $"auth0|fd21-notes-{Guid.NewGuid():N}";
        var property = await factory.SeedPropertyAsync(owner);
        var supplierName = await SeedActiveSupplierAsync(property.City, ServiceCategories.Cleaning);
        using var client = factory.CreateAuthenticatedClient(owner);
        lock (factory.Prompts)
            factory.Prompts.Clear();

        var response = await client.PostAsJsonAsync(MatchUrl, new
        {
            propertyId = property.Id,
            category = ServiceCategories.Cleaning,
            urgency = "High",
            notes = "Ospite Mario Rossi arriva alle 15, chiamare 333 1234567",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string[] prompts;
        lock (factory.Prompts)
            prompts = [.. factory.Prompts];
        var prompt = Assert.Single(prompts);
        Assert.DoesNotContain("Mario Rossi", prompt);
        Assert.DoesNotContain("333", prompt);
        Assert.DoesNotContain(supplierName, prompt);
        Assert.Contains(ServiceCategories.Cleaning, prompt);
    }

    [Fact]
    public async Task MatchSupplier_FlagOnUnknownCategory_Returns400AndDoesNotCallProvider()
    {
        var owner = $"auth0|fd21-category-{Guid.NewGuid():N}";
        var property = await factory.SeedPropertyAsync(owner);
        using var client = factory.CreateAuthenticatedClient(owner);
        factory.WebSearchMock.Invocations.Clear();

        var response = await client.PostAsJsonAsync(MatchUrl, new { propertyId = property.Id, category = Guid.NewGuid().ToString() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("validation_error", body.RootElement.GetProperty("code").GetString());
        factory.WebSearchMock.Verify(w => w.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MatchSupplier_OverPerUserLimit_Returns429BeforeTheProvider()
    {
        var owner = $"auth0|fd21-rate-{Guid.NewGuid():N}";
        var property = await factory.SeedPropertyAsync(owner);
        using var client = factory.CreateAuthenticatedClient(owner);
        factory.WebSearchMock
            .Setup(w => w.SearchAsync(It.Is<string>(q => q.Contains("idraulico")), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        var body = new { propertyId = property.Id, category = ServiceCategories.Plumbing };

        for (var i = 0; i < AiSupplierDiscoveryEnabledFactory.UserPermitLimit; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(MatchUrl, body)).StatusCode);

        factory.WebSearchMock.Invocations.Clear();
        var rejected = await client.PostAsJsonAsync(MatchUrl, body);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.Contains("Retry-After"));
        using var problem = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.Equal("rate_limited", problem.RootElement.GetProperty("code").GetString());
        factory.WebSearchMock.Verify(w => w.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MatchSupplier_BudgetExhaustedOnDiscovery_Returns422WithStableCode()
    {
        var owner = $"auth0|fd21-budget-{Guid.NewGuid():N}";
        var property = await factory.SeedPropertyAsync(owner);
        using var client = factory.CreateAuthenticatedClient(owner);
        factory.WebSearchMock
            .Setup(w => w.SearchAsync(It.Is<string>(q => q.Contains("lavanderia")), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiBudgetExceededException());

        var response = await client.PostAsJsonAsync(MatchUrl, new { propertyId = property.Id, category = ServiceCategories.Laundry });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ai_budget_exhausted", body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task PublicFeatures_FlagOn_ReturnsAiSupplierDiscoveryOn()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/public/features");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("aiSupplierDiscovery").GetBoolean());
    }

    private async Task<string> SeedActiveSupplierAsync(string comune, string category)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var supplierOrg = new OrgEntity
        {
            Name = $"Supplier {suffix}",
            Slug = $"fd21-supplier-{suffix}",
            DisplayName = $"Supplier {suffix}",
            ContactEmail = $"supplier-{suffix}@example.com",
            OrgType = OrgType.Supplier,
            IsActive = true,
        };
        db.Orgs.Add(supplierOrg);
        var legalName = $"Pulizie Bianchi {suffix}";
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = supplierOrg.Id,
            Status = SupplierStatus.Active,
            LegalName = legalName,
            Phone = "+39 06 000000",
            Email = $"supplier-{suffix}@example.com",
            CategoriesJson = $"[\"{category}\"]",
            ComuniJson = $"[\"{comune}\"]",
        });
        await db.SaveChangesAsync();
        return legalName;
    }
}
