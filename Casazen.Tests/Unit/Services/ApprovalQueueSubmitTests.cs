using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>E05/F04/US01/T02: what a submission to the approval queue stores and what it refuses, and what the list gives.</summary>
public class ApprovalQueueSubmitTests : IDisposable
{
    private readonly ApprovalQueueRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task Submit_ADraftOfAMessage_StoresWhatTheAssistantKnowsAndAnActionThatWaits()
    {
        await _rig.SeedAsync();
        var thread = ApprovalQueueRig.OtaKey(Guid.Parse("3F2504E0-4F89-41D3-9A0C-0305E82C3301"));

        var item = await _rig.Service().SubmitForApprovalAsync(
            _rig.OrgId,
            ApprovalItemTypes.MessageDraft,
            Guid.NewGuid(),
            ApprovalQueueRig.DraftText,
            isAiGenerated: true,
            contentVersion: "v1",
            propertyId: _rig.PropertyA.Id,
            originAction: ApprovalOriginActions.SendGuestMessage,
            confidenceScore: 0.912m,
            situationCode: ApprovalQueueRig.Situation,
            threadKey: thread.ToUpperInvariant().Replace("OTA:", "ota:", StringComparison.Ordinal));

        var stored = await _rig.Db.ApprovalQueueItems.AsNoTracking().SingleAsync();
        Assert.Equal(item.Id, stored.Id);
        Assert.Equal((_rig.OrgId, ApprovalStatus.Pending), (stored.OrgId, stored.Status));
        Assert.Equal(_rig.PropertyA.Id, stored.PropertyId);
        Assert.Equal(ApprovalOriginActions.SendGuestMessage, stored.OriginAction);
        Assert.Equal(ApprovalOriginActionStatus.Pending, stored.OriginActionStatus);
        Assert.Equal(0.912m, stored.ConfidenceScore);
        Assert.Equal(ApprovalQueueRig.Situation, stored.SituationCode);
        // The key of the conversation is stored in its own form, lower case.
        Assert.Equal("ota:3f2504e0-4f89-41d3-9a0c-0305e82c3301", stored.ThreadKey);
        Assert.Equal("v1", stored.ContentVersion);
        Assert.True(stored.IsAiGenerated);
        Assert.Equal(ApprovalQueueRig.Noon, stored.CreatedAt);
        Assert.Equal(ApprovalQueueRig.Noon.AddDays(ApprovalQueueRules.ExpirationDays), stored.ExpiresAt);
        Assert.Null(stored.EditedContentText);
        Assert.Null(stored.RejectionReason);
    }

    [Theory]
    [InlineData(ApprovalItemTypes.Routine)]
    [InlineData(ApprovalItemTypes.AiText)]
    [InlineData("TypeALaterEpicAdds")]
    public async Task Submit_ATypeWithNoAction_IsAPureDecisionThatShowsAsSuch(string itemType)
    {
        await _rig.SeedAsync();

        var item = await _rig.Service().SubmitForApprovalAsync(
            _rig.OrgId, itemType, Guid.NewGuid(), "Un testo", isAiGenerated: false, originAction: ApprovalOriginActions.None);

        Assert.Equal(ApprovalOriginActions.None, item.OriginAction);
        Assert.Equal(ApprovalOriginActionStatus.NotRequired, item.OriginActionStatus);
        Assert.Null(item.PropertyId);
        Assert.Null(item.ThreadKey);
    }

    // ─── What is refused ───

    [Fact]
    public async Task Submit_AnItemThatSaysItSendsAMessageAndHasNoActionThatSendsIt_IsRefused()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();

