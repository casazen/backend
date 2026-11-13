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
    /// Approves a campaign that waits for the decision of the approval queue (E05/F04/US01/T02, NF-68): <c>PendingApproval</c> becomes <c>Approved</c>, with the
    /// subject and the body the host edited in the queue when it edited them (<paramref name="subject"/> and <paramref name="body"/> both, or neither). It is
    /// the one place that sets <c>Approved</c> (<see cref="ScheduleAsync"/> needs it). A time that was asked for and has passed while the campaign waited is
    /// dropped (<c>ScheduledAt</c> is <c>null</c> in the answer: approved, to be scheduled again by the host). Repeating it is safe: a campaign already
    /// <c>Approved</c> or <c>Scheduled</c> is returned as it is but for the edit of this call, which is applied (the text the host approved last is the one that goes out), so a
    /// retry after a failed scheduling finds the work done.
    /// 422 <c>campaign_not_found</c>; 409 <c>campaign_not_pending_approval</c> for a draft or a campaign already sent.
    /// </summary>
    /// <param name="campaignId">Campaign ID.</param>
    /// <param name="orgId">Organization ID for tenant verification.</param>
    /// <param name="subject">The edited subject line, or <c>null</c> to keep the campaign's.</param>
    /// <param name="body">The edited body, or <c>null</c> to keep the campaign's.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The campaign as it is after the approval.</returns>
    Task<Campaign> MarkApprovedAsync(
        Guid campaignId,
        Guid orgId,
        string? subject,
        string? body,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a campaign whose approval was rejected (or whose item expired) to a draft (E05/F04/US01/T02): <c>PendingApproval</c> becomes <c>Draft</c>, so the host can change it and
    /// submit it again. An approval that went halfway (the campaign <c>Approved</c> or <c>Scheduled</c> while its item was still waiting: the item could not be saved, or the schedule
    /// failed) is undone too, with its time (<c>ScheduledAt</c> is <c>null</c>): a rejected campaign is never left approved or scheduled, and a send job still waiting finds a draft and
    /// sends nothing. Repeating it is safe, and a campaign in any other state (already a draft, sent, or gone) is left as it is.
    /// </summary>
    Task ReturnToDraftAsync(Guid campaignId, Guid orgId, CancellationToken cancellationToken = default);

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
    /// While <c>Features:CrmMarketing</c> is off (E07/F06/US01/T01) it sends nothing: it logs a warning, returns without failing (the Hangfire
    /// job is not retried) and puts a scheduled campaign back to <c>Approved</c>, so the host can schedule it again once the module is on.
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
