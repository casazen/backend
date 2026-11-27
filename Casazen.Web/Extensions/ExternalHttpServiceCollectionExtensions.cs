using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Services;
using Polly;

namespace Casazen.Web.Extensions;

/// <summary>
/// The outbound HTTP clients that need a policy of their own: the anti-SSRF client for the URLs a user chooses and the ConcordatoFacile status API
/// client with its Polly resilience.
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
public static class ExternalHttpServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenExternalHttpClients(this IServiceCollection services)
    {
        services.AddOptions<SafeExternalHttpOptions>().BindConfiguration(SafeExternalHttpOptions.SectionName);
        services.AddSingleton<IExternalHostResolver, SystemDnsHostResolver>();
        services.AddSingleton<ISafeExternalHttpClient, SafeExternalHttpClient>();

        services.AddHttpClient<ConcordatoFacileApiClient>((serviceProvider, client) =>
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();

            var baseUrl = configuration["ConcordatoFacile:StatusApiUrl"] ?? "https://stato.concordatofacile.it";
            client.BaseAddress = new Uri(baseUrl);

            // Without the key no header is set and ConcordatoFacileApiClient.IsConfigured is false: the status service does not ask
            // ConcordatoFacile and says once (CF-BE) that the key is missing. This delegate runs at every creation of the client, so it
            // must not log: it used to warn at every request that reached the controller, checkout and access included.
            // A person pastes the key in Railway: a blank or a line break at its edge is not part of it and would make every read fail.
            var apiKey = configuration["ConcordatoFacile:StatusApiKey"]?.Trim();
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }

            client.Timeout = TimeSpan.FromSeconds(120); // Total timeout for all retries
        })
        .AddPolicyHandler((serviceProvider, request) =>
        {
            var logger = serviceProvider.GetRequiredService<ILogger<ConcordatoFacileApiClient>>();
            return GetCfRetryPolicy(logger);
        })
        .AddPolicyHandler(Policy.TimeoutAsync<HttpResponseMessage>(30))
        .ConfigurePrimaryHttpMessageHandler(() =>
        {
            var handler = new SocketsHttpHandler
            {
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
                }
            };
            return handler;
        });

        return services;
    }

    /// <summary>
    /// Retry policy for ConcordatoFacile API: exponential backoff (1s, 2s, 4s) for transient errors
    /// (ADR-008-R3). Handles HTTP 5xx, HttpRequestException, TaskCanceledException, TimeoutException.
    /// </summary>
    private static IAsyncPolicy<HttpResponseMessage> GetCfRetryPolicy(ILogger logger)
    {
        return Policy
            .Handle<HttpRequestException>()
            .Or<TaskCanceledException>()
            .Or<TimeoutException>()
            .OrResult<HttpResponseMessage>(r => (int)r.StatusCode >= 500)
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt - 1)),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    if (outcome.Exception != null)
                    {
                        logger.LogWarning(outcome.Exception,
                            "CF API request failed (attempt {RetryCount}/3); retrying in {Delay}s",
                            retryCount, timespan.TotalSeconds);
                    }
                    else
                    {
                        logger.LogWarning(
                            "CF API returned {StatusCode} (attempt {RetryCount}/3); retrying in {Delay}s",
                            (int)outcome.Result.StatusCode, retryCount, timespan.TotalSeconds);
                    }
                });
    }
}
