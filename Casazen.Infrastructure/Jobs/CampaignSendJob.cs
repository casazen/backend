using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// Hangfire job for scheduled campaign sends (NF-24, AC-NF24-6).
/// Re-evaluates segment recipients at send time to respect current consent/suppression status. It is the only way a campaign leaves, so it
/// is closed with the module: while <c>Features:CrmMarketing</c> is off (E07/F06/US01/T01) the service sends nothing and the job ends without
/// a retry (<see cref="ICampaignService.SendCampaignAsync"/>).
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 1800)]
public class CampaignSendJob(ICampaignService campaignService)
{
    public const int MaxAttempts = 3;

    /// <summary>
    /// Send a scheduled campaign. Called by Hangfire at the scheduled time.
    /// </summary>
    /// <param name="campaignId">Campaign ID to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [AutomaticRetry(Attempts = MaxAttempts, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public async Task SendCampaignAsync(Guid campaignId, CancellationToken cancellationToken = default)
    {
        await campaignService.SendCampaignAsync(campaignId, cancellationToken);
    }
}
