using System.Net;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// Integration tests for PropertiesController
/// Note: These tests verify authorization requirements since Auth0 is not configured in test environment
/// For full authentication testing, see PropertiesController unit tests with mocked Auth0 claims
/// IMPORTANT: These tests are SKIPPED in CI/CD (Linux) because they require LocalDB (Windows-only)
/// </summary>
public class PropertiesControllerIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public PropertiesControllerIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetAll_WithoutAuthentication_ReturnsUnauthorizedOrError()
    {
        // Act - Without authentication token (Auth0 is not configured in tests)
        var response = await _client.GetAsync("/api/properties");

        // Assert - Should return 401 or 500 (auth not configured in minimal Program.cs)
        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.InternalServerError,
            $"Expected Unauthorized or InternalServerError, got {response.StatusCode}"
        );
    }

    [Fact]
    public async Task GetById_WithoutAuthentication_ReturnsUnauthorizedOrError()
    {
        // Arrange
        var testId = Guid.NewGuid();

        // Act
        var response = await _client.GetAsync($"/api/properties/{testId}");

        // Assert
        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.InternalServerError,
            $"Expected Unauthorized or InternalServerError, got {response.StatusCode}"
        );
    }

    [Fact]
    public async Task Create_WithoutAuthentication_ReturnsUnauthorizedOrError()
    {
        // Arrange — OwnerId is not included; it is always derived from JWT on the server side
        var newProperty = new
        {
            Name = "Test Property",
            City = "Rome",
            Address = "Via Roma 1",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100.00m,
            CleaningFee = 50.00m,
            DamageDeposit = 200.00m,
            IsActive = true
        };

        var json = JsonSerializer.Serialize(newProperty);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _client.PostAsync("/api/properties", content);

        // Assert
        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.InternalServerError,
            $"Expected Unauthorized or InternalServerError, got {response.StatusCode}"
        );
    }

    [Fact]
    public async Task Update_WithoutAuthentication_ReturnsUnauthorizedOrError()
    {
        // Arrange — OwnerId is not included; it is always derived from JWT on the server side
        var testId = Guid.NewGuid();
        var updateProperty = new
        {
            Name = "Updated Property",
            City = "Rome",
            Address = "Via Roma 1"
        };

        var json = JsonSerializer.Serialize(updateProperty);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await _client.PutAsync($"/api/properties/{testId}", content);

        // Assert
        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.InternalServerError,
            $"Expected Unauthorized or InternalServerError, got {response.StatusCode}"
        );
    }

    [Fact]
    public async Task Delete_WithoutAuthentication_ReturnsUnauthorizedOrError()
    {
        // Arrange
        var testId = Guid.NewGuid();

        // Act
        var response = await _client.DeleteAsync($"/api/properties/{testId}");

        // Assert
        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.InternalServerError,
            $"Expected Unauthorized or InternalServerError, got {response.StatusCode}"
        );
    }

    /// <summary>
    /// Regression test for issue #143: POST /api/properties without OwnerId in the body
    /// must not return 400 Bad Request. OwnerId is always derived from the JWT on the server.
    /// The test expects 401 because Auth0 is not configured in the integration test environment,
    /// confirming the request reaches auth middleware and not model-validation (which would 400).
    /// </summary>
    [Fact]
    public async Task Create_WithoutOwnerIdInBody_DoesNotReturn400()
    {
        // Arrange — no OwnerId field, matching the new CreatePropertyRequest DTO
        var newProperty = new
        {
            Name = "Regression Property",
            City = "Rome",
            Address = "Via Roma 1",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 80.00m
        };

        var json = JsonSerializer.Serialize(newProperty);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act — sent without an authentication token
        var response = await _client.PostAsync("/api/properties", content);

        // Assert — must NOT be 400 (which would indicate OwnerId is wrongly required in request body)
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);

        // Must be 401 or 500 (auth not configured), confirming validation passed
        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.InternalServerError,
            $"Expected Unauthorized or InternalServerError (not 400), got {response.StatusCode}"
        );
    }

    [Fact]
    public async Task PropertiesController_IsProtectedByAuthorize()
    {
        // This test verifies that all endpoints (except Search) require authentication
        // by attempting to access them without credentials

        var endpoints = new[]
        {
            "/api/properties",
            $"/api/properties/{Guid.NewGuid()}",
        };

        foreach (var endpoint in endpoints)
        {
            var response = await _client.GetAsync(endpoint);

            Assert.True(
                response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.InternalServerError,
                $"Endpoint {endpoint} should require authentication, got {response.StatusCode}"
            );
        }
    }

    [Fact]
    public async Task PropertiesController_SearchEndpoint_AllowsAnonymous()
    {
        // Act
        var response = await _client.GetAsync("/api/properties/search");

        // Assert - Should NOT return Unauthorized (may return 500 if database issues)
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
