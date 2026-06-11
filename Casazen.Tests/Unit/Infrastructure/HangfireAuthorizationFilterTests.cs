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
