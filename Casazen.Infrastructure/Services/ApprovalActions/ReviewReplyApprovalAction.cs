using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services.ApprovalActions;

/// <summary>
/// Approving the draft of a reply to a review (<see cref="ApprovalItemTypes.ReviewReply"/>, E05/F06/US01/T03, NF-69): the final text is the public reply of the host, sent <b>only here</b>, after a person
/// approved it (the assistant writes the draft and the queue holds it; nothing leaves before this runs, AC-NF69-4). A review of a channel (Airbnb) is answered through
/// <see cref="IOtaReviewService.ReplyAsync"/>, a review of the booking site through <see cref="IHostReviewService.ReplyAsync"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Once.</b> A review of a channel is answered with the key of the item and the text (<see cref="ApprovalQueueRules.SendKeyOf"/>: letters, digits and dashes, the one form the service of the reviews
/// accepts), so a second approval of the same text (a double click, two hosts, a retry after a timeout) finds the reply already there and sends nothing; a reply whose outcome is not known
/// (<c>ManualVerify</c>) counts as done, is checked on the channel by the next read and is never sent again. A reply the channel <b>refused</b> did not go out, but the same key would replay the
/// refusal for ever, so the retry of such an item sends with the key of the next attempt, up to <see cref="ApprovalQueueRules.MaxSendAttempts"/>. A review of the booking site is answered once by
/// its own rule: a second reply is the 409 <c>review_reply_already_sent</c>, which the item shows as the code of a failed action.</item>
/// <item><b>The review is the item's.</b> The key of the item has to be the key of a review and its id the id of the item, or nothing is sent: a key of a conversation never makes an approval write
/// a message, and a draft never goes to another review than the one it was written for.</item>
/// <item><b>The scope</b> is the org of the item: who may approve it, and reaches its property, was decided by <see cref="IApprovalQueueService"/> before this runs, and the services of the reviews
/// check the review, the listing of its property and the connector as they do for anyone.</item>
/// <item><b>Failures are codes</b> (<see cref="ApprovalActionResult.Failed"/>): the review is gone or not published, the listing is not confirmed, the connector is off, the channel manager refuses the
/// reply or cannot be reached, a reply is there already. The item stays pending and the host edits and tries again, or rejects it; a failure nobody foresaw is not turned into a code here, it reaches
/// the service.</item>
/// <item><b>Personal data.</b> The text is never logged: the logs carry the item, the org and the state.</item>
/// </list>
/// </remarks>
public sealed class ReviewReplyApprovalAction(
    IOtaReviewService channelReviews,
    IHostReviewService siteReviews,
    ILogger<ReviewReplyApprovalAction> logger) : IApprovalOriginAction
{
    public string ItemType => ApprovalItemTypes.ReviewReply;

    public string OriginAction => ApprovalOriginActions.SendReviewReply;

    public async Task<ApprovalActionResult> ExecuteAsync(ApprovalQueueItem item, string finalText, string userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalText);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        if (!MessageThreadKey.TryParse(item.ThreadKey, out var key) || !key.IsReview || key.Id != item.ItemId)
        {
            logger.LogWarning("Approval item {ItemId} of org {OrgId} does not name the review it answers: nothing is sent", item.Id, item.OrgId);
            return ApprovalActionResult.Failed(MessageThreadKey.InvalidCode);
        }

        try
        {
            return key.Source == MessageThreadSource.OtaReview
                ? await ReplyToChannelReviewAsync(item, key, finalText, userId, ct)
                : await ReplyToSiteReviewAsync(item, key, finalText, ct);
        }
        catch (DomainException ex)
        {
            logger.LogWarning("Approval item {ItemId} of org {OrgId}: the reply was not sent ({Code})", item.Id, item.OrgId, ex.Code);
            return ApprovalActionResult.Failed(ex.Code);
        }
        catch (NotFoundException ex)
        {
            logger.LogWarning("Approval item {ItemId} of org {OrgId}: the review was not found", item.Id, item.OrgId);
            return ApprovalActionResult.Failed(ex.Code ?? OtaReviewErrorCodes.NotFound);
        }
        catch (OctorateException ex)
        {
            logger.LogWarning("Approval item {ItemId} of org {OrgId}: the channel manager did not take the reply ({Code})", item.Id, item.OrgId, ex.Code);
            return ApprovalActionResult.Failed(ex.Code);
        }
    }

    private async Task<ApprovalActionResult> ReplyToChannelReviewAsync(
        ApprovalQueueItem item,
        MessageThreadKey key,
        string finalText,
        string userId,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= ApprovalQueueRules.MaxSendAttempts; attempt++)
        {
            var result = await channelReviews.ReplyAsync(key.Id, finalText, ApprovalQueueRules.SendKeyOf(item.Id, finalText, attempt), userId, ct);

            // The same key found an attempt of this item that the channel refused: it did not go out, and the next key sends it again.
            if (result is { Replayed: true, State: OtaReviewActionState.Error })
                continue;

            if (result.State == OtaReviewActionState.Error)
            {
                logger.LogWarning("Approval item {ItemId} of org {OrgId}: the reply was refused ({Code})", item.Id, item.OrgId, result.ErrorCode);
                return ApprovalActionResult.Failed(result.ErrorCode ?? OtaReviewErrorCodes.ReplyNotDelivered);
            }

            logger.LogInformation(
                "Approval item {ItemId} of org {OrgId}: the reply is {State}{Replayed}",
                item.Id, item.OrgId, result.State, result.Replayed ? " (already there)" : string.Empty);
            return ApprovalActionResult.Executed(result.State.ToString());
        }

        logger.LogWarning("Approval item {ItemId} of org {OrgId}: the channel refused the reply {Attempts} times", item.Id, item.OrgId, ApprovalQueueRules.MaxSendAttempts);
        return ApprovalActionResult.Failed(ApprovalQueueRules.SendAttemptsExhaustedCode);
    }

    private async Task<ApprovalActionResult> ReplyToSiteReviewAsync(ApprovalQueueItem item, MessageThreadKey key, string finalText, CancellationToken ct)
    {
        await siteReviews.ReplyAsync(key.Id, finalText, ct);
        logger.LogInformation("Approval item {ItemId} of org {OrgId}: the reply to the review of the booking site is published", item.Id, item.OrgId);
        return ApprovalActionResult.Executed(OtaReviewActionState.Sent.ToString());
    }

    /// <summary>A reply needs no undoing when its draft is rejected: nothing was sent.</summary>
    public Task OnRejectedAsync(ApprovalQueueItem item, string reason, string userId, CancellationToken ct) => Task.CompletedTask;
}
