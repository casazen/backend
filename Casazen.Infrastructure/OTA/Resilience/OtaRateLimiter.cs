using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Casazen.Infrastructure.OTA.Resilience;

public class OtaRateLimiter
{
    private readonly ILogger<OtaRateLimiter> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _requestTimestamps = new();
    private readonly Dictionary<string, RateLimitConfig> _configs = new();

    public OtaRateLimiter(IConfiguration configuration, ILogger<OtaRateLimiter> logger)
    {
        _logger = logger;
        LoadConfigurations(configuration);
    }

    private void LoadConfigurations(IConfiguration configuration)
    {
        var platforms = new[] { "Airbnb", "BookingCom", "Expedia", "Vrbo", "TripAdvisor", "Agoda" };

        foreach (var platform in platforms)
        {
            var section = configuration.GetSection($"OTA:Resilience:{platform}");
            var config = new RateLimitConfig
            {
                MaxRequestsPerWindow = section.GetValue<int>("MaxRequestsPerWindow", 100),
                WindowDurationSeconds = section.GetValue<int>("WindowDurationSeconds", 60),
                MaxConcurrentRequests = section.GetValue<int>("MaxConcurrentRequests", 10)
            };

            _configs[platform] = config;
            _semaphores[platform] = new SemaphoreSlim(config.MaxConcurrentRequests, config.MaxConcurrentRequests);
            _requestTimestamps[platform] = new Queue<DateTime>();

            _logger.LogInformation(
                "Rate limiter configured for {Platform}: {MaxRequests} requests per {Window}s, {MaxConcurrent} concurrent",
                platform,
                config.MaxRequestsPerWindow,
                config.WindowDurationSeconds,
                config.MaxConcurrentRequests
            );
        }
    }

    public async Task<IDisposable> AcquireAsync(string platform, CancellationToken cancellationToken = default)
    {
        if (!_configs.TryGetValue(platform, out var config))
        {
            _logger.LogWarning("No rate limit configuration found for {Platform}, allowing request", platform);
            return new NoOpDisposable();
        }

        // Wait for concurrent request slot
        await _semaphores[platform].WaitAsync(cancellationToken);

        try
        {
            await WaitForRateLimitWindow(platform, config, cancellationToken);

            var now = DateTime.UtcNow;
            lock (_requestTimestamps[platform])
            {
                _requestTimestamps[platform].Enqueue(now);
            }

            _logger.LogDebug("Rate limit acquired for {Platform}", platform);

            return new RateLimitToken(_semaphores[platform], platform, _logger);
        }
        catch
        {
            _semaphores[platform].Release();
            throw;
        }
    }

    private async Task WaitForRateLimitWindow(string platform, RateLimitConfig config, CancellationToken cancellationToken)
    {
        while (true)
        {
            var now = DateTime.UtcNow;
            var windowStart = now.AddSeconds(-config.WindowDurationSeconds);

            lock (_requestTimestamps[platform])
            {
                while (_requestTimestamps[platform].Count > 0 &&
                       _requestTimestamps[platform].Peek() < windowStart)
                {
                    _requestTimestamps[platform].Dequeue();
                }

                if (_requestTimestamps[platform].Count < config.MaxRequestsPerWindow)
                {
                    return;
                }

                var oldestRequest = _requestTimestamps[platform].Peek();
                var waitTime = oldestRequest.AddSeconds(config.WindowDurationSeconds) - now;

                if (waitTime.TotalMilliseconds > 0)
                {
                    _logger.LogWarning(
                        "Rate limit reached for {Platform}. Waiting {WaitTime}ms",
                        platform,
                        waitTime.TotalMilliseconds
                    );
                }
            }

            await Task.Delay(100, cancellationToken);
        }
    }

    public class RateLimitToken : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly string _platform;
        private readonly ILogger _logger;
        private bool _disposed;

        public RateLimitToken(SemaphoreSlim semaphore, string platform, ILogger logger)
        {
            _semaphore = semaphore;
            _platform = platform;
            _logger = logger;
        }


        public void Dispose()
        {
            if (!_disposed)
            {
                _semaphore.Release();
                _logger.LogDebug("Rate limit released for {Platform}", _platform);
                _disposed = true;
            }
        }
    }

    private class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private class RateLimitConfig
    {
        public int MaxRequestsPerWindow { get; set; }
        public int WindowDurationSeconds { get; set; }
        public int MaxConcurrentRequests { get; set; }
    }
}
