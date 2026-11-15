using Casazen.Core.Features;
using Casazen.Core.Services;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// The hourly pass over the check-in links that wait (E23/F01/US01/T01, T02, NF-122): a link of a stay with no e-mail address that has not reached the guest because the
/// chat of the portal is not licensed yet, the booking has no conversation yet, no way to the guest was on, the contact of the guest was refused (the static IP addresses) or a
/// failure was worth a retry, is tried again when the thing it waits for may have changed (<see cref="ICheckInLinkDeliveryService.RetryWaitingAsync"/>), and a row whose link
/// is no longer useful (the guest completed the check-in, the booking was cancelled, the link expired) is ended with its «To do».
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Flags.</b> Scheduled only while <c>Features:OctorateConnector</c> and at least one of <c>Features:PortalChatCheckInLink</c> and <c>Features:GuestContactInfo</c> are on
/// (the registration removes it otherwise), and a run with them off does nothing: a job writes only while the key and the flag of its module are on.</item>
/// <item><b>When.</b> At minute 30 of every hour (UTC): the daily <c>guest-checkin-send</c> runs at 08:00 and the sync of the chat passes every ten minutes, so a link that waited
/// for the chat is tried soon after the listing read it again, and the two jobs never start together.</item>
/// <item><b>No storm.</b> A row is tried again only when what it waits for changed, at most once an hour, and the contact of the guest at most once a day after a refusal.</item>
/// <item><b>Failures.</b> One row's failure never stops the others; the first is thrown when the pass is over, so the run shows as failed. No retry by Hangfire: the next run, an hour
/// later, looks at the same rows.</item>
/// </list>
/// </remarks>
public class CheckInLinkDeliveryRetryJob(
    ICheckInLinkDeliveryService deliveries,
    IGuestCheckInService checkInService,
    IFeatureFlags featureFlags,
    ILogger<CheckInLinkDeliveryRetryJob> logger)
{
    public const string RecurringJobId = "checkin-link-delivery-retry";

    /// <summary>At minute 30 of every hour (UTC).</summary>
    public const string Cron = "30 * * * *";

    /// <summary>The module is open: the connector and at least one of the two ways to the guest.</summary>
    public static bool IsEnabled(IFeatureFlags flags)
    {
        ArgumentNullException.ThrowIfNull(flags);
        return flags.IsEnabled(FeatureFlags.OctorateConnector)
               && (flags.IsEnabled(FeatureFlags.PortalChatCheckInLink) || flags.IsEnabled(FeatureFlags.GuestContactInfo));
    }

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled(featureFlags))
        {
            logger.LogInformation("Check-in link delivery retry: the flags of the portal chat and of the contact of the guest are off, nothing done");
            return;
        }

        var tried = await deliveries.RetryWaitingAsync(checkInService.IssueLinkAsync, cancellationToken);
        logger.LogInformation("Check-in link delivery retry: {Tried} links tried again", tried);
    }
}
