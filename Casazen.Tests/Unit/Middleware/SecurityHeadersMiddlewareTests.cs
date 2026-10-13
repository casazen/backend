using Casazen.Web.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Casazen.Tests.Unit.Middleware;

/// <summary>FD-17 (A9-29): baseline security headers on every API response, HSTS outside Development/Testing.</summary>
public class SecurityHeadersMiddlewareTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task InvokeAsync_DeployedEnvironment_SendsHstsAndBaselineHeaders(string environment)
    {
        await using var app = await StartAsync(environment);

        using var response = await app.GetTestClient().GetAsync("/api/ping");

        Assert.Equal("max-age=31536000; includeSubDomains", Header(response, "Strict-Transport-Security"));
        AssertBaselineHeaders(response);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public async Task InvokeAsync_LocalEnvironment_SendsBaselineHeadersWithoutHsts(string environment)
    {
        await using var app = await StartAsync(environment);

        using var response = await app.GetTestClient().GetAsync("/api/ping");

        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
        AssertBaselineHeaders(response);
    }

    [Fact]
    public async Task InvokeAsync_ResponseClearedByErrorHandling_KeepsHeaders()
    {
        await using var app = await StartAsync("Production");

        using var response = await app.GetTestClient().GetAsync("/api/cleared");

        Assert.Equal(500, (int)response.StatusCode);
        Assert.Equal("max-age=31536000; includeSubDomains", Header(response, "Strict-Transport-Security"));
        AssertBaselineHeaders(response);
    }

    [Fact]
    public async Task InvokeAsync_NotFound_KeepsHeaders()
    {
        await using var app = await StartAsync("Production");

        using var response = await app.GetTestClient().GetAsync("/does-not-exist");

        Assert.Equal(404, (int)response.StatusCode);
        AssertBaselineHeaders(response);
    }

    private static void AssertBaselineHeaders(HttpResponseMessage response)
    {
        Assert.Equal("frame-ancestors 'none'", Header(response, "Content-Security-Policy"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("strict-origin-when-cross-origin", Header(response, "Referrer-Policy"));
        Assert.Equal("geolocation=(), microphone=(), camera=()", Header(response, "Permissions-Policy"));
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
            ? Assert.Single(values)
            : throw new Xunit.Sdk.XunitException($"Missing response header {name}");

    private static async Task<WebApplication> StartAsync(string environment)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();

        var app = builder.Build();
        app.UseSecurityHeaders();
        app.MapGet("/api/ping", () => "pong");
        app.MapGet("/api/cleared", async (HttpContext context) =>
        {
            // What an error handler does: drop what was written so far, then write the error.
            context.Response.Headers["X-Partial"] = "1";
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync("error");
        });
        await app.StartAsync();
        return app;
    }
}
