using Casazen.Core.Services;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// Publishes one property to Octorate and to the channels the host asked for (E02/F03/US01/T02, NF-27, ADR-027), queued by
/// <see cref="IContentPushService.PublishAsync"/> after its guards. It runs under <see cref="IOctorateOrgScope.Use"/> of the property's org (the
/// client takes no org) and calls <see cref="IContentPushService.RunAsync"/>, which creates the property on Octorate when it is new, pushes the content
/// and the photos, and records the result for each channel. Behind <c>Features:OctorateConnector</c> (the service does nothing while it is off).
/// </summary>
/// <remarks>
/// <para>One run for each property at a time: a second publication of the same property waits for the first (the lock holds for
/// <see cref="LockTimeoutSeconds"/> seconds), so two runs never push the same content or create the same unit twice.</para>
/// <para><b>No automatic retry.</b> Octorate has no idempotency key: a creation whose answer was lost cannot be repeated blindly, because the refresh
/// token it carries comes once. A failure is recorded on the channel rows (their code and a «To do») and the host publishes again from the screen; the
/// job stays failed in the Hangfire dashboard with the type of the error, never with the data of the listing. What the host can fix (a refusal of the
/// data, a missing requirement) ends the job normally.</para>
/// </remarks>
public class ListingPublishJob(IContentPushService push, ILogger<ListingPublishJob> logger)
{
    /// <summary>The seconds a run waits for the previous run of the same property: the lock wait of an on-demand job (`JobLockTimeouts.DefaultSeconds`).</summary>
    public const int LockTimeoutSeconds = 300;

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    [DisableConcurrentExecution("ListingPublishJob.RunAsync:{0}", LockTimeoutSeconds)]
    public async Task RunAsync(
        Guid propertyId,
        string[] channels,
        bool overwrite,
        string userId,
        string[] tokenRoles,
        CancellationToken cancellationToken)
    {
        var roles = new HashSet<string>(tokenRoles ?? [], StringComparer.Ordinal);
        await push.RunAsync(propertyId, channels ?? [], overwrite, userId, roles, cancellationToken);
        logger.LogInformation("Listing publication of property {PropertyId} ended", propertyId);
    }
}
