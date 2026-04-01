using Casazen.Infrastructure.OTA.Resilience;
using Microsoft.Extensions.Logging;
using Moq;
using Polly.Timeout;
using System.Net;
using Xunit;

namespace Casazen.Tests.Unit.OTA.Resilience;

public class PollyPoliciesTests
{
    private readonly Mock<ILogger> _mockLogger;

    public PollyPoliciesTests()
    {
        _mockLogger = new Mock<ILogger>();
    }

    [Fact]
    public async Task GetRetryPolicy_ShouldRetryOnTransientError()
    {
        // Arrange
        var retryPolicy = PollyPolicies.GetRetryPolicy(3, _mockLogger.Object);
        var attemptCount = 0;
        var httpClient = new HttpClient(new MockHttpMessageHandler(async (request, token) =>
        {
            attemptCount++;
            if (attemptCount < 3)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        // Act
        var response = await retryPolicy.ExecuteAsync(() =>
            httpClient.GetAsync("http://test.com"));

        // Assert
        Assert.Equal(3, attemptCount);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetCircuitBreakerPolicy_ShouldOpenCircuitAfterFailures()
    {
        // Arrange
        var circuitBreakerPolicy = PollyPolicies.GetCircuitBreakerPolicy(
            failureThreshold: 2,
            durationOfBreak: TimeSpan.FromSeconds(1),
            _mockLogger.Object
        );

        var attemptCount = 0;
        var httpClient = new HttpClient(new MockHttpMessageHandler(async (request, token) =>
        {
            attemptCount++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }));

        // Act & Assert - First two failures should work
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await circuitBreakerPolicy.ExecuteAsync(() => httpClient.GetAsync("http://test.com")));
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await circuitBreakerPolicy.ExecuteAsync(() => httpClient.GetAsync("http://test.com")));

        // Circuit should now be open, so third attempt should fail immediately without calling handler
        var attemptCountBeforeCircuitOpen = attemptCount;
        await Assert.ThrowsAsync<Polly.CircuitBreaker.BrokenCircuitException>(async () =>
            await circuitBreakerPolicy.ExecuteAsync(() => httpClient.GetAsync("http://test.com")));

        Assert.Equal(attemptCountBeforeCircuitOpen, attemptCount); // No new attempt should be made
    }

    [Fact]
    public async Task GetTimeoutPolicy_ShouldTimeoutLongRunningRequests()
    {
        // Arrange
        var timeoutPolicy = PollyPolicies.GetTimeoutPolicy(TimeSpan.FromMilliseconds(100), _mockLogger.Object);

        // Act & Assert
        await Assert.ThrowsAsync<TimeoutRejectedException>(async () =>
            await timeoutPolicy.ExecuteAsync(async () =>
            {
                await Task.Delay(500);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }));
    }

    [Fact]
    public async Task GetCombinedPolicy_ShouldApplyAllPolicies()
    {
        // Arrange
        var combinedPolicy = PollyPolicies.GetCombinedPolicy(
            retryCount: 2,
            circuitBreakerFailures: 3,
            circuitBreakerDuration: TimeSpan.FromSeconds(1),
            timeout: TimeSpan.FromSeconds(1),
            _mockLogger.Object
        );

        var attemptCount = 0;
        var httpClient = new HttpClient(new MockHttpMessageHandler(async (request, token) =>
        {
            attemptCount++;
            if (attemptCount == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); // Will be retried
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        // Act
        var response = await combinedPolicy.ExecuteAsync(() =>
            httpClient.GetAsync("http://test.com"));

        // Assert
        Assert.Equal(2, attemptCount); // First attempt + 1 retry
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetRetryPolicy_ShouldLogRetryAttempts()
    {
        // Arrange
        var retryPolicy = PollyPolicies.GetRetryPolicy(2, _mockLogger.Object);
        var attemptCount = 0;
        var httpClient = new HttpClient(new MockHttpMessageHandler(async (request, token) =>
        {
            attemptCount++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }));

        // Act
        try
        {
            await retryPolicy.ExecuteAsync(() => httpClient.GetAsync("http://test.com"));
        }
        catch { }

        // Assert - Should log retry attempts
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("Retry")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetCircuitBreakerPolicy_ShouldLogCircuitStateChanges()
    {
        // Arrange
        var circuitBreakerPolicy = PollyPolicies.GetCircuitBreakerPolicy(
            failureThreshold: 2,
            durationOfBreak: TimeSpan.FromSeconds(1),
            _mockLogger.Object
        );

        var httpClient = new HttpClient(new MockHttpMessageHandler(async (request, token) =>
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }));

        // Act
        try
        {
            await circuitBreakerPolicy.ExecuteAsync(() => httpClient.GetAsync("http://test.com"));
            await circuitBreakerPolicy.ExecuteAsync(() => httpClient.GetAsync("http://test.com"));
        }
        catch { }

        // Assert - Should log circuit breaker opening
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("Circuit breaker opened")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}

/// <summary>
/// Mock HTTP message handler for testing
/// </summary>
public class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _sendAsyncFunc;

    public MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsyncFunc)
    {
        _sendAsyncFunc = sendAsyncFunc;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return await _sendAsyncFunc(request, cancellationToken);
    }
}
