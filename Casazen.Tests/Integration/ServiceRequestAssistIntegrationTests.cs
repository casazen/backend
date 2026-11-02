using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// The default integration factory with an AI provider in effect (a mock answers) and a per-user AI limit of 3 an hour: «Descrivi di cosa hai
/// bisogno» of the guided request (WIZ-AI-REQUEST) goes through the real pipeline (authentication, permission, property of the org, rate limit).
/// </summary>
public sealed class RequestAssistFactory : AiProviderMockFactory
{
    public const int UserPermitLimit = 3;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Provider"] = "DeepSeek",
                ["Ai:ApiKey"] = "test-key",
                ["RateLimiting:AiPerUser:PermitLimit"] = UserPermitLimit.ToString(),
                ["RateLimiting:AiPerOrg:PermitLimit"] = "1000",
            }));
    }
}

public class ServiceRequestAssistIntegrationTests(RequestAssistFactory factory) : IClassFixture<RequestAssistFactory>
{
    private const string AssistUrl = "/api/service-requests/assist";
    private const string Words = "Il rubinetto della cucina perde acqua, serve qualcuno domani mattina";

    private const string GoodAnswer =
        """{"category":"plumbing","urgency":"High","summary":"Il rubinetto della cucina perde acqua: serve un intervento domani mattina.","reason":"Una perdita è un lavoro da idraulico."}""";

    private void Answer(string content)
    {
        factory.AiProviderMock
            .Setup(p => p.GenerateAsync(It.IsAny<AiRequest>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<AiRequest, AiModelTier, string, CancellationToken>((request, _, _, _) =>
            {
                lock (factory.Prompts)
                    factory.Prompts.Add(request.ToTextPrompt());
            })
            .ReturnsAsync(new AiGenerationResult(content, 200, 60, AiModelTier.Economy, false));
    }

    [Fact]
    public async Task Availability_Anonymous_IsUnauthorized()
    {
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(AssistUrl)).StatusCode);
    }

