using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// Hangfire job for scheduled campaign sends (NF-24, AC-NF24-6).
/// Re-evaluates segment recipients at send time to respect current consent/suppression status.
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
