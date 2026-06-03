using Casazen.Infrastructure.OTA.Resilience;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using System.Diagnostics;
using Xunit;

namespace Casazen.Tests.Unit.OTA.Resilience;

public class OtaRateLimiterTests
{
    private readonly Mock<ILogger<OtaRateLimiter>> _mockLogger;
    private readonly IConfiguration _configuration;

    public OtaRateLimiterTests()
    {
        _mockLogger = new Mock<ILogger<OtaRateLimiter>>();

        // Setup test configuration
        var inMemorySettings = new Dictionary<string, string>
        {
            {"OTA:Resilience:Airbnb:MaxRequestsPerWindow", "5"},
            {"OTA:Resilience:Airbnb:WindowDurationSeconds", "1"},
            {"OTA:Resilience:Airbnb:MaxConcurrentRequests", "2"},
            {"OTA:Resilience:BookingCom:MaxRequestsPerWindow", "10"},
            {"OTA:Resilience:BookingCom:WindowDurationSeconds", "1"},
            {"OTA:Resilience:BookingCom:MaxConcurrentRequests", "3"},
        };

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings!)
            .Build();
    }

    [Fact]
    public async Task AcquireAsync_ShouldAllowRequestWithinLimit()
    {
        // Arrange
        var rateLimiter = new OtaRateLimiter(_configuration, _mockLogger.Object);

        // Act
        using var token = await rateLimiter.AcquireAsync("Airbnb");

        // Assert
        Assert.NotNull(token);
    }

    [Fact]
    public async Task AcquireAsync_ShouldEnforceConcurrentRequestLimit()
    {
        // Arrange
        var rateLimiter = new OtaRateLimiter(_configuration, _mockLogger.Object);
        var tasks = new List<Task<IDisposable>>();

        // Act - Acquire 2 tokens (max concurrent)
        var token1 = await rateLimiter.AcquireAsync("Airbnb");
        var token2 = await rateLimiter.AcquireAsync("Airbnb");

        // Try to acquire a third token (should block)
        var stopwatch = Stopwatch.StartNew();
        var token3Task = rateLimiter.AcquireAsync("Airbnb");

        // Give it a small delay to ensure it's blocked
        await Task.Delay(150);
        Assert.False(token3Task.IsCompleted); // Should still be waiting

        // Release one token
        token1.Dispose();

        // Now the third token should be acquired
        var token3 = await token3Task;
        stopwatch.Stop();

        // Assert
        Assert.NotNull(token3);
        Assert.True(stopwatch.ElapsedMilliseconds >= 100); // Was blocked for at least 100ms

        // Cleanup
        token2.Dispose();
        token3.Dispose();
    }

    [Fact]
    public async Task AcquireAsync_ShouldEnforceRateLimitWindow()
    {
        // Arrange
        var rateLimiter = new OtaRateLimiter(_configuration, _mockLogger.Object);
        var tokens = new List<IDisposable>();

        // Act - Acquire max requests quickly (5 requests per 1 second window)
        for (int i = 0; i < 5; i++)
        {
            var token = await rateLimiter.AcquireAsync("Airbnb");
            tokens.Add(token);
            token.Dispose(); // Release immediately to test rate limit, not concurrency
        }

        // Try to acquire one more (should be delayed)
        var stopwatch = Stopwatch.StartNew();
        using var extraToken = await rateLimiter.AcquireAsync("Airbnb");
        stopwatch.Stop();

        // Assert - Should have waited for the window to pass
        Assert.True(stopwatch.ElapsedMilliseconds >= 900); // Close to 1 second window
    }

    [Fact]
    public async Task AcquireAsync_ShouldHandleUnknownPlatformGracefully()
    {
        // Arrange
        var rateLimiter = new OtaRateLimiter(_configuration, _mockLogger.Object);

        // Act
        using var token = await rateLimiter.AcquireAsync("UnknownPlatform");

        // Assert - Should not throw, should return a no-op token
        Assert.NotNull(token);

        // Verify warning was logged
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("No rate limit configuration")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task AcquireAsync_ShouldIsolatePlatformRateLimits()
    {
        // Arrange
        var rateLimiter = new OtaRateLimiter(_configuration, _mockLogger.Object);

        // Act - Max out Airbnb rate limit (5 requests)
        var airbnbTokens = new List<IDisposable>();
        for (int i = 0; i < 5; i++)
        {
            var token = await rateLimiter.AcquireAsync("Airbnb");
            airbnbTokens.Add(token);
            token.Dispose();
        }

        // BookingCom should still be available immediately (different platform)
        var stopwatch = Stopwatch.StartNew();
        using var bookingToken = await rateLimiter.AcquireAsync("BookingCom");
        stopwatch.Stop();

        // Assert - BookingCom acquisition should be instant (< 50ms)
        Assert.True(stopwatch.ElapsedMilliseconds < 50);
    }

    [Fact]
    public async Task AcquireAsync_ShouldLogRateLimitAcquisition()
    {
        // Arrange
        var rateLimiter = new OtaRateLimiter(_configuration, _mockLogger.Object);

        // Act
        using var token = await rateLimiter.AcquireAsync("Airbnb");

        // Assert - Should log acquisition
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("Rate limit acquired")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task AcquireAsync_ShouldLogRateLimitRelease()
    {
        // Arrange
        var rateLimiter = new OtaRateLimiter(_configuration, _mockLogger.Object);

        // Act
        var token = await rateLimiter.AcquireAsync("Airbnb");
        token.Dispose();

        // Assert - Should log release
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("Rate limit released")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task AcquireAsync_ShouldSupportCancellation()
    {
        // Arrange
        var rateLimiter = new OtaRateLimiter(_configuration, _mockLogger.Object);
        var cts = new CancellationTokenSource();

        // Max out concurrent requests
        var token1 = await rateLimiter.AcquireAsync("Airbnb");
        var token2 = await rateLimiter.AcquireAsync("Airbnb");

        // Act - Try to acquire with cancellation
        var acquireTask = rateLimiter.AcquireAsync("Airbnb", cts.Token);
        await Task.Delay(50); // Let it start waiting
        cts.Cancel();

        // Assert
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await acquireTask);

        // Cleanup
        token1.Dispose();
        token2.Dispose();
    }
}
