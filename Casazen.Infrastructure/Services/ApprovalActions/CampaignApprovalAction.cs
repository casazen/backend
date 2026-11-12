using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services.ApprovalActions;

/// <summary>
/// Approving a campaign (<see cref="ApprovalItemTypes.Campaign"/>, E05/F04/US01/T02, NF-68, NF-24): the campaign <c>PendingApproval</c> becomes <c>Approved</c> (no
/// production code set that state before: <c>ScheduleAsync</c> needed it and nothing gave it), with the subject and the body the host edited in the queue, and when
/// the campaign already has a time it is scheduled for it. A rejection returns the campaign to a draft.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The text of the item</b> is the subject on one line, a blank line, and the body (<see cref="ApprovalQueueRules.TryParseCampaignText"/>): it is applied to the
/// campaign only when the host edited it, so an unedited campaign keeps exactly the subject and the body it was submitted with.</item>
/// <item><b>Once, and again after a failure.</b> The two steps are safe to repeat: <see cref="ICampaignService.MarkApprovedAsync"/> finds a campaign already approved or
/// scheduled and leaves it, so a retry after a scheduling that failed schedules and does not approve twice.</item>
/// <item><b>The time.</b> A campaign with no time is approved and waits for the host to schedule it (<c>Approved</c>); one whose time passed while it waited is approved
/// and not scheduled, for the host to pick another (the service drops the time).</item>
/// <item><b>The module.</b> With <c>Features:CrmMarketing</c> off nothing is approved or scheduled: the approval fails with <see cref="ModuleOffCode"/> and the item waits (a rejection and the
/// expiry still return the campaign to a draft: they only undo).</item>
/// <item><b>Sending is not here.</b> The send of the e-mails is the job of the schedule (<c>CampaignSendJob</c>), behind <c>Features:CrmMarketing</c>: approving only lets
/// the campaign be scheduled and never sends one.</item>
/// </list>
/// </remarks>
public sealed class CampaignApprovalAction(ICampaignService campaigns, IFeatureFlags featureFlags, ILogger<CampaignApprovalAction> logger) : IApprovalOriginAction
{
    /// <summary>
    /// The code of an approval refused because the module of the campaigns is closed (<c>Features:CrmMarketing</c> is off): the item stays pending and is approved when the module is
    /// on again.
    /// </summary>
    public const string ModuleOffCode = "crm_marketing_disabled";

    /// <summary>The state of the answer for a campaign that was approved and scheduled.</summary>
    public const string ScheduledState = "Scheduled";

    /// <summary>The state of the answer for a campaign that was approved and has no time yet.</summary>
    public const string ApprovedState = "Approved";

    public string ItemType => ApprovalItemTypes.Campaign;

    public string OriginAction => ApprovalOriginActions.ApproveCampaign;

    public async Task<ApprovalActionResult> ExecuteAsync(ApprovalQueueItem item, string finalText, string userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalText);

        // The campaigns are behind Features:CrmMarketing (their endpoints answer 404 while it is off): the queue is another door to the same campaigns, so it does not approve or
        // schedule one while the module is closed. The item stays pending with the code and the decision is taken when the module is on.
        if (!featureFlags.IsEnabled(FeatureFlags.CrmMarketing))
        {
            logger.LogWarning(
                "Approval item {ItemId} of org {OrgId}: campaign {CampaignId} not approved, Features:CrmMarketing is off",
                item.Id, item.OrgId, item.ItemId);
            return ApprovalActionResult.Failed(ModuleOffCode);
        }

        string? subject = null;
        string? body = null;
        if (item.EditedContentText is not null && !ApprovalQueueRules.TryParseCampaignText(finalText, out subject, out body))
            return ApprovalActionResult.Failed(ApprovalQueueRules.EditInvalidCode);

        try
        {
            var campaign = await campaigns.MarkApprovedAsync(item.ItemId, item.OrgId, subject, body, ct);
            if (campaign.Status == CampaignStatus.Scheduled)
                return ApprovalActionResult.Executed(ScheduledState);

            if (campaign.ScheduledAt is not { } scheduledAt)
                return ApprovalActionResult.Executed(ApprovedState);

            await campaigns.ScheduleAsync(item.ItemId, scheduledAt, item.OrgId, ct);
            logger.LogInformation("Approval item {ItemId} of org {OrgId}: campaign {CampaignId} approved and scheduled", item.Id, item.OrgId, item.ItemId);
            return ApprovalActionResult.Executed(ScheduledState);
        }
        catch (DomainException ex)
        {
            logger.LogWarning(
                "Approval item {ItemId} of org {OrgId}: campaign {CampaignId} could not be approved ({Code})",
                item.Id, item.OrgId, item.ItemId, ex.Code);
            return ApprovalActionResult.Failed(ex.Code);
        }
    }

    public Task OnRejectedAsync(ApprovalQueueItem item, string reason, string userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        return campaigns.ReturnToDraftAsync(item.ItemId, item.OrgId, ct);
    }
}
