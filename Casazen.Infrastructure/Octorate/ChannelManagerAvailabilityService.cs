using Casazen.Core.Features;
using Casazen.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// <see cref="IChannelManagerAvailability"/> (E08/F01/US02/T01, NF-74, NF-80, ADR-023): Octorate's <c>availabilityCheck</c> of the room a property
/// books into, with the quota of the org in mind (D7, ADR-023-R8).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Only a mirrored property is asked</b>, with the connector on (<c>Features:OctorateConnector</c>): <see cref="IOctorateBookingTargets"/>
/// says where it books (a connected account, a confirmed mapping that is not write-blocked, a room). Anything else asks nothing.</item>
/// <item><b>An answer is remembered for 60 seconds</b> for the property, its room and the dates (<see cref="AnswerMemory"/>): the guest who
/// tries again, the host who goes back and forth over the same dates and a burst of requests cost one call. What went wrong is not
/// remembered. Each API instance has its own memory.</item>
/// <item><b>Never a failure of its own</b>: a call that fails (<see cref="OctorateException"/>: no answer, 429, 5xx, a refusal of the token, a room
/// Octorate does not know), takes longer than <see cref="Budget"/> (the checkout of a guest does not wait for the 30 seconds the client allows) or
/// cannot be made at all gives <see cref="ChannelManagerAnswer.Unanswered"/>. A cancelled request goes up, as it is not Octorate's.</item>
/// <item><b>For the org of the property</b>: the call is made inside <see cref="IOctorateOrgScope.Use"/> of that org, as every Octorate call.
/// The logs carry ids and codes only.</item>
/// </list>
/// </remarks>
public sealed class ChannelManagerAvailabilityService(
    IOctorateBookingTargets targets,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    IFeatureFlags featureFlags,
    IMemoryCache cache,
    ILogger<ChannelManagerAvailabilityService> logger) : IChannelManagerAvailability
{
    /// <summary>How long an answer of Octorate is remembered for a property, a room and some dates.</summary>
    public static readonly TimeSpan AnswerMemory = TimeSpan.FromSeconds(60);

    /// <summary>The longest the lookup of the target and the call take together: past it the answer is "could not be asked".</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    public async Task<ChannelManagerAvailability> CheckAsync(
        Guid propertyId,
        Guid orgId,
        DateOnly from,
        DateOnly to,
        int guests,
        CancellationToken ct)
    {
        // Dates that are not a stay have nothing to ask; the connector off asks nothing, as everything else of the connector.
        if (to <= from || !featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return ChannelManagerAvailability.NotMirrored;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);
        try
        {
            var lookup = await targets.LookupAsync(propertyId, budget.Token);
            if (lookup.Target is not { } target)
            {
                // Not mirrored: nothing to ask. Mirrored with no room chosen: nothing can be asked until the host chooses it.
                return lookup.Gap == OctorateTargetGap.NotMirrored
                    ? ChannelManagerAvailability.NotMirrored
                    : ChannelManagerAvailability.Unanswered;
            }

            var key = new AnswerKey(propertyId, target.AccommodationId, target.ProductId, from, to);
            if (cache.TryGetValue(key, out OctorateAvailabilityResult? remembered) && remembered is not null)
                return AnswerOf(remembered);

            OctorateAvailabilityResult answer;
            using (orgScope.Use(orgId))
                answer = await client.CheckAvailabilityAsync(target, from, to, Math.Max(1, guests), budget.Token);

            cache.Set(key, answer, AnswerMemory);
            return AnswerOf(answer);
        }
        catch (OctorateException ex)
        {
            logger.LogWarning(
                "Availability of property {PropertyId} on the channel manager could not be asked ({Code}, HTTP {Status}): advice only",
                propertyId,
                ex.Code,
                ex.OctorateStatusCode);
            return ChannelManagerAvailability.Unanswered;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "Availability of property {PropertyId} on the channel manager took more than {Seconds} seconds: advice only",
                propertyId,
                Budget.TotalSeconds);
            return ChannelManagerAvailability.Unanswered;
        }
        catch (ArgumentException ex)
        {
            // An id of the mapping the client refuses to put in an address: nothing can be asked for this property.
            logger.LogWarning(ex, "Availability of property {PropertyId} on the channel manager cannot be asked: advice only", propertyId);
            return ChannelManagerAvailability.Unanswered;
        }
    }

    private static ChannelManagerAvailability AnswerOf(OctorateAvailabilityResult answer) =>
        answer.Available
            ? new ChannelManagerAvailability(ChannelManagerAnswer.Available)
            : new ChannelManagerAvailability(ChannelManagerAnswer.NotAvailable, answer.Reason);

    private sealed record AnswerKey(Guid PropertyId, string AccommodationId, string ProductId, DateOnly From, DateOnly To);
}
