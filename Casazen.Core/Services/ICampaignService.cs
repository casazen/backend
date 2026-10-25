using Casazen.Core.Authorization;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Service for marketing campaigns and guest segmentation (NF-24, ADR-014).
/// Handles campaign lifecycle, recipient selection with consent/suppression filtering,
/// and scheduled sends.
/// </summary>
public interface ICampaignService
{
    /// <summary>
    /// Creates a draft campaign (AC-NF24-4).
    /// </summary>
    /// <param name="orgId">Organization ID.</param>
    /// <param name="segmentId">Segment ID for recipient selection.</param>
    /// <param name="subject">Email subject line.</param>
    /// <param name="body">Email body (HTML).</param>
    /// <param name="isAiGenerated">True if content was AI-generated.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Created campaign.</returns>
    Task<Campaign> CreateDraftAsync(
        Guid orgId,
        Guid segmentId,
        string subject,
        string body,
        bool isAiGenerated,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits a draft campaign for approval (AC-NF24-5).
    /// Campaign enters approval queue.
    /// </summary>
    /// <param name="campaignId">Campaign ID.</param>
    /// <param name="orgId">Organization ID for tenant verification.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SubmitForApprovalAsync(Guid campaignId, Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Schedules an approved campaign for future send (AC-NF24-6).
    /// Enqueues a Hangfire job for the scheduled time.
    /// </summary>
    /// <param name="campaignId">Campaign ID.</param>
    /// <param name="scheduledAt">When to send the campaign.</param>
    /// <param name="orgId">Organization ID for tenant verification.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ScheduleAsync(Guid campaignId, DateTime scheduledAt, Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets recipients for a segment with consent and suppression filtering (AC-NF24-2, AC-NF24-3, AC-NF24-11).
    /// Re-evaluated at send time to respect current consent status.
    /// </summary>
    /// <param name="segmentId">Segment ID.</param>
    /// <param name="orgId">Organization ID.</param>
    /// <param name="scope">Host scope for perimeter enforcement (ADR-014-R4).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of eligible guest email addresses.</returns>
    Task<List<string>> GetRecipientsAsync(
        Guid segmentId,
        Guid orgId,
        HostScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a campaign immediately (AC-NF24-7, AC-NF24-8, AC-NF24-9).
    /// Re-evaluates segment, filters by consent/suppression, sends via IEmailService.
    /// Includes UTM params and one-click unsubscribe link. No tracking pixel.
    /// </summary>
    /// <param name="campaignId">Campaign ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendCampaignAsync(Guid campaignId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Previews recipient count for a segment (AC-NF24-3).
    /// Filters by consent and suppression at preview time.
    /// </summary>
    /// <param name="segmentId">Segment ID.</param>
    /// <param name="orgId">Organization ID.</param>
    /// <param name="scope">Host scope for perimeter enforcement.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Count of eligible recipients.</returns>
    Task<int> PreviewCountAsync(
        Guid segmentId,
        Guid orgId,
        HostScope scope,
        CancellationToken cancellationToken = default);
}
