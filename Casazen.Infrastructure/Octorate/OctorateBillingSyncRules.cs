using System.Text.Json;
using Casazen.Core.Events;
using Casazen.Core.Features;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// What the two handlers of the billing data sync with Octorate share (E06/F02/US01/T04): when they run at all, which failures of Octorate are worth
/// delivering the event again for, and how the payload of an event is read. No state, no call.
/// </summary>
internal static class OctorateBillingSyncRules
{
    private static readonly JsonSerializerOptions PayloadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// True while the sync runs: the Octorate connector is on <b>and</b> the sync of the billing data is (<c>Features:OctorateBillingSync</c>, off until
    /// the customer endpoints are proven in the sandbox). Either one off and the handlers read and write nothing.
    /// </summary>
    public static bool IsOn(IFeatureFlags featureFlags)
    {
        ArgumentNullException.ThrowIfNull(featureFlags);

        return featureFlags.IsEnabled(FeatureFlags.OctorateConnector) && featureFlags.IsEnabled(FeatureFlags.OctorateBillingSync);
    }

    /// <summary>
    /// True for a failing or refused call (AC-E06-F02-US01-03): Octorate did not answer or failed (5xx), the rate limit of the org was reached, or
    /// Octorate refused the credentials or the guests' data (403: CasaZen's outbound IPs not whitelisted yet, D24). These are rethrown after a log line
    /// with ids and a code, so the outbox delivers the event again (ADR-022-R4, R5) and, if it still fails after five deliveries, keeps it dead-lettered
    /// with the code: that is where support sees what did not reach Octorate. Anything else (a record or a reservation Octorate does not know, a request
    /// it rejects, a conflict, the wrong environment, or no connection to use) would be answered the same five times: it is logged with ids and a code
    /// and left.
    /// </summary>
    public static bool IsRetryable(OctorateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is OctorateServerException or OctorateRateLimitException or OctorateAuthException { IsNotConnected: false };
    }

    /// <summary>The payload of <paramref name="domainEvent"/> as <typeparamref name="T"/>, or <c>null</c> when it is not that.</summary>
    public static T? PayloadOf<T>(DomainEvent domainEvent)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        try
        {
            return JsonSerializer.Deserialize<T>(domainEvent.PayloadJson, PayloadOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
