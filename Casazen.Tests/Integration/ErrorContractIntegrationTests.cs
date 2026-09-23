using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// The API's single error shape (FD-05): ProblemDetails with a stable <c>code</c>, a localized <c>detail</c>
/// (Italian by default, English with <c>Accept-Language: en</c>) and a <c>traceId</c>.
/// </summary>
public class ErrorContractIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public ErrorContractIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_WithoutToken_Returns401UnauthorizedProblemInItalian()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/leases");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await ReadJsonAsync(response);
        Assert.Equal("unauthorized", problem.GetProperty("code").GetString());
        Assert.StartsWith("È richiesta l'autenticazione", problem.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task Get_WithoutTokenAndEnglishAcceptLanguage_Returns401ProblemInEnglish()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");

        var response = await client.GetAsync("/api/leases");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await ReadJsonAsync(response);
        Assert.Equal("unauthorized", problem.GetProperty("code").GetString());
        Assert.StartsWith("Authentication is required", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Get_AuthenticatedWithoutRequiredRole_Returns403ForbiddenProblem()
    {
        var owner = $"auth0|error-contract-forbidden-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(owner);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var response = await client.GetAsync("/api/leases");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await ReadJsonAsync(response);
        Assert.Equal("forbidden", problem.GetProperty("code").GetString());
        Assert.Equal("Non si dispone dei permessi per accedere a questa risorsa.", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Get_UnknownRoute_Returns404NotFoundProblem()
    {
        using var client = _factory.CreateAuthenticatedClient();

        var response = await client.GetAsync($"/api/does-not-exist-{Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await ReadJsonAsync(response);
        Assert.Equal("not_found", problem.GetProperty("code").GetString());
        Assert.Equal("La risorsa richiesta non esiste o non è accessibile.", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task UpdateProperty_InvalidBody_Returns400ValidationProblemWithFieldErrors()
    {
        var owner = $"auth0|error-contract-validation-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");

        var response = await client.PutAsJsonAsync($"/api/properties/{property.Id}", UpdateBody(name: string.Empty, slug: null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await ReadJsonAsync(response);
        Assert.Equal("validation_error", problem.GetProperty("code").GetString());
        Assert.Equal("Uno o più errori di validazione.", problem.GetProperty("title").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("Name", out _));
        Assert.False(problem.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String);
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task UpdateProperty_SlugUsedByAnotherPropertyOfOrg_Returns409DuplicatePropertySlug()
    {
        var owner = $"auth0|error-contract-slug-{Guid.NewGuid():N}";
        var first = await _factory.SeedPropertyAsync(owner);
        var second = await _factory.SeedPropertyAsync(owner);
        var slug = $"villa-{Guid.NewGuid():N}"[..20];
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await db.Properties.FindAsync(first.Id);
            stored!.Slug = slug;
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");
        var response = await client.PutAsJsonAsync($"/api/properties/{second.Id}", UpdateBody("Seconda villa", slug));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ReadJsonAsync(response);
        Assert.Equal("duplicate_property_slug", problem.GetProperty("code").GetString());
        Assert.Equal(
            "Questo indirizzo web (slug) è già usato da un altro immobile della tua organizzazione.",
            problem.GetProperty("detail").GetString());
        Assert.DoesNotContain("Slug already in use", await response.Content.ReadAsStringAsync());
    }

    private static object UpdateBody(string name, string? slug) => new
    {
        name,
        description = "Integration test property",
        address = $"Via Errori {Guid.NewGuid():N}",
        city = "Rome",
        postalCode = "00100",
        latitude = 41.9028m,
        longitude = 12.4964m,
        bedrooms = 2,
        bathrooms = 1,
        maxGuests = 4,
        nightlyRate = 100m,
        cleaningFee = 50m,
        damageDeposit = 200m,
        cinCode = (string?)null,
        slug,
    };

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(body);
    }
}
