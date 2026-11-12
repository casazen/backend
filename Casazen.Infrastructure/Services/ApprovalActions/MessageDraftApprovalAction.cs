using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services.ApprovalActions;

/// <summary>
/// Approving the draft of a message (<see cref="ApprovalItemTypes.MessageDraft"/>, E05/F04/US01/T02, NF-68): the final text goes to the conversation of the item
/// through <see cref="IMessageReplyService.ReplyAsync"/>, as the host's own message (the guest sees no AI disclosure, NF-67): a message the host did not edit is the
/// assistant's (<see cref="MessageOrigin.Assistant"/>, the situation as the rule code), one the host edited is the person's (<see cref="MessageOrigin.Person"/>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Once.</b> The key of the send is the item's (<see cref="ApprovalQueueRules.SendKeyOf"/>), so a second approval (a double click, two hosts, a retry after a
/// timeout) finds the message already there and sends nothing. A send whose outcome is not known (<c>ManualVerify</c>) counts as done: it is the same message, checked on
/// the channel and never sent again. A message the channel <b>refused</b> (<c>Error</c>) did not go out, but the same key would replay the refusal for ever, so the
/// retry of such an item sends with the key of the next attempt, up to <see cref="ApprovalQueueRules.MaxSendAttempts"/>.</item>
/// <item><b>The scope</b> is the whole org of the item: who may approve it, and reaches its property, was decided by <see cref="IApprovalQueueService"/> before this
/// runs, and the reply service checks the conversation, the mapping of its property and the connector as it does for anyone.</item>
/// <item><b>Failures are codes</b> (<see cref="ApprovalActionResult.Failed"/>): the conversation is gone or closed, the mapping is not confirmed, the connector is off, the
/// rules of the channel refuse the text, the channel manager refuses it or is not reachable. The item stays pending and the host edits and tries again; a failure nobody
/// foresaw is not turned into a code here, it reaches the service.</item>
/// <item><b>Personal data.</b> The text is never logged: the logs carry the item, the org and the state.</item>
/// </list>
/// </remarks>
public sealed class MessageDraftApprovalAction(IMessageReplyService replies, ILogger<MessageDraftApprovalAction> logger) : IApprovalOriginAction
{
    public string ItemType => ApprovalItemTypes.MessageDraft;

    public string OriginAction => ApprovalOriginActions.SendGuestMessage;

    public async Task<ApprovalActionResult> ExecuteAsync(ApprovalQueueItem item, string finalText, string userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalText);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        if (!MessageThreadKey.TryParse(item.ThreadKey, out var key))
            return ApprovalActionResult.Failed(MessageThreadKey.InvalidCode);

        var edited = item.EditedContentText is not null;
        var origin = edited ? MessageOrigin.Person : MessageOrigin.Assistant;
        var ruleCode = !edited && !string.IsNullOrWhiteSpace(item.SituationCode) ? item.SituationCode : null;
        var scope = new HostScope(item.OrgId);

        try
        {
            for (var attempt = 1; attempt <= ApprovalQueueRules.MaxSendAttempts; attempt++)
            {
                var result = await replies.ReplyAsync(scope, key, finalText, ApprovalQueueRules.SendKeyOf(item.Id, attempt), userId, origin, ruleCode, ct);

                // The same key found a message of this item that the channel refused: it did not go out, and the next key sends it again.
                if (result is { Replayed: true, State: OtaMessageDeliveryState.Error })
                    continue;

                if (result.State == OtaMessageDeliveryState.Error)
                {
                    logger.LogWarning(
                        "Approval item {ItemId} of org {OrgId}: the message was refused ({Code})",
                        item.Id, item.OrgId, result.ErrorCode);
                    return ApprovalActionResult.Failed(result.ErrorCode ?? "message_not_sent");
                }

                logger.LogInformation(
                    "Approval item {ItemId} of org {OrgId}: the message is {State}{Replayed}",
                    item.Id, item.OrgId, result.State, result.Replayed ? " (already there)" : string.Empty);
                return ApprovalActionResult.Executed(result.State.ToString());
            }
        }
        catch (DomainException ex)
        {
            logger.LogWarning("Approval item {ItemId} of org {OrgId}: the message was not sent ({Code})", item.Id, item.OrgId, ex.Code);
            return ApprovalActionResult.Failed(ex.Code);
        }
        catch (NotFoundException ex)
        {
            logger.LogWarning("Approval item {ItemId} of org {OrgId}: the conversation was not found", item.Id, item.OrgId);
            return ApprovalActionResult.Failed(ex.Code ?? MessageHubService.ThreadNotFoundCode);
        }
        catch (OctorateException ex)
        {
            logger.LogWarning("Approval item {ItemId} of org {OrgId}: the channel manager did not take the message ({Code})", item.Id, item.OrgId, ex.Code);
            return ApprovalActionResult.Failed(ex.Code);
        }

        logger.LogWarning(
            "Approval item {ItemId} of org {OrgId}: the channel refused the message {Attempts} times",
            item.Id, item.OrgId, ApprovalQueueRules.MaxSendAttempts);
        return ApprovalActionResult.Failed(ApprovalQueueRules.SendAttemptsExhaustedCode);
    }

    /// <summary>A message needs no undoing when its draft is rejected: nothing was sent.</summary>
    public Task OnRejectedAsync(ApprovalQueueItem item, string reason, string userId, CancellationToken ct) => Task.CompletedTask;
}
