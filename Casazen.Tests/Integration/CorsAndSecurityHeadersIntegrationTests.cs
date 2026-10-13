using Casazen.Web.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// FD-17 (A3-29, A9-28, A9-29): the real pipeline answers with the security headers and the restricted CORS policy
/// (configured origins only, no credentials, previews only through the configured pattern).
/// </summary>
public class CorsAndSecurityHeadersIntegrationTests : IClassFixture<CorsAndSecurityHeadersIntegrationTests.CorsFactory>
{
    private const string WebApp = "https://app.casazen.test";
    private const string OwnPreview = "https://casazen-web-git-feature-casazen-team.vercel.app";

    private readonly CorsFactory _factory;

    public CorsAndSecurityHeadersIntegrationTests(CorsFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_AnyApiResponse_HasSecurityHeaders()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(HealthCheckExtensions.LivePath);

        Assert.Equal("frame-ancestors 'none'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("strict-origin-when-cross-origin", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        // Testing environment: HSTS only outside Development/Testing (covered by SecurityHeadersMiddlewareTests).
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Theory]
    [InlineData(WebApp)]
    [InlineData(OwnPreview)]
    public async Task Preflight_ConfiguredOrigin_IsAllowedWithoutCredentials(string origin)
    {
        using var response = await PreflightAsync(origin);

        Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Theory]
    [InlineData("https://evil.vercel.app")]
    [InlineData("https://casazen-web-git-feature-other-team.vercel.app")]
    [InlineData("https://evil.example")]
    public async Task Preflight_OriginNotConfigured_IsRejected(string origin)
    {
        using var response = await PreflightAsync(origin);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    private async Task<HttpResponseMessage> PreflightAsync(string origin)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/properties");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");
        return await client.SendAsync(request);
    }

    public sealed class CorsFactory : CasazenWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Cors:AllowedOrigins"] = WebApp,
                    ["Cors:VercelPreviewPattern"] = "casazen-web-git-[a-z0-9-]+-casazen-team",
                }));
        }
    }
}
