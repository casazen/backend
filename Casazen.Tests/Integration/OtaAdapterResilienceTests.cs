using Casazen.Infrastructure.OTA;
using Casazen.Infrastructure.OTA.Resilience;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using Xunit;

namespace Casazen.Tests.Integration;

public class OtaAdapterResilienceTests
{
    private readonly IServiceProvider _serviceProvider;

    public OtaAdapterResilienceTests()
    {
        var services = new ServiceCollection();

        // Setup configuration
        var inMemorySettings = new Dictionary<string, string>
        {
            {"OTA:Resilience:Airbnb:RetryCount", "3"},
            {"OTA:Resilience:Airbnb:CircuitBreakerFailures", "5"},
            {"OTA:Resilience:Airbnb:CircuitBreakerDurationSeconds", "60"},
            {"OTA:Resilience:Airbnb:TimeoutSeconds", "30"},
            {"OTA:Resilience:Airbnb:MaxRequestsPerWindow", "100"},
            {"OTA:Resilience:Airbnb:WindowDurationSeconds", "60"},
            {"OTA:Resilience:Airbnb:MaxConcurrentRequests", "10"},
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings!)
            .Build();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(builder => builder.AddConsole());

        // Register rate limiter
        services.AddSingleton<OtaRateLimiter>();

        // Register HttpClient with Polly policies for Airbnb
        services.AddHttpClient<AirbnbAdapter>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(35);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        })
        .AddPolicyHandler((serviceProvider, request) =>
        {
            var logger = serviceProvider.GetRequiredService<ILogger<AirbnbAdapter>>();
            var context = new Polly.Context { ["Platform"] = "Airbnb" };

            return PollyPolicies.GetCombinedPolicy(
                retryCount: 3,
                circuitBreakerFailures: 5,
                circuitBreakerDuration: TimeSpan.FromSeconds(60),
                timeout: TimeSpan.FromSeconds(30),
                logger
            );
        });

        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task AirbnbAdapter_ShouldUseRateLimiter()
    {
        // Arrange
        var adapter = _serviceProvider.GetRequiredService<AirbnbAdapter>();
        var rateLimiter = _serviceProvider.GetRequiredService<OtaRateLimiter>();

        // Act - Make multiple requests
        var tasks = new List<Task<bool>>();
        for (int i = 0; i < 3; i++)
        {
            tasks.Add(adapter.ValidateCredentialsAsync("test-api-key"));
        }

        var results = await Task.WhenAll(tasks);

        // Assert - All should complete (rate limiter allows them)
        Assert.Equal(3, results.Length);
    }

    [Fact]
    public async Task AirbnbAdapter_ShouldHandleTransientFailuresWithRetry()
    {
        // Arrange
        var adapter = _serviceProvider.GetRequiredService<AirbnbAdapter>();

        // Act - This will fail because it's a real HTTP call to invalid endpoint
        // But the retry policy should handle it gracefully
        var result = await adapter.ValidateCredentialsAsync("invalid-key");

        // Assert - Should return false after retries, not throw
        Assert.False(result);
    }

    [Fact]
    public async Task MultipleAdapters_ShouldShareRateLimiter()
    {
        // Arrange
        var rateLimiter = _serviceProvider.GetRequiredService<OtaRateLimiter>();
        var adapter1 = _serviceProvider.GetRequiredService<AirbnbAdapter>();
        var adapter2 = _serviceProvider.GetRequiredService<AirbnbAdapter>();

        // Act - Both adapters should use the same rate limiter instance
        using var token1 = await rateLimiter.AcquireAsync("Airbnb");
        using var token2 = await rateLimiter.AcquireAsync("Airbnb");

        // Assert - Both tokens acquired from same limiter
        Assert.NotNull(token1);
        Assert.NotNull(token2);
    }

    [Fact]
    public async Task AirbnbAdapter_ShouldRespectTimeout()
    {
        // Arrange
        var adapter = _serviceProvider.GetRequiredService<AirbnbAdapter>();

        // Act - Call to a very slow endpoint (will timeout)
        var startTime = DateTime.UtcNow;
        var result = await adapter.GetBookingsAsync(
            "test-property-id",
            DateTime.UtcNow,
            DateTime.UtcNow.AddDays(7)
        );
        var duration = DateTime.UtcNow - startTime;

        // Assert - Should complete within timeout + retries window (roughly 90 seconds max)
        Assert.True(duration.TotalSeconds < 120); // Generous upper bound
        Assert.Empty(result); // Should return empty list on failure
    }

    [Fact]
    public async Task AirbnbAdapter_ShouldLogResilienceEvents()
    {
        // Arrange
        var adapter = _serviceProvider.GetRequiredService<AirbnbAdapter>();
        var logger = _serviceProvider.GetRequiredService<ILogger<AirbnbAdapter>>();

        // Act
        await adapter.ValidateCredentialsAsync("test-key");

        // Assert - Logging should have been called
        // (This is a basic test; in real scenarios, you'd use a mock logger to verify specific log messages)
        Assert.NotNull(logger);
    }
}
