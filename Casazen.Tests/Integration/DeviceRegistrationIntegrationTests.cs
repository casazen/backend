using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
// OrgEntity alias from Casazen.Tests.csproj

namespace Casazen.Tests.Integration;

public class DeviceRegistrationIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public DeviceRegistrationIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task RegisterDevice_AsHost_Returns201_AndUpserts()
    {
        var userId = $"auth0|device-{Guid.NewGuid():N}";
        await SeedHostAsync(userId);

        using var client = _factory.CreateAuthenticatedClient(userId, "PropertyOwner");

        var body = new
        {
            platform = "ios",
            pushToken = "ExponentPushToken[test-token-1]",
            deviceId = "device-abc",
        };

        var response = await client.PostAsJsonAsync("/api/devices", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ios", dto.GetProperty("platform").GetString());
        Assert.Equal("device-abc", dto.GetProperty("deviceId").GetString());

        var updated = await client.PostAsJsonAsync("/api/devices", new
        {
            platform = "ios",
            pushToken = "ExponentPushToken[test-token-2]",
            deviceId = "device-abc",
        });
        Assert.Equal(HttpStatusCode.Created, updated.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = await db.DeviceRegistrations.CountAsync(d => d.UserId == userId);
        Assert.Equal(1, count);

        var token = await db.DeviceRegistrations
            .Where(d => d.UserId == userId)
            .Select(d => d.PushToken)
            .SingleAsync();
        Assert.Equal("ExponentPushToken[test-token-2]", token);
    }

    [Fact]
    public async Task UnregisterDevice_Returns204()
    {
        var userId = $"auth0|device-{Guid.NewGuid():N}";
        await SeedHostAsync(userId);

        using var client = _factory.CreateAuthenticatedClient(userId, "PropertyOwner");
        await client.PostAsJsonAsync("/api/devices", new
        {
            platform = "android",
            pushToken = "ExponentPushToken[remove-me]",
            deviceId = "device-remove",
        });

        var response = await client.DeleteAsync("/api/devices/device-remove");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.DeviceRegistrations.AnyAsync(d => d.UserId == userId));
    }

    [Fact]
    public async Task RegisterDevice_WhenPushTokenMovesToAnotherUser_RemovesStaleRegistration()
    {
        var previousUserId = $"auth0|device-prev-{Guid.NewGuid():N}";
        var currentUserId = $"auth0|device-current-{Guid.NewGuid():N}";
        await SeedHostAsync(previousUserId);
        await SeedHostAsync(currentUserId);

        const string reusedPushToken = "ExponentPushToken[shared-device]";

        using (var previousClient = _factory.CreateAuthenticatedClient(previousUserId, "PropertyOwner"))
        {
            var previousResponse = await previousClient.PostAsJsonAsync("/api/devices", new
            {
                platform = "ios",
                pushToken = reusedPushToken,
                deviceId = "previous-installation",
            });
            Assert.Equal(HttpStatusCode.Created, previousResponse.StatusCode);
        }

        using (var currentClient = _factory.CreateAuthenticatedClient(currentUserId, "PropertyOwner"))
        {
            var currentResponse = await currentClient.PostAsJsonAsync("/api/devices", new
            {
                platform = "ios",
                pushToken = reusedPushToken,
                deviceId = "current-installation",
            });
            Assert.Equal(HttpStatusCode.Created, currentResponse.StatusCode);
        }

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.DeviceRegistrations.AnyAsync(d =>
            d.UserId == previousUserId && d.PushToken == reusedPushToken));

        var registration = await db.DeviceRegistrations.SingleAsync(d => d.PushToken == reusedPushToken);
        Assert.Equal(currentUserId, registration.UserId);
        Assert.Equal("current-installation", registration.DeviceId);
    }

    [Fact]
    public async Task RegisterDevice_WithoutAuth_Returns401()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/devices", new
        {
            platform = "ios",
            pushToken = "ExponentPushToken[x]",
            deviceId = "x",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task SeedHostAsync(string userId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var org = new OrgEntity
        {
            Id = Guid.NewGuid(),
            Name = "Device Test Org",
            Slug = $"device-{Guid.NewGuid():N}".Substring(0, 20),
            ContactEmail = "host@example.com",
            IsActive = true,
        };
        db.Orgs.Add(org);

        db.Users.Add(new User
        {
            Id = userId,
            Email = $"{userId}@test.local",
            FirstName = "Host",
            LastName = "Test",
            OrgId = org.Id,
            Role = UserRole.PropertyOwner,
            IsActive = true,
        });

        await db.SaveChangesAsync();
    }
}
