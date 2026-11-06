using System.Text.Json;
using Casazen.Core.Events;
using Casazen.Core.Features;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// What the two handlers of the billing data sync with Octorate share (E06/F02/US01/T04): when they run at all, which failures of Octorate the write-back
/// delivers the event again for, the code of a reservation with no customer record, how much of the quota of an org the pre-fill leaves alone, and how the
/// payload of an event is read. No state, no call.
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

    /// <summary>The calls the pre-fill of one booking makes to Octorate: the reservation (for the customer record it names) and the record itself.</summary>
    public const int PrefillCalls = 2;

    /// <summary>
    /// The calls of the bucket of an org (<see cref="OctorateRateLimiter"/>) that the pre-fill never uses: half of it. The bucket is shared with
    /// everything else CasaZen sends to Octorate for the org (the bookings it creates, the payments, the calendar), and the pre-fill is a convenience, so
    /// it must never be what empties it.
    /// </summary>
    public const int PrefillQuotaReserve = OctorateRateLimiter.RequestsPerMinute / 2;

    /// <summary>
    /// True while the bucket of <paramref name="orgId"/> holds the calls of a pre-fill and still leaves <see cref="PrefillQuotaReserve"/>. A pre-fill that
    /// finds less is not made and is not an error (the booking is left for the host to fill in): an import of many bookings reads the first ones and leaves
    /// the quota to the work that matters, instead of draining it and sending the next minutes of that work into the rate limit.
    /// </summary>
    public static bool QuotaAllowsPrefill(OctorateRateLimiter rateLimiter, Guid orgId)
    {
        ArgumentNullException.ThrowIfNull(rateLimiter);

        return rateLimiter.AvailableCalls(orgId) - PrefillCalls >= PrefillQuotaReserve;
    }

    /// <summary>
    /// The code kept on the invoice data (and logged) when the write-back finds that the reservation names no customer record: nothing was sent, nothing
    /// is marked as written, and the data is written once the reservation names one and the data is saved again. Not an error of Octorate, so it has a
    /// code of its own.
    /// </summary>
    public const string NoCustomerRecordCode = "octorate_no_customer_record";

    /// <summary>
    /// True for a failing or refused call that the <b>write-back</b> delivers again (AC-E06-F02-US01-03): Octorate did not answer or failed (5xx), the
    /// rate limit of the org was reached, or Octorate refused the credentials or the guests' data (403: CasaZen's outbound IPs not whitelisted yet, D24).
    /// These are rethrown after their code is kept on the invoice data and a log line with ids and a code is written, so the outbox delivers the event
    /// again (ADR-022-R4, R5) and, if it still fails after five deliveries, keeps it dead-lettered with the code. Anything else (a record or a reservation
    /// Octorate does not know, a request it rejects, a conflict, the wrong environment, or no connection to use) would be answered the same five times: its
    /// code is kept on the invoice data, it is logged, and the data is written at the next save. The pre-fill has no row to keep a code on, so it delivers
    /// every failure again and lets the outbox keep it (<see cref="InvoiceDataFromOctorateHandler"/>).
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
