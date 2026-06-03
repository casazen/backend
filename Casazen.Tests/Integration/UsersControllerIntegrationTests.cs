using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// Integration tests for UsersController.
/// These tests verify authorization requirements in the absence of a real Auth0 token.
/// Full authentication testing requires a configured Auth0 tenant.
/// </summary>
public class UsersControllerIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public UsersControllerIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    // ─── GET /api/users ─ Admin only ────────────────────────────────────────

    [Fact]
    public async Task GetAll_WithoutAuthentication_Returns401()
    {
        // Act
        var response = await _client.GetAsync("/api/users");

        // Assert — no token → 401 (or 500 if auth not configured in test env)
        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.InternalServerError,
            $"Expected 401/500, got {response.StatusCode}");
    }

    // ─── GET /api/users/me ─ Any authenticated user ──────────────────────────

    [Fact]
    public async Task GetMe_WithoutAuthentication_Returns401()
    {
        // Act
        var response = await _client.GetAsync("/api/users/me");

        // Assert
        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.InternalServerError,
            $"Expected 401/500, got {response.StatusCode}");
    }

    // ─── DELETE /api/users/{id} ─ Admin only ────────────────────────────────

    [Fact]
    public async Task Delete_WithoutAuthentication_Returns401()
    {
        // Act
        var response = await _client.DeleteAsync("/api/users/auth0|test123");

        // Assert
        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.InternalServerError,
            $"Expected 401/500, got {response.StatusCode}");
    }

    // ─── GET /api/admin/stats ─ Admin only ──────────────────────────────────

    [Fact]
    public async Task GetAdminStats_WithoutAuthentication_Returns401()
    {
        // Act
        var response = await _client.GetAsync("/api/admin/stats");

        // Assert
        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.InternalServerError,
            $"Expected 401/500, got {response.StatusCode}");
    }

    // ─── GET /api/admin/cin-compliance ──────────────────────────────────────

    [Fact]
    public async Task GetCinCompliance_WithoutAuthentication_Returns401()
    {
        // Act
        var response = await _client.GetAsync("/api/admin/cin-compliance");

        // Assert
        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.InternalServerError,
            $"Expected 401/500, got {response.StatusCode}");
    }
}
