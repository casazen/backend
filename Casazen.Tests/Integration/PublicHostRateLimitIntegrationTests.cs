using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Integration;

public class PublicHostRateLimitIntegrationTests
{
    [Fact]
    public async Task ResolveHost_RateLimit_IsPartitionedByForwardedClientIp()
    {
        using var factory = new PublicHostRateLimitFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var first = await ResolveMissingHostAsync(client, "203.0.113.10");
        Assert.Equal(HttpStatusCode.NotFound, first.StatusCode);

        var repeatedSameClient = await ResolveMissingHostAsync(client, "203.0.113.10");
        Assert.Equal(HttpStatusCode.TooManyRequests, repeatedSameClient.StatusCode);

        var differentClient = await ResolveMissingHostAsync(client, "203.0.113.11");
        Assert.Equal(HttpStatusCode.NotFound, differentClient.StatusCode);
    }

    private static Task<HttpResponseMessage> ResolveMissingHostAsync(HttpClient client, string forwardedFor)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/public/resolve-host?host=missing.casazen.it");
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return client.SendAsync(request);
    }

    private sealed class PublicHostRateLimitFactory : CasazenWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["PublicHost:RateLimitPermitLimit"] = "1",
                });
            });
        }
    }
}
