using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E05/F04/US01/T02 (NF-68): the proofs of the approval queue that executes. A decision in the queue does what the item was waiting for, once, with the text the host
/// approved; a rejection keeps its reason; the host who may decide reaches only the items of its properties. EF InMemory, with the real "To do" service, the real preferences and
/// the real actions over a fake of the replies that keeps the rule of the key of a send (<see cref="ApprovalQueueRig"/>).
/// </summary>
public class ApprovalDecisionProofTests : IDisposable
{
    private readonly ApprovalQueueRig _rig = new();

    public void Dispose() => _rig.Dispose();

    // ─── Data: once, and the edited text (AC-NF68-1, AC-NF68-2) ───

    /// <summary>
    /// Data rule (no duplicate message): two approvals of one draft of a message reach the guest once. The second one (a double click, another host) runs nothing, and the key of
    /// the send is the item's and is one the service of the replies accepts (a colon, as the brief wrote it, would be refused as <c>idempotency_key_invalid</c>).
    /// </summary>
    [Fact]
    public async Task Approval_SecondApproveClick_SendsOnce_Test()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var item = await _rig.SubmitDraftAsync(service, _rig.PropertyA);

        var first = await service.ApproveAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.Owner);
        var second = await service.ApproveAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.Manager);

        var sent = Assert.Single(_rig.Sent);
        Assert.Equal(ApprovalQueueRules.SendKeyOf(item.Id, ApprovalQueueRig.DraftText), sent.IdempotencyKey);
        Assert.NotNull(GuestMessageRules.NormalizeIdempotencyKey(sent.IdempotencyKey));
        Assert.Equal(nameof(OtaMessageDeliveryState.Sent), first.ActionState);
        Assert.Null(second.ActionState);

        // The first approver stays; the item is approved and its action executed, once.
        var stored = await _rig.Db.ApprovalQueueItems.AsNoTracking().SingleAsync(i => i.Id == item.Id);
        Assert.Equal(ApprovalStatus.Approved, stored.Status);
        Assert.Equal(ApprovalQueueRig.Owner, stored.ApprovedByUserId);
        Assert.Equal(ApprovalOriginActionStatus.Executed, stored.OriginActionStatus);
        Assert.Equal(ApprovalOriginActions.SendGuestMessage, stored.OriginAction);
        Assert.Equal(ApprovalQueueRig.Noon, stored.OriginActionExecutedAt);
        Assert.Null(stored.OriginActionError);
    }

    /// <summary>
    /// Data rule (the text that is approved is the text that is sent): an edited draft goes out as the edited text, as the person's message (not the assistant's), and the original
    /// text, with its author and time of the edit, stays in the item.
    /// </summary>
    [Fact]
    public async Task Approval_EditedText_IsWhatIsSent_Test()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var item = await _rig.SubmitDraftAsync(service, _rig.PropertyA);

        var edited = await service.EditAsync(_rig.OrgWide, item.Id, "  Buongiorno,\r\nil check-in è dalle 14:00.  ", ApprovalQueueRig.Owner);
        await service.ApproveAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.Owner);

        var sent = Assert.Single(_rig.Sent);
        Assert.Equal("Buongiorno,\nil check-in è dalle 14:00.", sent.Text);
        Assert.Equal(edited.EditedContentText, sent.Text);
        Assert.Equal(MessageOrigin.Person, sent.Origin);
        Assert.Null(sent.RuleCode);
        Assert.Equal(ApprovalQueueRig.Owner, sent.UserId);

        var stored = await _rig.Db.ApprovalQueueItems.AsNoTracking().SingleAsync(i => i.Id == item.Id);
        Assert.Equal(ApprovalQueueRig.DraftText, stored.ContentText);
        Assert.Equal("Buongiorno,\nil check-in è dalle 14:00.", stored.EditedContentText);
        Assert.Equal(ApprovalQueueRig.Owner, stored.EditedByUserId);
        Assert.Equal(ApprovalQueueRig.Noon, stored.EditedAt);
    }

    [Fact]
    public async Task Approval_UneditedDraft_IsTheAssistantsMessage_WithTheSituationAsItsRule()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var item = await _rig.SubmitDraftAsync(service, _rig.PropertyA);

        await service.ApproveAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.Owner);

        var sent = Assert.Single(_rig.Sent);
        Assert.Equal(ApprovalQueueRig.DraftText, sent.Text);
        Assert.Equal(MessageOrigin.Assistant, sent.Origin);
        Assert.Equal(ApprovalQueueRig.Situation, sent.RuleCode);
        Assert.Equal(item.ThreadKey, sent.Key.ToString());
        // The executor is a system caller for the org: who may approve was decided before.
        Assert.Equal(_rig.OrgId, sent.Scope.OrgId);
        Assert.True(sent.Scope.IsOrgWide);
    }

    [Fact]
    public async Task Approval_AnEditThatGivesTheOriginalBack_IsNoEdit()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var item = await _rig.SubmitDraftAsync(service, _rig.PropertyA);
        await service.EditAsync(_rig.OrgWide, item.Id, "Un altro testo", ApprovalQueueRig.Owner);

        var back = await service.EditAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.DraftText, ApprovalQueueRig.Owner);
        await service.ApproveAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.Owner);

        Assert.Null(back.EditedContentText);
        Assert.Null(back.EditedByUserId);
        Assert.Null(back.EditedAt);
        var sent = Assert.Single(_rig.Sent);
        Assert.Equal(MessageOrigin.Assistant, sent.Origin);
        Assert.Equal(ApprovalQueueRig.DraftText, sent.Text);
    }

    // ─── Failures and pure decisions (AC-NF68-1) ───

    [Fact]
    public async Task Approval_AFailedAction_LeavesTheItemPendingWithTheCode_AndTheRetryAfterAnEditSendsOnce()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var item = await _rig.SubmitDraftAsync(service, _rig.PropertyA);
        _rig.RefuseKey(ApprovalQueueRules.SendKeyOf(item.Id, ApprovalQueueRig.DraftText));

        var failure = await Assert.ThrowsAsync<DomainConflictException>(() => service.ApproveAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.Owner));

        Assert.Equal("approval_origin_action_failed", failure.Code);
        Assert.Equal("octorate_request_rejected", failure.Extensions["originActionError"]);
        var pending = await _rig.Db.ApprovalQueueItems.AsNoTracking().SingleAsync(i => i.Id == item.Id);
        Assert.Equal(ApprovalStatus.Pending, pending.Status);
        Assert.Equal(ApprovalOriginActionStatus.Failed, pending.OriginActionStatus);
        Assert.Equal("octorate_request_rejected", pending.OriginActionError);
        Assert.Null(pending.ApprovedAt);
        Assert.Null(await _rig.Db.InternalActivities.AsNoTracking().Where(a => a.EntityId == item.Id).Select(a => a.CompletedAt).SingleAsync());

        // The host edits and tries again: the key follows the text, so the corrected message is not taken for the refused one and goes out, once, as the person's.
        await service.EditAsync(_rig.OrgWide, item.Id, "Un testo corretto", ApprovalQueueRig.Owner);
        var retried = await service.ApproveAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.Owner);
        await service.ApproveAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.Owner);

        Assert.Equal(nameof(OtaMessageDeliveryState.Sent), retried.ActionState);
        Assert.Equal(
            [ApprovalQueueRules.SendKeyOf(item.Id, ApprovalQueueRig.DraftText), ApprovalQueueRules.SendKeyOf(item.Id, "Un testo corretto")],
            _rig.Sent.Select(s => s.IdempotencyKey));
        Assert.Equal([ApprovalQueueRig.DraftText, "Un testo corretto"], _rig.Sent.Select(s => s.Text));
        Assert.Equal(MessageOrigin.Person, _rig.Sent[1].Origin);
        var done = await _rig.Db.ApprovalQueueItems.AsNoTracking().SingleAsync(i => i.Id == item.Id);
        Assert.Equal((ApprovalStatus.Approved, ApprovalOriginActionStatus.Executed, (string?)null), (done.Status, done.OriginActionStatus, done.OriginActionError));
    }

    /// <summary>The host retries with the same text after a refusal of the channel: the refusal of the first key is replayed, so the message goes with the next attempt's key, once.</summary>
    [Fact]
    public async Task Approval_ARetryWithNoEditAfterARefusal_SendsWithTheNextKey()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var item = await _rig.SubmitDraftAsync(service, _rig.PropertyA);
        var first = ApprovalQueueRules.SendKeyOf(item.Id, ApprovalQueueRig.DraftText);
        _rig.RefuseKey(first);

        await Assert.ThrowsAsync<DomainConflictException>(() => service.ApproveAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.Owner));
        var retried = await service.ApproveAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.Owner);

        Assert.Equal(nameof(OtaMessageDeliveryState.Sent), retried.ActionState);
        Assert.Equal([first, first, ApprovalQueueRules.SendKeyOf(item.Id, ApprovalQueueRig.DraftText, 2)], _rig.Sent.Select(s => s.IdempotencyKey));
    }

    /// <summary>
    /// Data rule (the text that is approved is the text that is sent): an item that was approved and sent, then went back to pending with a new text, sends the new text when it is approved
    /// again. The service of the replies replays the message of a key it already knows whatever text it is given, so a key of the item alone would have answered Sent and sent nothing.
    /// </summary>
    [Fact]
    public async Task Approval_AfterAReturnToPending_SendsTheNewText_AndDoesNotReplayTheFirstMessage_Test()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var draftId = Guid.NewGuid();
        var item = await service.SubmitForApprovalAsync(
            _rig.OrgId, ApprovalItemTypes.MessageDraft, draftId, ApprovalQueueRig.DraftText, true, propertyId: _rig.PropertyA.Id, threadKey: ApprovalQueueRig.OtaKey());
        await service.ApproveAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.Owner);
        _rig.Clock.Advance(TimeSpan.FromMinutes(5));

        var returned = await service.ReturnToPendingIfChangedAsync(ApprovalItemTypes.MessageDraft, draftId, "Il testo nuovo del messaggio");
        var again = await service.ApproveAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.Owner);

        Assert.True(returned);
        Assert.Equal(nameof(OtaMessageDeliveryState.Sent), again.ActionState);
        Assert.Equal([ApprovalQueueRig.DraftText, "Il testo nuovo del messaggio"], _rig.Sent.Select(s => s.Text));
        Assert.Equal(
            [ApprovalQueueRules.SendKeyOf(item.Id, ApprovalQueueRig.DraftText), ApprovalQueueRules.SendKeyOf(item.Id, "Il testo nuovo del messaggio")],
            _rig.Sent.Select(s => s.IdempotencyKey));
    }

    [Fact]
    public async Task Approval_AnExceptionNobodyForesaw_IsRecordedAndLetThrough_TheItemStaysPending()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var item = await _rig.SubmitDraftAsync(service, _rig.PropertyA);
        _rig.Replies
            .Setup(r => r.ReplyAsync(
                It.IsAny<Casazen.Core.Authorization.HostScope>(),
                It.IsAny<MessageThreadKey>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<MessageOrigin>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the database went away"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.Owner));

        var stored = await _rig.Db.ApprovalQueueItems.AsNoTracking().SingleAsync(i => i.Id == item.Id);
        Assert.Equal(ApprovalStatus.Pending, stored.Status);
        Assert.Equal(ApprovalOriginActionStatus.Failed, stored.OriginActionStatus);
        Assert.Equal(ApprovalQueueRules.UnexpectedErrorCode, stored.OriginActionError);
    }

    [Theory]
    [InlineData("thread_closed")]
    [InlineData("mapping_not_confirmed")]
    public async Task Approval_AConversationThatCannotTakeTheMessage_IsAFailureWithItsCode(string code)
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var item = await _rig.SubmitDraftAsync(service, _rig.PropertyA);
        _rig.Replies
            .Setup(r => r.ReplyAsync(
                It.IsAny<Casazen.Core.Authorization.HostScope>(),
                It.IsAny<MessageThreadKey>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<MessageOrigin>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DomainConflictException(code, "GuestThreadClosed"));

        var failure = await Assert.ThrowsAsync<DomainConflictException>(() => service.ApproveAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.Owner));

        Assert.Equal("approval_origin_action_failed", failure.Code);
        Assert.Equal(code, failure.Extensions["originActionError"]);
        Assert.Equal(code, (await _rig.Db.ApprovalQueueItems.AsNoTracking().SingleAsync(i => i.Id == item.Id)).OriginActionError);
    }

    [Fact]
    public async Task Approval_AnAmbiguousSend_CountsAsExecutedAndSaysItsState()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var item = await _rig.SubmitDraftAsync(service, _rig.PropertyA);
        _rig.Replies
            .Setup(r => r.ReplyAsync(
                It.IsAny<Casazen.Core.Authorization.HostScope>(),
                It.IsAny<MessageThreadKey>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<MessageOrigin>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MessageReplyResult(MessageContactChannel.Ota, null, null, OtaMessageDeliveryState.ManualVerify, false, null));

        var decision = await service.ApproveAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.Owner);

        Assert.Equal(nameof(OtaMessageDeliveryState.ManualVerify), decision.ActionState);
        Assert.Equal(ApprovalStatus.Approved, decision.Item.Status);
        Assert.Equal(ApprovalOriginActionStatus.Executed, decision.Item.OriginActionStatus);
    }

    [Fact]
    public async Task Approval_ATypeWithNoAction_IsAPureDecision()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var item = await service.SubmitForApprovalAsync(_rig.OrgId, ApprovalItemTypes.AiText, Guid.NewGuid(), "Un testo dell'AI", isAiGenerated: true);

        Assert.Equal(ApprovalOriginActions.None, item.OriginAction);
        Assert.Equal(ApprovalOriginActionStatus.NotRequired, item.OriginActionStatus);

        var decision = await service.ApproveAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.Owner);

        Assert.Null(decision.ActionState);
        Assert.Equal(ApprovalStatus.Approved, decision.Item.Status);
        Assert.Equal(ApprovalOriginActionStatus.NotRequired, decision.Item.OriginActionStatus);
        Assert.Equal(ApprovalOriginActions.None, decision.Item.OriginAction);
        Assert.Empty(_rig.Sent);
        _rig.Campaigns.VerifyNoOtherCalls();
    }

    // ─── Campaigns (AC-NF68-1) ───

    [Fact]
    public async Task Approval_ACampaign_IsApprovedWithItsEditedTextAndScheduledWhenItHasATime()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var campaignId = Guid.NewGuid();
        var at = ApprovalQueueRig.Noon.AddDays(2);
        _rig.Campaigns
            .Setup(c => c.MarkApprovedAsync(campaignId, _rig.OrgId, "Nuovo oggetto", "<p>Nuovo testo</p>", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Campaign { Id = campaignId, OrgId = _rig.OrgId, Status = CampaignStatus.Approved, ScheduledAt = at });
        var item = await service.SubmitForApprovalAsync(
            _rig.OrgId,
            ApprovalItemTypes.Campaign,
            campaignId,
            ApprovalQueueRules.CampaignTextOf("Oggetto", "<p>Testo</p>"),
            isAiGenerated: false,
            originAction: ApprovalOriginActions.ApproveCampaign);
        await service.EditAsync(_rig.OrgWide, item.Id, "Nuovo oggetto\r\n\r\n<p>Nuovo testo</p>", ApprovalQueueRig.Owner);

        var decision = await service.ApproveAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.Owner);

        Assert.Equal("Scheduled", decision.ActionState);
        _rig.Campaigns.Verify(c => c.ScheduleAsync(campaignId, at, _rig.OrgId, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(ApprovalOriginActions.ApproveCampaign, decision.Item.OriginAction);
        Assert.Equal(ApprovalOriginActionStatus.Executed, decision.Item.OriginActionStatus);
    }

    [Fact]
    public async Task Approval_AnUneditedCampaignWithNoTime_IsApprovedWithItsOwnTextAndWaitsForTheHost()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var campaignId = Guid.NewGuid();
        _rig.Campaigns
            .Setup(c => c.MarkApprovedAsync(campaignId, _rig.OrgId, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Campaign { Id = campaignId, OrgId = _rig.OrgId, Status = CampaignStatus.Approved });
        var item = await service.SubmitForApprovalAsync(
            _rig.OrgId, ApprovalItemTypes.Campaign, campaignId, ApprovalQueueRules.CampaignTextOf("Oggetto", "<p>Testo</p>"), isAiGenerated: false);

        var decision = await service.ApproveAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.Owner);

        Assert.Equal("Approved", decision.ActionState);
        _rig.Campaigns.Verify(c => c.ScheduleAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Approval_ACampaignThatCannotBeApproved_IsAFailureWithTheCodeOfTheCampaign()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var campaignId = Guid.NewGuid();
        _rig.Campaigns
            .Setup(c => c.MarkApprovedAsync(campaignId, _rig.OrgId, null, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DomainConflictException("campaign_not_pending_approval", "Only campaigns waiting for approval can be approved"));
        var item = await service.SubmitForApprovalAsync(
            _rig.OrgId, ApprovalItemTypes.Campaign, campaignId, ApprovalQueueRules.CampaignTextOf("Oggetto", "<p>Testo</p>"), isAiGenerated: false);

        var failure = await Assert.ThrowsAsync<DomainConflictException>(() => service.ApproveAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.Owner));

        Assert.Equal("campaign_not_pending_approval", failure.Extensions["originActionError"]);
        Assert.Equal(ApprovalStatus.Pending, (await _rig.Db.ApprovalQueueItems.AsNoTracking().SingleAsync(i => i.Id == item.Id)).Status);
    }

    // ─── Rejection (AC-NF68-3) ───

    /// <summary>
    /// Data rule (a decision leaves its reason): a rejection without a reason, with a blank one or with one longer than 500 characters is refused and the item stays pending; with
    /// its reason it is saved on the item, the hook of the type runs (a campaign goes back to a draft, a message needs nothing) and nothing is sent.
    /// </summary>
    [Fact]
    public async Task Approval_ARejection_NeedsItsReason_SavesIt_AndRunsTheHookOfTheType_Test()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var draft = await _rig.SubmitDraftAsync(service, _rig.PropertyA);
        var campaignId = Guid.NewGuid();
        var campaign = await service.SubmitForApprovalAsync(
            _rig.OrgId, ApprovalItemTypes.Campaign, campaignId, ApprovalQueueRules.CampaignTextOf("Oggetto", "<p>Testo</p>"), isAiGenerated: false);

        foreach (var reason in new string?[] { null, "", "   \r\n ", new string('x', 501) })
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.RejectAsync(_rig.OrgWide, draft.Id, ApprovalQueueRig.Owner, reason));
            Assert.Equal("approval_rejection_reason_required", refused.Code);
        }

        Assert.Equal(ApprovalStatus.Pending, (await _rig.Db.ApprovalQueueItems.AsNoTracking().SingleAsync(i => i.Id == draft.Id)).Status);

        var rejected = await service.RejectAsync(_rig.OrgWide, draft.Id, ApprovalQueueRig.Owner, "  Il tono non va  ");
        await service.RejectAsync(_rig.OrgWide, campaign.Id, ApprovalQueueRig.Manager, new string('y', 500));

        Assert.Equal(ApprovalStatus.Rejected, rejected.Status);
        Assert.Equal("Il tono non va", rejected.RejectionReason);
        Assert.Equal(ApprovalQueueRig.Owner, rejected.ApprovedByUserId);
        Assert.Equal(ApprovalQueueRig.Noon, rejected.ApprovedAt);
        Assert.Equal(new string('y', 500), (await _rig.Db.ApprovalQueueItems.AsNoTracking().SingleAsync(i => i.Id == campaign.Id)).RejectionReason);
        Assert.Empty(_rig.Sent);
        _rig.Campaigns.Verify(c => c.ReturnToDraftAsync(campaignId, _rig.OrgId, It.IsAny<CancellationToken>()), Times.Once);

        // Both leave the queue, and their "To do" is closed.
        Assert.Empty(await service.GetPendingAsync(_rig.OrgWide));
        var todos = await _rig.Db.InternalActivities.AsNoTracking().ToListAsync();
        Assert.Equal(2, todos.Count);
        Assert.All(todos, a => Assert.NotNull(a.CompletedAt));
    }

    /// <summary>
    /// The rejection is saved, and its "To do" is closed whatever happens to the request, as for an approval: the close gets no token of the request (a cut request would otherwise leave a
    /// rejected item with an open "To do" that nothing closes). One that could not be closed is found by the daily job (<c>ApprovalExpirationAndPrivacyTests</c>).
    /// </summary>
    [Fact]
    public async Task Rejection_ClosesTheTodoOfTheItem_WithNoDependenceOnTheTokenOfTheRequest()
    {
        await _rig.SeedAsync();
        var closeTokens = new List<CancellationToken>();
        var activities = new Mock<IInternalActivityService>();
        activities
            .Setup(a => a.CloseAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, CancellationToken>((_, _, token) => closeTokens.Add(token))
            .Returns(Task.CompletedTask);
        var service = new Casazen.Infrastructure.Services.ApprovalQueueService(
            _rig.Db,
            _rig.Clock,
            _rig.Registry(),
            activities.Object,
            _rig.Alerts(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Casazen.Infrastructure.Services.ApprovalQueueService>.Instance);
        var item = await _rig.SubmitDraftAsync(service, _rig.PropertyA);
        using var request = new CancellationTokenSource();

        await service.RejectAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.Owner, "No", request.Token);

        Assert.Equal([CancellationToken.None], closeTokens);
    }

    // ─── Reach (AC-NF68-5, AM-03) ───

    /// <summary>
    /// Permission rule (the reach of the caller): a caller whose reach is limited to some properties lists, edits, approves and rejects only the items of those, and never an item
    /// of the org (no property); an item of another property is a 404, not a 403, and nothing of it runs.
    /// </summary>
    [Fact]
    public async Task Approval_APartialScope_ReadsAndDecidesOnlyTheItemsOfItsProperties_Test()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var onA = await _rig.SubmitDraftAsync(service, _rig.PropertyA);
        var onB = await _rig.SubmitDraftAsync(service, _rig.PropertyB);
        var ofTheOrg = await service.SubmitForApprovalAsync(
            _rig.OrgId, ApprovalItemTypes.Campaign, Guid.NewGuid(), ApprovalQueueRules.CampaignTextOf("Oggetto", "<p>Testo</p>"), isAiGenerated: false);

        Assert.Equal([onA.Id], (await service.GetPendingAsync(_rig.OnlyA)).Select(i => i.Id));
        Assert.Equal(
            new[] { onA.Id, onB.Id, ofTheOrg.Id }.Order(),
            (await service.GetPendingAsync(_rig.OrgWide)).Select(i => i.Id).Order());

        foreach (var hidden in new[] { onB.Id, ofTheOrg.Id })
        {
            var edit = await Assert.ThrowsAsync<NotFoundException>(() => service.EditAsync(_rig.OnlyA, hidden, "Un testo", ApprovalQueueRig.Collaborator));
            var approve = await Assert.ThrowsAsync<NotFoundException>(() => service.ApproveAsync(_rig.OnlyA, hidden, ApprovalQueueRig.Collaborator));
            var reject = await Assert.ThrowsAsync<NotFoundException>(() => service.RejectAsync(_rig.OnlyA, hidden, ApprovalQueueRig.Collaborator, "No"));
            Assert.All(new[] { edit, approve, reject }, e => Assert.Equal("approval_item_not_found", e.Code));
        }

        Assert.Empty(_rig.Sent);
        _rig.Campaigns.VerifyNoOtherCalls();
        Assert.All(
            await _rig.Db.ApprovalQueueItems.AsNoTracking().Where(i => i.Id != onA.Id).ToListAsync(),
            i => Assert.Equal((ApprovalStatus.Pending, (string?)null), (i.Status, i.EditedContentText)));

        // Within its reach it decides.
        await service.ApproveAsync(_rig.OnlyA, onA.Id, ApprovalQueueRig.Collaborator);
        Assert.Single(_rig.Sent);
    }

    [Fact]
    public async Task Approval_AnItemOfAnotherOrg_IsNeverFound()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var item = await _rig.SubmitDraftAsync(service, _rig.PropertyA);

        var stranger = new Casazen.Core.Authorization.HostScope(Guid.NewGuid());

        Assert.Empty(await service.GetPendingAsync(stranger));
        await Assert.ThrowsAsync<NotFoundException>(() => service.ApproveAsync(stranger, item.Id, "auth0|stranger"));
        await Assert.ThrowsAsync<NotFoundException>(() => service.RejectAsync(stranger, item.Id, "auth0|stranger", "No"));
        await Assert.ThrowsAsync<NotFoundException>(() => service.EditAsync(stranger, item.Id, "Un testo", "auth0|stranger"));
        Assert.Empty(_rig.Sent);
    }

    // ─── Edits (AC-NF68-2) ───

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Edit_AnEmptyText_IsRefused(string? text)
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var item = await _rig.SubmitDraftAsync(service, _rig.PropertyA);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.EditAsync(_rig.OrgWide, item.Id, text, ApprovalQueueRig.Owner));

        Assert.Equal("approval_edit_invalid", refused.Code);
        Assert.Null((await _rig.Db.ApprovalQueueItems.AsNoTracking().SingleAsync(i => i.Id == item.Id)).EditedContentText);
    }

    [Fact]
    public async Task Edit_AMessageLongerThanTheLimit_IsRefused_AndOneOfTheLimitIsKept()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var item = await _rig.SubmitDraftAsync(service, _rig.PropertyA);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.EditAsync(_rig.OrgWide, item.Id, new string('a', GuestMessageRules.TextMaxLength + 1), ApprovalQueueRig.Owner));
        var kept = await service.EditAsync(_rig.OrgWide, item.Id, new string('a', GuestMessageRules.TextMaxLength), ApprovalQueueRig.Owner);

        Assert.Equal("approval_edit_invalid", refused.Code);
        Assert.Equal(GuestMessageRules.TextMaxLength, kept.EditedContentText!.Length);
    }

    [Theory]
    [InlineData("Solo l'oggetto")]
    [InlineData("Oggetto\nsu due righe\n\nTesto")]
    [InlineData("\n\nTesto senza oggetto")]
    [InlineData("Oggetto\n\n   ")]
    public async Task Edit_ACampaignWithoutASubjectLineABlankLineAndABody_IsRefused(string text)
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var item = await service.SubmitForApprovalAsync(
            _rig.OrgId, ApprovalItemTypes.Campaign, Guid.NewGuid(), ApprovalQueueRules.CampaignTextOf("Oggetto", "<p>Testo</p>"), isAiGenerated: false);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.EditAsync(_rig.OrgWide, item.Id, text, ApprovalQueueRig.Owner));

        Assert.Equal("approval_edit_invalid", refused.Code);
    }

    [Fact]
    public async Task EditApproveAndReject_OfADecidedItem_AreConflicts_ExceptTheSecondApprovalOfAnApprovedOne()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var approved = await _rig.SubmitDraftAsync(service, _rig.PropertyA);
        var rejected = await _rig.SubmitDraftAsync(service, _rig.PropertyA);
        await service.ApproveAsync(_rig.OrgWide, approved.Id, ApprovalQueueRig.Owner);
        await service.RejectAsync(_rig.OrgWide, rejected.Id, ApprovalQueueRig.Owner, "No");

        foreach (var id in new[] { approved.Id, rejected.Id })
        {
            var edit = await Assert.ThrowsAsync<DomainConflictException>(() => service.EditAsync(_rig.OrgWide, id, "Un testo", ApprovalQueueRig.Owner));
            var reject = await Assert.ThrowsAsync<DomainConflictException>(() => service.RejectAsync(_rig.OrgWide, id, ApprovalQueueRig.Owner, "No"));
            Assert.All(new[] { edit, reject }, e => Assert.Equal("approval_item_not_pending", e.Code));
        }

        var again = await Assert.ThrowsAsync<DomainConflictException>(() => service.ApproveAsync(_rig.OrgWide, rejected.Id, ApprovalQueueRig.Owner));
        Assert.Equal("approval_item_not_pending", again.Code);
        Assert.Single(_rig.Sent);
    }
}