    [Fact]
    public async Task Assist_Anonymous_IsUnauthorized()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(AssistUrl, new { propertyId = Guid.NewGuid(), text = Words });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Availability_AHostIsToldTheAiIsThere_WithoutACallToTheProvider()
    {
        var owner = $"auth0|assist-available-{Guid.NewGuid():N}";
        await factory.SeedPropertyAsync(owner);
        using var client = factory.CreateAuthenticatedClient(owner);
        factory.AiProviderMock.Invocations.Clear();

        var response = await client.GetAsync(AssistUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("available").GetBoolean());
        factory.AiProviderMock.Verify(
            p => p.GenerateAsync(It.IsAny<AiRequest>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Assist_TheOwner_GetsTheSuggestion_AndOnlyTheirWordsReachTheProvider()
    {
        var owner = $"auth0|assist-owner-{Guid.NewGuid():N}";
        var property = await factory.SeedPropertyAsync(owner);
        Answer(GoodAnswer);
        using var client = factory.CreateAuthenticatedClient(owner);
        lock (factory.Prompts)
            factory.Prompts.Clear();

        var response = await client.PostAsJsonAsync(
            AssistUrl,
            new { propertyId = property.Id, text = $"{Words}. Chiamare il 338 123 4567, la casa è in {property.Address}" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("aiUsed").GetBoolean());
        Assert.Equal("plumbing", body.RootElement.GetProperty("category").GetString());
        Assert.Equal("High", body.RootElement.GetProperty("urgency").GetString());
        Assert.Contains("rubinetto", body.RootElement.GetProperty("summary").GetString());
        // The property of the test has no comune of the official list: nobody of CasaZen is proposed.
        Assert.Equal("external", body.RootElement.GetProperty("channel").GetString());

        string[] prompts;
        lock (factory.Prompts)
            prompts = [.. factory.Prompts];
        var prompt = Assert.Single(prompts);
        Assert.Contains("rubinetto della cucina", prompt);
        Assert.DoesNotContain("338", prompt);
        Assert.DoesNotContain("Via Test", prompt);
        Assert.DoesNotContain(property.Name, prompt);
        Assert.DoesNotContain(property.City, prompt);
    }

    [Fact]
    public async Task Assist_WhenASupplierCoversTheComuneOfTheProperty_CasazenIsProposed()
    {
        var owner = $"auth0|assist-casazen-{Guid.NewGuid():N}";
        var property = await factory.SeedPropertyAsync(owner);
        await SetComuneAndAddSupplierAsync(property.Id, "058091", property.City, ServiceCategories.Plumbing);
        Answer(GoodAnswer);
        using var client = factory.CreateAuthenticatedClient(owner);

        var response = await client.PostAsJsonAsync(AssistUrl, new { propertyId = property.Id, text = Words });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("casazen", body.RootElement.GetProperty("channel").GetString());
        Assert.True(body.RootElement.GetProperty("casazenSuppliers").GetInt32() >= 1);
    }

    [Fact]
    public async Task Assist_AnAnswerThatIsNotAService_IsStillA200WithTheReason()
    {
        var owner = $"auth0|assist-invalid-{Guid.NewGuid():N}";
        var property = await factory.SeedPropertyAsync(owner);
        Answer("""{"category":"piscina","urgency":"Normal"}""");
        using var client = factory.CreateAuthenticatedClient(owner);

        var response = await client.PostAsJsonAsync(AssistUrl, new { propertyId = property.Id, text = Words });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("aiUsed").GetBoolean());
        Assert.Equal("ai_invalid_answer", body.RootElement.GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("perde")]
    public async Task Assist_ADescriptionTooShort_IsAValidationError_AndTheProviderIsNotCalled(string text)
    {
        var owner = $"auth0|assist-short-{Guid.NewGuid():N}";
        var property = await factory.SeedPropertyAsync(owner);
        using var client = factory.CreateAuthenticatedClient(owner);
        factory.AiProviderMock.Invocations.Clear();

        var response = await client.PostAsJsonAsync(AssistUrl, new { propertyId = property.Id, text });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("validation_error", body.RootElement.GetProperty("code").GetString());
        factory.AiProviderMock.Verify(
            p => p.GenerateAsync(It.IsAny<AiRequest>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Assist_ADescriptionTooLong_IsAValidationError()
    {
        var owner = $"auth0|assist-long-{Guid.NewGuid():N}";
        var property = await factory.SeedPropertyAsync(owner);
        using var client = factory.CreateAuthenticatedClient(owner);

        var response = await client.PostAsJsonAsync(
            AssistUrl,
            new { propertyId = property.Id, text = new string('a', ServiceRequestAssistLimits.MaxTextLength + 1) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Assist_APropertyOfAnotherOrg_IsNotFound()
    {
        var owner = $"auth0|assist-mine-{Guid.NewGuid():N}";
        var stranger = $"auth0|assist-other-{Guid.NewGuid():N}";
        await factory.SeedPropertyAsync(owner);
        var theirs = await factory.SeedPropertyAsync(stranger);
        using var client = factory.CreateAuthenticatedClient(owner);

        var response = await client.PostAsJsonAsync(AssistUrl, new { propertyId = theirs.Id, text = Words });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Assist_OverThePerUserLimit_Is429BeforeTheProvider()
    {
        var owner = $"auth0|assist-rate-{Guid.NewGuid():N}";
        var property = await factory.SeedPropertyAsync(owner);
        Answer(GoodAnswer);
        using var client = factory.CreateAuthenticatedClient(owner);

        for (var i = 0; i < RequestAssistFactory.UserPermitLimit; i++)
        {
            var allowed = await client.PostAsJsonAsync(AssistUrl, new { propertyId = property.Id, text = $"{Words} {i}" });
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        factory.AiProviderMock.Invocations.Clear();
        var rejected = await client.PostAsJsonAsync(AssistUrl, new { propertyId = property.Id, text = $"{Words} altro" });

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.Contains("Retry-After"));
        using var problem = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.Equal("rate_limited", problem.RootElement.GetProperty("code").GetString());
        factory.AiProviderMock.Verify(
            p => p.GenerateAsync(It.IsAny<AiRequest>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Reading whether the AI is there costs no permit: it still answers after the limit.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(AssistUrl)).StatusCode);
    }

    private async Task SetComuneAndAddSupplierAsync(Guid propertyId, string istatCode, string city, string category)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var property = db.Properties.IgnoreQueryFilters().First(p => p.Id == propertyId);
        property.ComuneIstatCode = istatCode;

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var supplierOrg = new OrgEntity
        {
            Name = $"Supplier {suffix}",
            Slug = $"assist-supplier-{suffix}",
            DisplayName = $"Supplier {suffix}",
            ContactEmail = $"supplier-{suffix}@example.com",
            OrgType = OrgType.Supplier,
            IsActive = true,
        };
        db.Orgs.Add(supplierOrg);
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = supplierOrg.Id,
            Status = SupplierStatus.Active,
            LegalName = $"Idraulica Bianchi {suffix}",
            Phone = "+39 06 000000",
            Email = $"supplier-{suffix}@example.com",
            CategoriesJson = $"[\"{category}\"]",
            ComuneIstatCodesJson = $"[\"{istatCode}\"]",
            ComuniJson = $"[\"{city}\"]",
        });
        await db.SaveChangesAsync();
    }
}
