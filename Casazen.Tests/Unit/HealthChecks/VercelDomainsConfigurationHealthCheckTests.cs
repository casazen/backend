using Casazen.Core.Options;
using Casazen.Web.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.HealthChecks;

/// <summary>BK-17 (D9, A3-25): the app signals the missing Vercel configuration by variable name, never by value.</summary>
public class VercelDomainsConfigurationHealthCheckTests
{
    private const string TokenValue = "vcl_TokenValueNeverShown";

    [Fact]
    public async Task CheckHealthAsync_TokenAndProjectSet_IsHealthy()
    {
        var result = await CheckAsync(new VercelDomainsOptions { ApiToken = TokenValue, ProjectId = "prj_1" });

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_NothingSet_IsDegradedNamingBothVariables()
    {
        var result = await CheckAsync(new VercelDomainsOptions());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("Vercel__ApiToken", result.Description);
        Assert.Contains("Vercel__ProjectId", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_OnlyTheProjectMissing_NamesOnlyTheProjectAndNeverTheToken()
    {
        var result = await CheckAsync(new VercelDomainsOptions { ApiToken = TokenValue, ProjectId = "  " });

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("Vercel__ProjectId", result.Description);
        Assert.DoesNotContain("Vercel__ApiToken", result.Description);
        Assert.DoesNotContain(TokenValue, result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_TeamIdIsOptional()
    {
        var result = await CheckAsync(new VercelDomainsOptions { ApiToken = TokenValue, ProjectId = "prj_1", TeamId = null });

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.DoesNotContain("TeamId", result.Description);
    }

    [Fact]
    public void MissingVariables_NeverReturnsAValue()
    {
        var options = new VercelDomainsOptions { ApiToken = TokenValue };

        var missing = options.MissingVariables();

        Assert.Equal(["Vercel__ProjectId"], missing);
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void ApiBaseUrl_DefaultsToTheDocumentedHttpsHost()
    {
        Assert.StartsWith("https://", new VercelDomainsOptions().ApiBaseUrl, StringComparison.Ordinal);
    }

    private static Task<HealthCheckResult> CheckAsync(VercelDomainsOptions options) =>
        new VercelDomainsConfigurationHealthCheck(Options.Create(options)).CheckHealthAsync(new HealthCheckContext());
}
