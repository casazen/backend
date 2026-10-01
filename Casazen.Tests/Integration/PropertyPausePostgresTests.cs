using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// Pause / activate over HTTP on PostgreSQL (PC-03, A2-05): the dedicated <c>POST /api/properties/{id}/pause</c> and
/// <c>.../activate</c> actions hide a property from the public site and keep it in its own host's list and detail; the
/// generic update can no longer hide it with <c>isActive</c>.
/// </summary>
public class PropertyPausePostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public PropertyPausePostgresTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task Pause_PublishedProperty_HidesItFromThePublicAndKeepsItInTheHostListAndDetail()
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "pc03-pause");
        using var host = _factory.CreateAuthenticatedClient(property.OwnerId, "PropertyOwner");
        using var anonymous = _factory.CreateClient();

        var response = await host.PostAsync($"/api/properties/{property.Id}/pause", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await ReadJsonAsync(response);
        Assert.True(status.GetProperty("isPaused").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, status.GetProperty("pausedAt").ValueKind);

        var list = await ReadJsonAsync(await host.GetAsync("/api/properties"));
        var row = Assert.Single(list.EnumerateArray(), p => p.GetProperty("id").GetGuid() == property.Id);
        Assert.True(row.GetProperty("isPaused").GetBoolean());
        Assert.True(row.GetProperty("isActive").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync($"/api/properties/{property.Id}")).StatusCode);
        var detail = await host.GetAsync($"/api/properties/{property.Id}/detail");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.True((await ReadJsonAsync(detail)).GetProperty("isPaused").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/properties/{property.Id}/public")).StatusCode);

        var stored = await ReadStoredAsync(property.Id);
        Assert.True(stored.IsPaused);
        Assert.True(stored.IsActive);
    }

    [PostgresFact]
    public async Task Activate_PausedProperty_PublishesItAgain()
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "pc03-activate");
        using var host = _factory.CreateAuthenticatedClient(property.OwnerId, "PropertyOwner");
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await host.PostAsync($"/api/properties/{property.Id}/pause", content: null)).StatusCode);

        var response = await host.PostAsync($"/api/properties/{property.Id}/activate", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await ReadJsonAsync(response);
        Assert.False(status.GetProperty("isPaused").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("pausedAt").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/properties/{property.Id}/public")).StatusCode);
        var stored = await ReadStoredAsync(property.Id);
        Assert.False(stored.IsPaused);
        Assert.Null(stored.PausedAt);
    }

    [PostgresFact]
    public async Task Pause_PropertyOfAnotherOrg_Returns404AndChangesNothing()
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "pc03-victim");
        var otherOwner = $"auth0|pc03-other-{Guid.NewGuid():N}";
        await _factory.SeedPropertyAsync(otherOwner);
        using var other = _factory.CreateAuthenticatedClient(otherOwner, "PropertyOwner");

        var response = await other.PostAsync($"/api/properties/{property.Id}/pause", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False((await ReadStoredAsync(property.Id)).IsPaused);
    }

    [PostgresFact]
    public async Task Update_IsActiveFalseSent_KeepsThePropertyInTheHostList()
    {
        var property = await PublishedPropertyTests.SeedAsync(_factory, "pc03-update");
        using var host = _factory.CreateAuthenticatedClient(property.OwnerId, "PropertyOwner");

        var response = await host.PutAsJsonAsync($"/api/properties/{property.Id}", new { name = "Rinominata", isActive = false });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = await ReadStoredAsync(property.Id);
        Assert.Equal("Rinominata", stored.Name);
        Assert.True(stored.IsActive);
        Assert.False(stored.IsPaused);
        var list = await ReadJsonAsync(await host.GetAsync("/api/properties"));
        Assert.Contains(list.EnumerateArray(), p => p.GetProperty("id").GetGuid() == property.Id);
    }

    private async Task<Property> ReadStoredAsync(Guid propertyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Test assertion outside any request: no tenant context, read the row as stored.
        return await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == propertyId);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
}
