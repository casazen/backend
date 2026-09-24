using System.Net;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RichardSzalay.MockHttp;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Exercises the real Auth0 SDK client against a fake HTTP handler, so the assertions are on the
/// requests actually sent to the Management API.
/// </summary>
public class Auth0ManagementServiceTests
{
    private const string Domain = "tenant.eu.auth0.com";
    private const string ApiBase = "https://tenant.eu.auth0.com/api/v2";
    private const string DualRoleUser = "auth0|host-and-supplier";

    private static readonly object[] AllRoles =
    [
        new { id = "rol_owner", name = "PropertyOwner", description = "" },
        new { id = "rol_ltl", name = "LongTermLandlord", description = "" },
        new { id = "rol_admin", name = "Admin", description = "" },
        new { id = "rol_supplier", name = "Supplier", description = "" },
    ];

    private readonly MockHttpMessageHandler _http = new();
    private readonly List<RecordedRequest> _requests = [];
    private readonly Mock<IAuth0ManagementTokenProvider> _tokenProvider = new();

    public Auth0ManagementServiceTests()
    {
        _tokenProvider.SetupGet(p => p.IsConfigured).Returns(true);
        _tokenProvider.SetupGet(p => p.Domain).Returns(Domain);
        _tokenProvider.Setup(p => p.GetTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("mgmt-token");
    }

    [Fact]
    public async Task AssignRoleAsync_UserWithHostRole_AddsSupplierWithoutRemovingOtherRoles()
    {
        RespondToRoles(AllRoles);
        RespondToUserRoles(HttpStatusCode.NoContent);
        var service = CreateService();

        var result = await service.AssignRoleAsync(DualRoleUser, UserRole.Supplier);

        Assert.True(result.Succeeded);
        var assign = Assert.Single(_requests, r => r.Method == HttpMethod.Post);
        Assert.EndsWith("/users/auth0%7Chost-and-supplier/roles", assign.Path);
        Assert.Equal(["rol_supplier"], RoleIdsOf(assign));
        // The destructive pattern (read the user's roles, delete them all) must be gone.
        Assert.DoesNotContain(_requests, r => r.Method == HttpMethod.Delete);
        Assert.DoesNotContain(_requests, r => r.Method == HttpMethod.Get && r.Path.Contains("/users/"));
    }

    [Fact]
    public async Task AssignRolesAsync_SeveralRoles_SendsOneAdditiveRequest()
    {
        RespondToRoles(AllRoles);
        RespondToUserRoles(HttpStatusCode.NoContent);
        var service = CreateService();

        var result = await service.AssignRolesAsync(DualRoleUser, [UserRole.PropertyOwner, UserRole.LongTermLandlord]);

        Assert.True(result.Succeeded);
        var assign = Assert.Single(_requests, r => r.Method == HttpMethod.Post);
        Assert.Equal(["rol_owner", "rol_ltl"], RoleIdsOf(assign));
        Assert.DoesNotContain(_requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task RemoveRoleAsync_RemovesOnlyTheNamedRole()
    {
        RespondToRoles(AllRoles);
        RespondToUserRoles(HttpStatusCode.NoContent);
        var service = CreateService();

        var result = await service.RemoveRoleAsync(DualRoleUser, UserRole.PropertyOwner);

        Assert.True(result.Succeeded);
        var remove = Assert.Single(_requests, r => r.Method == HttpMethod.Delete);
        Assert.Equal(["rol_owner"], RoleIdsOf(remove));
        Assert.DoesNotContain(_requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task AssignRoleAsync_CalledTwice_FetchesRoleIdsOnce()
    {
        var rolesRequest = RespondToRoles(AllRoles);
        RespondToUserRoles(HttpStatusCode.NoContent);
        var cache = new MemoryCache(new MemoryCacheOptions());

        await CreateService(cache).AssignRoleAsync("auth0|a", UserRole.Supplier);
        await CreateService(cache).AssignRoleAsync("auth0|b", UserRole.Admin);

        Assert.Equal(1, _http.GetMatchCount(rolesRequest));
        Assert.Equal(2, _requests.Count(r => r.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task AssignRoleAsync_ManagementApiError_ReturnsFailedResultInsteadOfSwallowing()
    {
        RespondToRoles(AllRoles);
        RespondToUserRoles(HttpStatusCode.InternalServerError);
        var service = CreateService();

        var result = await service.AssignRoleAsync(DualRoleUser, UserRole.Supplier);

        Assert.False(result.Succeeded);
        Assert.Equal(Auth0SyncStatus.Failed, result.Status);
        Assert.Equal(Auth0SyncResult.ApiErrorCode, result.ErrorCode);
    }

    [Fact]
    public async Task AssignRoleAsync_TokenUnavailable_ReturnsTokenFailed()
    {
        _tokenProvider.Setup(p => p.GetTokenAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Auth0ManagementTokenException("Auth0 token endpoint returned HTTP 401."));
        var service = CreateService();

        var result = await service.AssignRoleAsync(DualRoleUser, UserRole.Supplier);

        Assert.Equal(Auth0SyncResult.TokenFailedCode, result.ErrorCode);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task AssignRoleAsync_ManagementApiReturns401_RenewsTokenAndRetriesOnce()
    {
        RespondToRoles(AllRoles);
        var posts = 0;
        _http.When(HttpMethod.Post, $"{ApiBase}/users/*").Respond(async request =>
        {
            await Record(request);
            return ++posts == 1
                ? JsonResponse(HttpStatusCode.Unauthorized, new { statusCode = 401, error = "Unauthorized", message = "Expired token" })
                : new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var service = CreateService();

        var result = await service.AssignRoleAsync(DualRoleUser, UserRole.Supplier);

        Assert.True(result.Succeeded);
        Assert.Equal(2, posts);
        _tokenProvider.Verify(p => p.Invalidate(), Times.Once);
    }

    [Fact]
    public async Task AssignRoleAsync_RoleMissingInAuth0_RefreshesOnceThenReturnsRoleNotFound()
    {
        var rolesRequest = RespondToRoles([new { id = "rol_owner", name = "PropertyOwner", description = "" }]);
        RespondToUserRoles(HttpStatusCode.NoContent);
        var service = CreateService();

        var result = await service.AssignRoleAsync(DualRoleUser, UserRole.Supplier);

        Assert.Equal(Auth0SyncResult.RoleNotFoundCode, result.ErrorCode);
        Assert.Equal(2, _http.GetMatchCount(rolesRequest));
        Assert.DoesNotContain(_requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task AssignRoleAsync_NotConfigured_ReturnsNotConfiguredWithoutHttpCalls()
    {
        _tokenProvider.SetupGet(p => p.IsConfigured).Returns(false);
        var service = CreateService();

        var result = await service.AssignRoleAsync(DualRoleUser, UserRole.Supplier);

        Assert.Equal(Auth0SyncStatus.NotConfigured, result.Status);
        Assert.Equal(Auth0SyncResult.NotConfiguredCode, result.ErrorCode);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task SetBlockedAsync_Block_SendsPatchWithBlockedTrueOnly()
    {
        RespondToUserUpdate(HttpStatusCode.OK);
        var service = CreateService();

        var result = await service.SetBlockedAsync(DualRoleUser, blocked: true);

        Assert.True(result.Succeeded);
        var patch = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Patch, patch.Method);
        Assert.EndsWith("/users/auth0%7Chost-and-supplier", patch.Path);
        var body = JsonDocument.Parse(patch.Body!).RootElement;
        Assert.True(body.GetProperty("blocked").GetBoolean());
        // Nothing else of the profile is overwritten.
        Assert.Equal(["blocked"], body.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task SetBlockedAsync_Unblock_SendsBlockedFalse()
    {
        RespondToUserUpdate(HttpStatusCode.OK);
        var service = CreateService();

        var result = await service.SetBlockedAsync(DualRoleUser, blocked: false);

        Assert.True(result.Succeeded);
        Assert.False(JsonDocument.Parse(Assert.Single(_requests).Body!).RootElement.GetProperty("blocked").GetBoolean());
    }

    [Fact]
    public async Task SetBlockedAsync_ManagementApiError_ReturnsFailedResult()
    {
        RespondToUserUpdate(HttpStatusCode.InternalServerError);
        var service = CreateService();

        var result = await service.SetBlockedAsync(DualRoleUser, blocked: true);

        Assert.Equal(Auth0SyncResult.ApiErrorCode, result.ErrorCode);
    }

    [Fact]
    public async Task SetBlockedAsync_NotConfigured_ReturnsNotConfiguredWithoutHttpCalls()
    {
        _tokenProvider.SetupGet(p => p.IsConfigured).Returns(false);
        var service = CreateService();

        var result = await service.SetBlockedAsync(DualRoleUser, blocked: true);

        Assert.Equal(Auth0SyncStatus.NotConfigured, result.Status);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task GetUserRolesAsync_UserWithCasazenAndForeignRoles_ReturnsOnlyCasazenRoles()
    {
        _http.When(HttpMethod.Get, $"{ApiBase}/users/*").Respond(async request =>
        {
            await Record(request);
            return JsonResponse(HttpStatusCode.OK, new object[]
            {
                new { id = "rol_owner", name = "PropertyOwner", description = "" },
                new { id = "rol_supplier", name = "supplier", description = "" },
                new { id = "rol_other", name = "OtherAppEditor", description = "" },
            });
        });
        var service = CreateService();

        var result = await service.GetUserRolesAsync(DualRoleUser);

        Assert.True(result.Sync.Succeeded);
        Assert.Equal([UserRole.PropertyOwner, UserRole.Supplier], result.Roles.Order());
        var read = Assert.Single(_requests);
        Assert.Contains("/users/auth0%7Chost-and-supplier/roles", read.Path);
    }

    [Fact]
    public async Task GetUserRolesAsync_ManagementApiError_ReturnsFailedWithoutRoles()
    {
        RespondToUserRoles(HttpStatusCode.InternalServerError);
        var service = CreateService();

        var result = await service.GetUserRolesAsync(DualRoleUser);

        Assert.Equal(Auth0SyncResult.ApiErrorCode, result.Sync.ErrorCode);
        Assert.Empty(result.Roles);
    }

    private Auth0ManagementService CreateService(IMemoryCache? cache = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(_http, disposeHandler: false));
        return new Auth0ManagementService(
            _tokenProvider.Object,
            factory.Object,
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            NullLogger<Auth0ManagementService>.Instance);
    }

    private MockedRequest RespondToRoles(object[] roles) =>
        _http.When(HttpMethod.Get, $"{ApiBase}/roles").Respond(async request =>
        {
            await Record(request);
            return JsonResponse(HttpStatusCode.OK, roles);
        });

    private void RespondToUserRoles(HttpStatusCode status)
    {
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Delete, HttpMethod.Get })
        {
            _http.When(method, $"{ApiBase}/users/*").Respond(async request =>
            {
                await Record(request);
                return status == HttpStatusCode.NoContent
                    ? new HttpResponseMessage(status)
                    : JsonResponse(status, new { statusCode = (int)status, error = "Internal Server Error", message = "boom" });
            });
        }
    }

    private void RespondToUserUpdate(HttpStatusCode status) =>
        _http.When(HttpMethod.Patch, $"{ApiBase}/users/*").Respond(async request =>
        {
            await Record(request);
            return status == HttpStatusCode.OK
                ? JsonResponse(status, new { user_id = DualRoleUser, blocked = true })
                : JsonResponse(status, new { statusCode = (int)status, error = "Internal Server Error", message = "boom" });
        });

    private async Task Record(HttpRequestMessage request)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
        lock (_requests)
        {
            _requests.Add(new RecordedRequest(request.Method, request.RequestUri!.AbsoluteUri, body));
        }
    }

    private static string[] RoleIdsOf(RecordedRequest request) =>
        JsonDocument.Parse(request.Body!).RootElement.GetProperty("roles")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object payload) =>
        new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

    private sealed record RecordedRequest(HttpMethod Method, string Path, string? Body);
}
