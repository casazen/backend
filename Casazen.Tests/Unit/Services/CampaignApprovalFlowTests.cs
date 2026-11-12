using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Services.ApprovalActions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E05/F04/US01/T02 (NF-68, NF-24): a campaign goes through the approval queue and comes out approved. Before this task no production code ever set
/// <c>CampaignStatus.Approved</c>, so approving the item of a campaign in the queue never approved the campaign and <c>ScheduleAsync</c> could never be called. The real
/// campaign service, the real queue and the real action over EF InMemory.
/// </summary>
public class CampaignApprovalFlowTests : IDisposable
{
    private readonly ApprovalQueueRig _rig = new();
    private readonly Mock<IBackgroundJobClient> _hangfire = new();
    private readonly TestFeatureFlags _flags = new(FeatureFlags.CrmMarketing);
    private ApprovalOriginActionRegistry _registry = null!;
    private Guid _segmentId;

    public void Dispose() => _rig.Dispose();

    private async Task<(CampaignService Campaigns, ApprovalQueueService Queue)> BuildAsync()
    {
        await _rig.SeedAsync();
        _segmentId = Guid.NewGuid();
        _rig.Db.GuestSegments.Add(new GuestSegment
        {
            Id = _segmentId,
            OrgId = _rig.OrgId,
            Name = "Tutti",
            ConditionJson = JsonSerializer.Serialize(new Condition { Version = "1.0", Groups = new List<ConditionGroup>() }),
        });
        await _rig.Db.SaveChangesAsync();

        // The campaign service needs the queue, and the action of the queue needs the campaign service: the registry asks for the action when it is first used.
        CampaignService? campaigns = null;
        _registry = new ApprovalOriginActionRegistry(() => [new CampaignApprovalAction(campaigns!, _flags, NullLogger<CampaignApprovalAction>.Instance)]);
        var queue = _rig.Service(_registry);
        campaigns = new CampaignService(
            _rig.Db,
            new ConditionEvaluator(NullLogger<ConditionEvaluator>.Instance),
            new SuppressionService(_rig.Db, CrmTestIndex.Create(), NullLogger<SuppressionService>.Instance),
            Mock.Of<Casazen.Infrastructure.External.IEmailService>(),
            new MarketingUnsubscribeLinks(
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [MarketingUnsubscribeLinks.SecretVariable] = "test-signed-link-secret-0123456789abcdef",
                    ["App:ApiBaseUrl"] = "https://api.casazen.test",
                }).Build(),
                new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://app.casazen.test" })),
                TimeProvider.System,
                CrmTestIndex.Create()),
            _hangfire.Object,
            _flags,
            _rig.Clock,
            NullLogger<CampaignService>.Instance,
            queue);
        return (campaigns, queue);
    }

    /// <summary>
    /// Trust rule (nothing leaves unapproved): the queue is the way a campaign becomes <c>Approved</c> and so can be scheduled. Submitted, it waits as an item with its action; approved
    /// with an edited text, the campaign takes the edit and is approved (not scheduled: it has no time); only then the host can schedule it.
    /// </summary>
    [Fact]
    public async Task ACampaignSubmittedToTheQueue_IsApprovedByApprovingItsItem_AndCanThenBeScheduled()
    {
        var (campaigns, queue) = await BuildAsync();
        var draft = await campaigns.CreateDraftAsync(_rig.OrgId, _segmentId, "Oggetto", "<p>Testo</p>", false);

        await campaigns.SubmitForApprovalAsync(draft.Id, _rig.OrgId);

        var item = await _rig.Db.ApprovalQueueItems.AsNoTracking().SingleAsync();
        Assert.Equal((ApprovalItemTypes.Campaign, draft.Id), (item.ItemType, item.ItemId));
        Assert.Equal(ApprovalQueueRules.CampaignTextOf("Oggetto", "<p>Testo</p>"), item.ContentText);
        Assert.Equal((ApprovalOriginActions.ApproveCampaign, ApprovalOriginActionStatus.Pending), (item.OriginAction, item.OriginActionStatus));
        Assert.Equal(CampaignStatus.PendingApproval, (await _rig.Db.Campaigns.AsNoTracking().SingleAsync()).Status);
        // The alert and the "To do" come with the item (before, the campaign path bypassed the queue service and told nobody).
        Assert.Single(await _rig.Db.InAppNotifications.AsNoTracking().ToListAsync(), n => n.UserId == ApprovalQueueRig.Owner);
        Assert.NotNull(item.ActivityId);
        var notYet = await Assert.ThrowsAsync<DomainRuleException>(() => campaigns.ScheduleAsync(draft.Id, ApprovalQueueRig.Noon.AddDays(1), _rig.OrgId));
        Assert.Equal("campaign_not_approved", notYet.Code);

        await queue.EditAsync(_rig.OrgWide, item.Id, "Oggetto corretto\n\n<p>Testo corretto</p>", ApprovalQueueRig.Owner);
        var decision = await queue.ApproveAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.Owner);

        Assert.Equal("Approved", decision.ActionState);
        var approved = await _rig.Db.Campaigns.AsNoTracking().SingleAsync();
        Assert.Equal(CampaignStatus.Approved, approved.Status);
        Assert.Equal(("Oggetto corretto", "<p>Testo corretto</p>"), (approved.Subject, approved.Body));

        // What was impossible before: the approved campaign is scheduled.
        var at = ApprovalQueueRig.Noon.AddDays(3);
        await campaigns.ScheduleAsync(draft.Id, at, _rig.OrgId);
        var scheduled = await _rig.Db.Campaigns.AsNoTracking().SingleAsync();
        Assert.Equal((CampaignStatus.Scheduled, at), (scheduled.Status, scheduled.ScheduledAt));
    }

    /// <summary>
    /// Module rule: the campaigns are closed with <c>Features:CrmMarketing</c> (the endpoints answer 404), and the queue is another door to them: with the module off an approval
    /// does not approve or schedule a campaign. It fails with its code and the item stays pending (409, <c>originActionError</c>); with the module on again the same item is approved.
    /// </summary>
    [Fact]
    public async Task ApprovingACampaign_WithTheModuleOff_FailsWithItsCode_AndTheItemStaysPending()
    {
        var (campaigns, queue) = await BuildAsync();
        var draft = await campaigns.CreateDraftAsync(_rig.OrgId, _segmentId, "Oggetto", "<p>Testo</p>", false);
        (await _rig.Db.Campaigns.SingleAsync()).ScheduledAt = ApprovalQueueRig.Noon.AddDays(2);
        await _rig.Db.SaveChangesAsync();
        await campaigns.SubmitForApprovalAsync(draft.Id, _rig.OrgId);
        var item = await _rig.Db.ApprovalQueueItems.AsNoTracking().SingleAsync();
        _flags.Set(FeatureFlags.CrmMarketing, false);

        var failure = await Assert.ThrowsAsync<DomainConflictException>(() => queue.ApproveAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.Owner));

        Assert.Equal("approval_origin_action_failed", failure.Code);
        Assert.Equal("crm_marketing_disabled", failure.Extensions["originActionError"]);
        Assert.Equal(CampaignApprovalAction.ModuleOffCode, failure.Extensions["originActionError"]);
        var stored = await _rig.Db.ApprovalQueueItems.AsNoTracking().SingleAsync();
        Assert.Equal((ApprovalStatus.Pending, ApprovalOriginActionStatus.Failed, "crm_marketing_disabled"), (stored.Status, stored.OriginActionStatus, stored.OriginActionError));
        Assert.Equal(CampaignStatus.PendingApproval, (await _rig.Db.Campaigns.AsNoTracking().SingleAsync()).Status);
        _hangfire.Verify(j => j.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()), Times.Never);

        _flags.Set(FeatureFlags.CrmMarketing, true);
        var decision = await queue.ApproveAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.Owner);

        Assert.Equal("Scheduled", decision.ActionState);
        Assert.Equal(CampaignStatus.Scheduled, (await _rig.Db.Campaigns.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task ACampaignWithATime_IsApprovedAndScheduledByTheApproval()
    {
        var (campaigns, queue) = await BuildAsync();
        var draft = await campaigns.CreateDraftAsync(_rig.OrgId, _segmentId, "Oggetto", "<p>Testo</p>", false);
        var at = ApprovalQueueRig.Noon.AddDays(2);
        (await _rig.Db.Campaigns.SingleAsync()).ScheduledAt = at;
        await _rig.Db.SaveChangesAsync();
        await campaigns.SubmitForApprovalAsync(draft.Id, _rig.OrgId);
        var item = await _rig.Db.ApprovalQueueItems.AsNoTracking().SingleAsync();

        var decision = await queue.ApproveAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.Owner);

        Assert.Equal("Scheduled", decision.ActionState);
        var scheduled = await _rig.Db.Campaigns.AsNoTracking().SingleAsync();
        Assert.Equal((CampaignStatus.Scheduled, at), (scheduled.Status, scheduled.ScheduledAt));
        Assert.Equal(ApprovalStatus.Approved, (await _rig.Db.ApprovalQueueItems.AsNoTracking().SingleAsync()).Status);
        _hangfire.Verify(j => j.Create(It.Is<Hangfire.Common.Job>(job => job.Type == typeof(CampaignSendJob)), It.IsAny<Hangfire.States.IState>()), Times.Once);
    }

    [Fact]
    public async Task ACampaignWhoseTimePassedWhileItWaited_IsApprovedAndNotScheduled()
    {
        var (campaigns, queue) = await BuildAsync();
        var draft = await campaigns.CreateDraftAsync(_rig.OrgId, _segmentId, "Oggetto", "<p>Testo</p>", false);
        (await _rig.Db.Campaigns.SingleAsync()).ScheduledAt = ApprovalQueueRig.Noon.AddHours(1);
        await _rig.Db.SaveChangesAsync();
        await campaigns.SubmitForApprovalAsync(draft.Id, _rig.OrgId);
        var item = await _rig.Db.ApprovalQueueItems.AsNoTracking().SingleAsync();
        _rig.Clock.Advance(TimeSpan.FromHours(5));

        var decision = await queue.ApproveAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.Owner);

        Assert.Equal("Approved", decision.ActionState);
        var approved = await _rig.Db.Campaigns.AsNoTracking().SingleAsync();
        Assert.Equal(CampaignStatus.Approved, approved.Status);
        Assert.Null(approved.ScheduledAt);
        _hangfire.Verify(j => j.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()), Times.Never);
    }

    [Fact]
    public async Task ARejection_ReturnsTheCampaignToADraft_ThatCanBeSubmittedAgain()
    {
        var (campaigns, queue) = await BuildAsync();
        var draft = await campaigns.CreateDraftAsync(_rig.OrgId, _segmentId, "Oggetto", "<p>Testo</p>", false);
        await campaigns.SubmitForApprovalAsync(draft.Id, _rig.OrgId);
        var first = await _rig.Db.ApprovalQueueItems.AsNoTracking().SingleAsync();

        await queue.RejectAsync(_rig.OrgWide, first.Id, ApprovalQueueRig.Owner, "Troppo promozionale");

        Assert.Equal(CampaignStatus.Draft, (await _rig.Db.Campaigns.AsNoTracking().SingleAsync()).Status);
        _rig.Clock.Advance(TimeSpan.FromMinutes(1));
        await campaigns.SubmitForApprovalAsync(draft.Id, _rig.OrgId);
        var items = await _rig.Db.ApprovalQueueItems.AsNoTracking().OrderBy(i => i.CreatedAt).ToListAsync();
        Assert.Equal(2, items.Count);
        Assert.Equal(
            [ApprovalStatus.Rejected, ApprovalStatus.Pending],
            items.Select(i => i.Status));
        Assert.Equal("Troppo promozionale", items[0].RejectionReason);
    }

    [Fact]
    public async Task ARetryAfterTheCampaignWasApproved_FindsTheWorkDoneAndSchedulesOnce()
    {
        var (campaigns, queue) = await BuildAsync();
        var draft = await campaigns.CreateDraftAsync(_rig.OrgId, _segmentId, "Oggetto", "<p>Testo</p>", false);
        await campaigns.SubmitForApprovalAsync(draft.Id, _rig.OrgId);
        var item = await _rig.Db.ApprovalQueueItems.AsNoTracking().SingleAsync();

        var firstCall = await campaigns.MarkApprovedAsync(draft.Id, _rig.OrgId, "Un altro oggetto", "<p>Un altro testo</p>");
        var secondCall = await campaigns.MarkApprovedAsync(draft.Id, _rig.OrgId, "Ancora un altro", "<p>Ancora</p>");

        // The second call finds the campaign approved and changes nothing, edits included.
        Assert.Equal(CampaignStatus.Approved, firstCall.Status);
        Assert.Equal(("Un altro oggetto", "<p>Un altro testo</p>"), (secondCall.Subject, secondCall.Body));
        var decision = await queue.ApproveAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.Owner);
        Assert.Equal("Approved", decision.ActionState);
        Assert.Equal(CampaignStatus.Approved, (await _rig.Db.Campaigns.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task MarkApproved_ADraftOrACampaignOfAnotherOrg_IsRefused_AndTheEditsComeTogether()
    {
        var (campaigns, _) = await BuildAsync();
        var draft = await campaigns.CreateDraftAsync(_rig.OrgId, _segmentId, "Oggetto", "<p>Testo</p>", false);

        var notPending = await Assert.ThrowsAsync<DomainConflictException>(() => campaigns.MarkApprovedAsync(draft.Id, _rig.OrgId, null, null));
        var otherOrg = await Assert.ThrowsAsync<DomainRuleException>(() => campaigns.MarkApprovedAsync(draft.Id, Guid.NewGuid(), null, null));
        await Assert.ThrowsAsync<ArgumentException>(() => campaigns.MarkApprovedAsync(draft.Id, _rig.OrgId, "Solo l'oggetto", null));

        Assert.Equal("campaign_not_pending_approval", notPending.Code);
        Assert.Equal("campaign_not_found", otherOrg.Code);
        Assert.Equal(CampaignStatus.Draft, (await _rig.Db.Campaigns.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task ReturnToDraft_OnlyAffectsACampaignThatWaited()
    {
        var (campaigns, _) = await BuildAsync();
        var draft = await campaigns.CreateDraftAsync(_rig.OrgId, _segmentId, "Oggetto", "<p>Testo</p>", false);
        await campaigns.SubmitForApprovalAsync(draft.Id, _rig.OrgId);
        await campaigns.MarkApprovedAsync(draft.Id, _rig.OrgId, null, null);

        // Approved campaigns, campaigns of another org and campaigns that are gone are left as they are, with no error.
        await campaigns.ReturnToDraftAsync(draft.Id, _rig.OrgId);
        await campaigns.ReturnToDraftAsync(draft.Id, Guid.NewGuid());
        await campaigns.ReturnToDraftAsync(Guid.NewGuid(), _rig.OrgId);

        Assert.Equal(CampaignStatus.Approved, (await _rig.Db.Campaigns.AsNoTracking().SingleAsync()).Status);
    }
}
