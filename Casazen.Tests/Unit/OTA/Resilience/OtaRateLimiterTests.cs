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
        var rateLimiter = new OtaRateLimiter(_configuration, _mockLogger.Object);

        using var token = await rateLimiter.AcquireAsync("Airbnb");

        Assert.NotNull(token);
    }

    [Fact]
    public async Task AcquireAsync_ShouldEnforceConcurrentRequestLimit()
    {
        var rateLimiter = new OtaRateLimiter(_configuration, _mockLogger.Object);
        var tasks = new List<Task<IDisposable>>();

        // Act - Acquire 2 tokens (max concurrent)
        var token1 = await rateLimiter.AcquireAsync("Airbnb");
        var token2 = await rateLimiter.AcquireAsync("Airbnb");

        var stopwatch = Stopwatch.StartNew();
        var token3Task = rateLimiter.AcquireAsync("Airbnb");

        await Task.Delay(150);
        Assert.False(token3Task.IsCompleted);

        token1.Dispose();

        var token3 = await token3Task;
        stopwatch.Stop();

        Assert.NotNull(token3);
        Assert.True(stopwatch.ElapsedMilliseconds >= 100);

        token2.Dispose();
        token3.Dispose();
    }

    [Fact]
    public async Task AcquireAsync_ShouldEnforceRateLimitWindow()
    {
        var rateLimiter = new OtaRateLimiter(_configuration, _mockLogger.Object);
        var tokens = new List<IDisposable>();

        for (int i = 0; i < 5; i++)
        {
            var token = await rateLimiter.AcquireAsync("Airbnb");
            tokens.Add(token);
            token.Dispose(); // Release immediately to test rate limit, not concurrency
        }

        var stopwatch = Stopwatch.StartNew();
        using var extraToken = await rateLimiter.AcquireAsync("Airbnb");
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds >= 900);
    }

    [Fact]
    public async Task AcquireAsync_ShouldHandleUnknownPlatformGracefully()
    {
        var rateLimiter = new OtaRateLimiter(_configuration, _mockLogger.Object);

        using var token = await rateLimiter.AcquireAsync("UnknownPlatform");

        Assert.NotNull(token);

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
        var rateLimiter = new OtaRateLimiter(_configuration, _mockLogger.Object);

        var airbnbTokens = new List<IDisposable>();
        for (int i = 0; i < 5; i++)
        {
            var token = await rateLimiter.AcquireAsync("Airbnb");
            airbnbTokens.Add(token);
            token.Dispose();
        }

        var stopwatch = Stopwatch.StartNew();
        using var bookingToken = await rateLimiter.AcquireAsync("BookingCom");
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 50);
    }

    [Fact]
    public async Task AcquireAsync_ShouldLogRateLimitAcquisition()
    {
        var rateLimiter = new OtaRateLimiter(_configuration, _mockLogger.Object);

        using var token = await rateLimiter.AcquireAsync("Airbnb");

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
        var rateLimiter = new OtaRateLimiter(_configuration, _mockLogger.Object);

        var token = await rateLimiter.AcquireAsync("Airbnb");
        token.Dispose();

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
        var rateLimiter = new OtaRateLimiter(_configuration, _mockLogger.Object);
        var cts = new CancellationTokenSource();

        var token1 = await rateLimiter.AcquireAsync("Airbnb");
        var token2 = await rateLimiter.AcquireAsync("Airbnb");

        var acquireTask = rateLimiter.AcquireAsync("Airbnb", cts.Token);
        await Task.Delay(50);
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await acquireTask);

        token1.Dispose();
        token2.Dispose();
    }
}
