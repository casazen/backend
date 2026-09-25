using System.Security.Claims;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

public class HangfireAuthorizationFilterTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Authorize_WithValidApiKey_ReturnsTrue()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Hangfire-ApiKey"] = "secret-key";

        Assert.True(HangfireAuthorizationFilter.AuthorizeRequest(
            httpContext,
            Config(new() { ["Hangfire:DashboardApiKey"] = "secret-key" })));
    }

    [Fact]
    public void Authorize_WithInvalidApiKey_ReturnsFalse()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Hangfire-ApiKey"] = "wrong-key";

        Assert.False(HangfireAuthorizationFilter.AuthorizeRequest(
            httpContext,
            Config(new() { ["Hangfire:DashboardApiKey"] = "secret-key" })));
    }

    [Theory]
    [InlineData("secret-ke")]
    [InlineData("secret-key-longer")]
    [InlineData("SECRET-KEY")]
    [InlineData("")]
    public void Authorize_WithApiKeyDifferingInLengthOrCase_ReturnsFalse(string providedKey)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Hangfire-ApiKey"] = providedKey;

        Assert.False(HangfireAuthorizationFilter.AuthorizeRequest(
            httpContext,
            Config(new() { ["Hangfire:DashboardApiKey"] = "secret-key" })));
    }

    [Theory]
    [InlineData("secret-key", "secret-key", true)]
    [InlineData("chiave-è-segreta", "chiave-è-segreta", true)]
    [InlineData("secret-key", "secret-kez", false)]
    [InlineData("secret", "secret-key", false)]
    public void ApiKeysMatch_Keys_ComparesWholeValue(string provided, string expected, bool match)
    {
        Assert.Equal(match, HangfireAuthorizationFilter.ApiKeysMatch(provided, expected));
    }

    [Fact]
    public void Authorize_WithAdminRole_ReturnsTrue()
    {
        var identity = new ClaimsIdentity(
            [new Claim("https://casazen.app/roles", "Admin")],
            "TestAuth");
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };

        Assert.True(HangfireAuthorizationFilter.AuthorizeRequest(httpContext, Config(new())));
    }

    [Fact]
    public void Authorize_WithoutApiKeyOrAdmin_ReturnsFalse()
    {
        var httpContext = new DefaultHttpContext();

        Assert.False(HangfireAuthorizationFilter.AuthorizeRequest(
            httpContext,
            Config(new() { ["Hangfire:DashboardApiKey"] = "secret-key" })));
    }
}