        // An approval that does nothing, shown as one that sends: refused when it is made, not discovered when the host approves.
        await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitForApprovalAsync(
            _rig.OrgId, ApprovalItemTypes.AiText, Guid.NewGuid(), "Un testo", isAiGenerated: true, originAction: ApprovalOriginActions.SendGuestMessage));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitForApprovalAsync(
            _rig.OrgId, ApprovalItemTypes.MessageDraft, Guid.NewGuid(), "Un testo", isAiGenerated: true, originAction: ApprovalOriginActions.None, threadKey: ApprovalQueueRig.OtaKey()));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitForApprovalAsync(
            _rig.OrgId, "ReviewReply", Guid.NewGuid(), "Un testo", isAiGenerated: true, originAction: ApprovalOriginActions.SendReviewReply));

        Assert.Empty(await _rig.Db.ApprovalQueueItems.ToListAsync());
    }

    [Fact]
    public async Task Submit_ADraftWithoutAValidConversation_IsRefused()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();

        await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitForApprovalAsync(
            _rig.OrgId, ApprovalItemTypes.MessageDraft, Guid.NewGuid(), "Un testo", isAiGenerated: true));
        foreach (var threadKey in new[] { "", "ota:42", "other:3f2504e0-4f89-41d3-9a0c-0305e82c3301", "3f2504e0-4f89-41d3-9a0c-0305e82c3301" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitForApprovalAsync(
                _rig.OrgId, ApprovalItemTypes.MessageDraft, Guid.NewGuid(), "Un testo", isAiGenerated: true, threadKey: threadKey));
        }

        Assert.Empty(await _rig.Db.ApprovalQueueItems.ToListAsync());
    }

    [Theory]
    [InlineData("-0.001")]
    [InlineData("1.001")]
    public async Task Submit_AScoreOutsideZeroAndOne_IsRefused(string score)
    {
        await _rig.SeedAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _rig.Service().SubmitForApprovalAsync(
            _rig.OrgId,
            ApprovalItemTypes.MessageDraft,
            Guid.NewGuid(),
            "Un testo",
            isAiGenerated: true,
            confidenceScore: decimal.Parse(score, System.Globalization.CultureInfo.InvariantCulture),
            threadKey: ApprovalQueueRig.OtaKey()));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("0.5")]
    public async Task Submit_AScoreFromZeroToOne_IsKept(string score)
    {
        await _rig.SeedAsync();
        var value = decimal.Parse(score, System.Globalization.CultureInfo.InvariantCulture);

        var item = await _rig.Service().SubmitForApprovalAsync(
            _rig.OrgId, ApprovalItemTypes.MessageDraft, Guid.NewGuid(), "Un testo", isAiGenerated: true, confidenceScore: value, threadKey: ApprovalQueueRig.OtaKey());

        Assert.Equal(value, item.ConfidenceScore);
    }

    [Fact]
    public async Task Submit_ValuesLongerThanTheirColumns_AreRefused()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();

        await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitForApprovalAsync(
            _rig.OrgId, new string('t', 51), Guid.NewGuid(), "Un testo", isAiGenerated: true));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitForApprovalAsync(
            _rig.OrgId, ApprovalItemTypes.AiText, Guid.NewGuid(), "Un testo", isAiGenerated: true, contentVersion: new string('v', 101)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitForApprovalAsync(
            _rig.OrgId, ApprovalItemTypes.AiText, Guid.NewGuid(), "Un testo", isAiGenerated: true, situationCode: new string('s', 65)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitForApprovalAsync(
            _rig.OrgId, ApprovalItemTypes.AiText, Guid.NewGuid(), "Un testo", isAiGenerated: true, situationCode: " "));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitForApprovalAsync(
            _rig.OrgId, ApprovalItemTypes.AiText, Guid.NewGuid(), "  ", isAiGenerated: true));

        Assert.Empty(await _rig.Db.ApprovalQueueItems.ToListAsync());
    }

    /// <summary>
    /// Data rule (tenant isolation): a property that is not one of the org is never trusted. The item is stored with no property, which only a caller who reaches every property
    /// sees, and nobody of the other org is told or reaches it.
    /// </summary>
    [Fact]
    public async Task Submit_APropertyOfAnotherOrg_IsStoredAsNoProperty_Test()
    {
        await _rig.SeedAsync();
        var strangerOrg = OrgTeamTestData.AddOrg(_rig.Db);
        var foreign = new Property
        {
            OrgId = strangerOrg.Id,
            OwnerId = "auth0|stranger",
            Name = "Casa Altrui",
            Address = "Via Altra 2",
            City = "Milano",
            PostalCode = "20100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 90m,
        };
        _rig.Db.Properties.Add(foreign);
        await _rig.Db.SaveChangesAsync();

        var item = await _rig.Service().SubmitForApprovalAsync(
            _rig.OrgId,
            ApprovalItemTypes.MessageDraft,
            Guid.NewGuid(),
            "Un testo",
            isAiGenerated: true,
            propertyId: foreign.Id,
            threadKey: ApprovalQueueRig.OtaKey());

        Assert.Null(item.PropertyId);
        Assert.Empty(await _rig.Service().GetPendingAsync(_rig.OnlyA));
        Assert.Single(await _rig.Service().GetPendingAsync(_rig.OrgWide));
        Assert.Empty(await _rig.Service().GetPendingAsync(new Casazen.Core.Authorization.HostScope(strangerOrg.Id)));
        // The alert went to the administrators of the org of the item, as the item of an org with no property does.
        Assert.All(_rig.Push.Queued, p => Assert.Equal(_rig.OrgId, p.Audience.Id));
    }

    // ─── The list ───

    [Fact]
    public async Task GetPending_ListsTheItemsWaitingOldestFirst_NeverADecidedOne()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var first = await _rig.SubmitDraftAsync(service, _rig.PropertyA);
        _rig.Clock.Advance(TimeSpan.FromMinutes(1));
        var second = await _rig.SubmitDraftAsync(service, _rig.PropertyB);
        _rig.Clock.Advance(TimeSpan.FromMinutes(1));
        var third = await _rig.SubmitDraftAsync(service, _rig.PropertyA);
        _rig.Clock.Advance(TimeSpan.FromMinutes(1));
        var decided = await _rig.SubmitDraftAsync(service, _rig.PropertyA);
        await service.RejectAsync(_rig.OrgWide, decided.Id, ApprovalQueueRig.Owner, "No");
        var expired = await _rig.SubmitDraftAsync(service, _rig.PropertyA);
        (await _rig.Db.ApprovalQueueItems.SingleAsync(i => i.Id == expired.Id)).Status = ApprovalStatus.Expired;
        await _rig.Db.SaveChangesAsync();

        var pending = await service.GetPendingAsync(_rig.OrgWide);

        Assert.Equal([first.Id, second.Id, third.Id], pending.Select(i => i.Id));
        Assert.All(pending, i => Assert.Equal(ApprovalStatus.Pending, i.Status));
    }
}
