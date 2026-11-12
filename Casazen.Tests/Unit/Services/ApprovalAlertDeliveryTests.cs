using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E05/F04/US01/T02 (AC-NF68-4, ADR-025, ADR-026): a new item of the approval queue alerts only the hosts who hold <c>approval.decide</c> and reach its property, through the
/// counter, the push and the e-mail they chose and with the quiet hours held back, raises ONE "To do" for it, and tells nobody twice and nothing of the text. EF InMemory; the push
/// queue, the e-mail queue, the bell and Hangfire are doubles that record what they are given.
/// </summary>
public class ApprovalAlertDeliveryTests : IDisposable
{
    private const string Secret = "Gentile Zefirino Ospite, il suo IBAN IT60X0542811101000000123456 è stato ricevuto";

    private readonly ApprovalQueueRig _rig = new();

    public void Dispose() => _rig.Dispose();

    // ─── Who is told (AC-NF68-4) ───

    /// <summary>
    /// Permission rule: the alert of an item goes to the people who hold <c>approval.decide</c> and reach its property, not to everybody in the org (as the notice of NF-06 did): the
    /// collaborator in charge of the property, who cannot decide, is told nothing; the manager is told about the property it is in charge of and the owner about both.
    /// </summary>
    [Fact]
    public async Task NewItem_AlertsOnlyTheUsersWhoMayDecideAndReachTheProperty_Test()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();

        var onA = await _rig.SubmitDraftAsync(service, _rig.PropertyA);
        var onB = await _rig.SubmitDraftAsync(service, _rig.PropertyB);

        // On A: the owner (an administrator); the collaborator, in charge of A, does not hold the permission. On B: the owner and the manager in charge.
        var bells = (await _rig.Db.InAppNotifications.AsNoTracking().ToListAsync()).Select(n => (n.EntityId!.Value, n.UserId)).ToHashSet();
        Assert.Equal(
            new HashSet<(Guid, string)>
            {
                (onA.Id, ApprovalQueueRig.Owner),
                (onB.Id, ApprovalQueueRig.Owner),
                (onB.Id, ApprovalQueueRig.Manager),
            },
            bells);
        Assert.DoesNotContain(_rig.Push.Queued, p => p.Audience.UserId == ApprovalQueueRig.Collaborator);
        Assert.Equal(3, _rig.Push.Queued.Count);
        Assert.All(_rig.Push.Queued, p => Assert.Equal(PushAudienceKind.User, p.Audience.Kind));
    }

    [Fact]
    public async Task NewItem_OfTheOrg_AlertsTheAdministratorsWhoMayDecide_AndOpensTheApprovals()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();

        var item = await service.SubmitForApprovalAsync(
            _rig.OrgId, ApprovalItemTypes.Campaign, Guid.NewGuid(), ApprovalQueueRules.CampaignTextOf("Oggetto", "<p>Testo</p>"), isAiGenerated: false);

        // An event with no property goes to the administrators: the owner; the manager is not one, and the collaborator may not decide.
        var push = Assert.Single(_rig.Push.Queued);
        Assert.Equal(PushAudience.User(_rig.OrgId, ApprovalQueueRig.Owner), push.Audience);
        Assert.Equal(PushRoutes.Approvals(), push.Payload.Route);
        Assert.True(PushRoutes.IsAppRoute(push.Payload.Route));
        Assert.Equal(item.Id, push.Payload.ApprovalItemId);
        Assert.Single(await _rig.Db.InAppNotifications.AsNoTracking().ToListAsync(), n => n.UserId == ApprovalQueueRig.Owner && n.EntityId == item.Id);
    }

    [Fact]
    public async Task NewItem_ThePushOfAnApprovalCarriesTheItemTheCategoryAndTheRouteOfTheConversation()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var thread = ApprovalQueueRig.OtaKey();

        var item = await _rig.SubmitDraftAsync(service, _rig.PropertyA, threadKey: thread);

        var push = Assert.Single(_rig.Push.Queued);
        Assert.Equal(PushDeliveryKeys.ApprovalQueueItem(item.Id, item.CreatedAt, ApprovalQueueRig.Owner), push.DeliveryKey);
        Assert.Equal(PushTypes.ApprovalQueueItem, push.Payload.Type);
        Assert.Equal($"/messages/{thread}", push.Payload.Route);
        Assert.Equal(PushRoutes.MessageThread(thread), push.Payload.Route);
        Assert.Equal(item.Id, push.Payload.ApprovalItemId);
        Assert.Equal("draft-ready", push.Payload.CategoryId);
        Assert.Equal(ApprovalQueueRules.PushCategory, push.Payload.CategoryId);
        Assert.Null(push.Payload.BookingId);
        Assert.Contains("Casa Rosa", push.Payload.Body, StringComparison.Ordinal);
        Assert.Equal("Bozza pronta", push.Payload.Title);
    }

    // ─── The choice of each person (AC-NF65) ───

    [Fact]
    public async Task NewItem_TheCounterAndThePushAreTheDefault_TheEmailIsNot()
    {
        await _rig.SeedAsync();

        await _rig.SubmitDraftAsync(_rig.Service(), _rig.PropertyA);

        Assert.Single(await _rig.Db.InAppNotifications.AsNoTracking().ToListAsync());
        Assert.Single(_rig.Push.Queued);
        Assert.Empty(_rig.Email.Snapshot());
        _rig.Publisher.Verify(
            p => p.PublishAsync(ApprovalQueueRig.Owner, PushTypes.ApprovalQueueItem, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task NewItem_FollowsTheChoiceOfThePerson_TheEmailGoesToItsOwnAddressOnce()
    {
        await _rig.SeedAsync();
        await _rig.ChooseAsync(ApprovalQueueRig.Owner, NotificationChannels.Email);
        var ownerEmail = (await _rig.Db.Users.AsNoTracking().SingleAsync(u => u.Id == ApprovalQueueRig.Owner)).Email;
        var service = _rig.Service();

        var item = await _rig.SubmitDraftAsync(service, _rig.PropertyA, text: Secret);
        await _rig.Alerts().RaiseAsync(new ApprovalAlert(_rig.OrgId, item.Id, item.CreatedAt), CancellationToken.None);

        // Neither the counter nor the push (the person chose the e-mail), one e-mail even though the alert was raised twice.
        Assert.Empty(await _rig.Db.InAppNotifications.AsNoTracking().ToListAsync());
        Assert.Empty(_rig.Push.Queued);
        var email = Assert.Single(_rig.Email.Snapshot());
        Assert.Equal(ownerEmail, email.To);
        Assert.Equal(EmailTemplates.Names.HostApprovalToDecide, email.Template);
        Assert.Contains("Casa Rosa", email.Content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("/app/short-rent/approval-queue", email.Content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Da approvare", email.Content.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewItem_ANothingChoice_TellsNobody_ButTheToDoIsRaised()
    {
        await _rig.SeedAsync();
        await _rig.ChooseAsync(ApprovalQueueRig.Owner, NotificationChannels.None);

        var item = await _rig.SubmitDraftAsync(_rig.Service(), _rig.PropertyA);

        Assert.Empty(_rig.Push.Queued);
        Assert.Empty(_rig.Email.Snapshot());
        Assert.Empty(await _rig.Db.InAppNotifications.AsNoTracking().ToListAsync());
        Assert.NotNull(item.ActivityId);
    }

    [Fact]
    public async Task NewItem_AnEmailTheQueueCannotTake_GivesTheSlotBack_SoTheAlertRaisedAgainTriesAgain()
    {
        await _rig.SeedAsync();
        await _rig.ChooseAsync(ApprovalQueueRig.Owner, NotificationChannels.Email);
        _rig.Email.Refuse = true;
        var service = _rig.Service();

        var item = await _rig.SubmitDraftAsync(service, _rig.PropertyA);
        _rig.Email.Refuse = false;
        await _rig.Alerts().RaiseAsync(new ApprovalAlert(_rig.OrgId, item.Id, item.CreatedAt), CancellationToken.None);

        Assert.Single(_rig.Email.Snapshot());
    }

    // ─── Quiet hours (AC-NF65-3) ───

    [Fact]
    public async Task NewItem_InTheQuietHours_PushAndEmailWaitForTheMorning_TheCounterDoesNot()
    {
        await _rig.SeedAsync();
        await _rig.ChooseAsync(ApprovalQueueRig.Owner, NotificationChannels.MobilePush | NotificationChannels.Email | NotificationChannels.CounterOnly);
        _rig.Clock.SetUtcNow(new DateTimeOffset(ApprovalQueueRig.Night));

        var item = await _rig.SubmitDraftAsync(_rig.Service(), _rig.PropertyA);

        Assert.Single(await _rig.Db.InAppNotifications.AsNoTracking().ToListAsync(), n => n.UserId == ApprovalQueueRig.Owner);
        Assert.Empty(_rig.Push.Queued);
        Assert.Empty(_rig.Email.Snapshot());
        var (queued, enqueueAt) = Assert.Single(_rig.Scheduled<QueuedApprovalAlert>(typeof(ApprovalAlertJob)));
        Assert.Equal(ApprovalQueueRig.MorningDue, enqueueAt);
        Assert.Equal(ApprovalQueueRig.MorningDue, queued.DueAtUtc);
        Assert.Equal(ApprovalQueueRig.Owner, queued.UserId);
        Assert.Equal(item.Id, queued.ItemId);
        Assert.Equal(NotificationChannels.MobilePush | NotificationChannels.Email, queued.Channels);

        // In the morning the job delivers what waited, and the bell is not written a second time.
        _rig.Clock.SetUtcNow(new DateTimeOffset(ApprovalQueueRig.MorningDue));
        await _rig.Alerts().DeliverDeferredAsync(queued, CancellationToken.None);

        Assert.Single(await _rig.Db.InAppNotifications.AsNoTracking().ToListAsync(), n => n.UserId == ApprovalQueueRig.Owner);
        var push = Assert.Single(_rig.Push.Queued);
        Assert.Equal(PushDeliveryKeys.ApprovalQueueItem(item.Id, item.CreatedAt, ApprovalQueueRig.Owner), push.DeliveryKey);
        Assert.Single(_rig.Email.Snapshot());
    }

    [Fact]
    public async Task DeferredAlert_OfAnItemDecidedMeanwhile_TellsNobody()
    {
        await _rig.SeedAsync();
        await _rig.ChooseAsync(ApprovalQueueRig.Owner, NotificationChannels.MobilePush | NotificationChannels.CounterOnly);
        _rig.Clock.SetUtcNow(new DateTimeOffset(ApprovalQueueRig.Night));
        var service = _rig.Service();
        var item = await _rig.SubmitDraftAsync(service, _rig.PropertyA);
        var (queued, _) = Assert.Single(_rig.Scheduled<QueuedApprovalAlert>(typeof(ApprovalAlertJob)));
        await service.RejectAsync(_rig.OrgWide, item.Id, ApprovalQueueRig.Owner, "Non serve più");

        await _rig.Alerts().DeliverDeferredAsync(queued, CancellationToken.None);

        Assert.Empty(_rig.Push.Queued);
    }

    [Fact]
    public async Task TheArgumentsOfTheScheduledJobs_SurviveTheSerializationOfHangfire()
    {
        await _rig.SeedAsync();
        await _rig.ChooseAsync(ApprovalQueueRig.Owner, NotificationChannels.MobilePush | NotificationChannels.Email | NotificationChannels.CounterOnly);
        _rig.Clock.SetUtcNow(new DateTimeOffset(ApprovalQueueRig.Night));
        await _rig.SubmitDraftAsync(_rig.Service(), _rig.PropertyA);
        var job = _rig.Created.Select(c => c.Job).Single(j => j.Type == typeof(ApprovalAlertJob));

        var stored = InvocationData.SerializeJob(job).DeserializeJob();

        var args = Assert.IsType<QueuedApprovalAlert>(stored.Args[0]);
        Assert.Equal((QueuedApprovalAlert)job.Args[0], args);
        Assert.Equal(NotificationChannels.MobilePush | NotificationChannels.Email, args.Channels);
        Assert.Equal(DateTimeKind.Utc, args.DueAtUtc.Kind);
        Assert.Equal(nameof(ApprovalAlertJob.DeliverAsync), stored.Method.Name);
    }

    // ─── The "To do" (ADR-026) ───

    [Fact]
    public async Task NewItem_RaisesOneTodo_LinkedToTheItem_AndAnAlertRaisedAgainRaisesNoSecond()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();

        var item = await _rig.SubmitDraftAsync(service, _rig.PropertyA);
        await _rig.Alerts().RaiseAsync(new ApprovalAlert(_rig.OrgId, item.Id, item.CreatedAt), CancellationToken.None);

        var todo = await _rig.Db.InternalActivities.AsNoTracking().SingleAsync();
        Assert.Equal(todo.Id, item.ActivityId);
        Assert.Equal(todo.Id, (await _rig.Db.ApprovalQueueItems.AsNoTracking().SingleAsync()).ActivityId);
        Assert.Equal(ActivityType.MessageToApprove.ToString(), todo.ActivityType);
        Assert.Equal(ActivityAction.ApproveAiDraft, todo.Action);
        Assert.Equal("ApprovalQueueItem", todo.EntityType);
        Assert.Equal(item.Id, todo.EntityId);
        Assert.Equal(_rig.PropertyA.Id, todo.PropertyId);
        Assert.Equal(_rig.OrgId, todo.OrgId);
        Assert.Equal(item.ExpiresAt, todo.Deadline);
        Assert.Equal(ActivityUrgency.Medium, todo.Urgency);
        Assert.Equal(ApprovalQueueRig.Situation, todo.Reason);
        Assert.Null(todo.CompletedAt);
    }

    [Fact]
    public async Task Approving_ClosesTheTodoOfTheItem_AndTheExpiryDoesToo()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var approved = await _rig.SubmitDraftAsync(service, _rig.PropertyA);
        await service.ApproveAsync(_rig.OrgWide, approved.Id, ApprovalQueueRig.Owner);

        var todo = await _rig.Db.InternalActivities.AsNoTracking().SingleAsync(a => a.EntityId == approved.Id);

        Assert.Equal(InternalActivity.StatusCompleted, todo.Status);
        Assert.Equal(ApprovalQueueRig.Noon, todo.CompletedAt);
        Assert.Equal(ApprovalQueueRig.Owner, todo.ClosedBy);
        Assert.Empty(await _rig.Activities().GetPendingAsync(_rig.OrgWide, CancellationToken.None));
    }

    // ─── Nothing of the text, nothing twice, nothing lost ───

    /// <summary>
    /// Privacy rule: the words of the item (a message to a guest, which may name the guest and an IBAN) are in no push, e-mail or notification of the alert: the property and the
    /// kind of item only.
    /// </summary>
    [Fact]
    public async Task NewItem_TheTextOfTheItemIsInNoPushEmailOrNotification_Test()
    {
        await _rig.SeedAsync();
        await _rig.ChooseAsync(ApprovalQueueRig.Owner, NotificationChannels.MobilePush | NotificationChannels.Email | NotificationChannels.CounterOnly);

        await _rig.SubmitDraftAsync(_rig.Service(), _rig.PropertyA, text: Secret);

        var email = Assert.Single(_rig.Email.Snapshot());
        var everything = string.Join(
            "\n",
            _rig.Push.Queued.Select(p => $"{p.DeliveryKey} {p.Payload.Title} {p.Payload.Body} {p.Payload.Route}")
                .Concat([email.Content.Subject, email.Content.HtmlBody])
                .Concat((await _rig.Db.InAppNotifications.AsNoTracking().ToListAsync()).Select(n => $"{n.Type} {n.DeliveryKey} {n.EntityId}")));
        Assert.DoesNotContain("Zefirino", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("IT60X0542811101000000123456", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("Gentile", everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewItem_ThatCannotBeAlertedNow_IsStillSaved_AndAJobRaisesTheAlertAgain()
    {
        await _rig.SeedAsync();
        var refusing = new RefusingPushQueue();
        var service = new ApprovalQueueService(
            _rig.Db,
            _rig.Clock,
            _rig.Registry(),
            _rig.Activities(),
            new ApprovalAlertDelivery(
                _rig.Db,
                _rig.Preferences(),
                _rig.Activities(),
                refusing,
                _rig.Email,
                _rig.Publisher.Object,
                _rig.Jobs.Object,
                EmailTestHelpers.Links(),
                NullLogger<ApprovalAlertDelivery>.Instance),
            NullLogger<ApprovalQueueService>.Instance);

        var item = await _rig.SubmitDraftAsync(service, _rig.PropertyA);

        // The item is there and its "To do" was raised even though the push was refused; the whole alert is handed to a job.
        Assert.Equal(ApprovalStatus.Pending, (await _rig.Db.ApprovalQueueItems.AsNoTracking().SingleAsync(i => i.Id == item.Id)).Status);
        Assert.NotNull(item.ActivityId);
        var (retry, _) = Assert.Single(_rig.Scheduled<ApprovalAlert>(typeof(ApprovalAlertJob)));
        Assert.Equal(new ApprovalAlert(_rig.OrgId, item.Id, item.CreatedAt), retry);

        // The argument of the retry job is a positional record: it has to come back from the JSON of Hangfire as it went in, with its instant still UTC.
        var job = _rig.Created.Select(c => c.Job).Single(j => j.Type == typeof(ApprovalAlertJob) && j.Method.Name == nameof(ApprovalAlertJob.RaiseAsync));
        var stored = InvocationData.SerializeJob(job).DeserializeJob();
        var back = Assert.IsType<ApprovalAlert>(stored.Args[0]);
        Assert.Equal(retry, back);
        Assert.Equal(DateTimeKind.Utc, back.OccurredAtUtc.Kind);

        // The job runs when the push can be queued: the bell was written once already, and is not written again.
        await _rig.Alerts().RaiseAsync(retry, CancellationToken.None);
        Assert.Single(await _rig.Db.InAppNotifications.AsNoTracking().ToListAsync());
        Assert.Single(_rig.Push.Queued);
        Assert.Single(await _rig.Db.InternalActivities.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task TheSameItemSubmittedAgain_IsTheOneThatWaits_AndTellsNobodyTwice()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var itemId = Guid.NewGuid();
        var thread = ApprovalQueueRig.OtaKey();

        var first = await service.SubmitForApprovalAsync(
            _rig.OrgId, ApprovalItemTypes.MessageDraft, itemId, ApprovalQueueRig.DraftText, true, propertyId: _rig.PropertyA.Id, threadKey: thread);
        var again = await service.SubmitForApprovalAsync(
            _rig.OrgId, ApprovalItemTypes.MessageDraft, itemId, ApprovalQueueRig.DraftText, true, propertyId: _rig.PropertyA.Id, threadKey: thread);
        var other = await service.SubmitForApprovalAsync(
            _rig.OrgId, ApprovalItemTypes.MessageDraft, itemId, "Un altro testo per lo stesso messaggio", true, propertyId: _rig.PropertyA.Id, threadKey: thread);

        Assert.Equal(first.Id, again.Id);
        Assert.NotEqual(first.Id, other.Id);
        Assert.Equal(2, await _rig.Db.ApprovalQueueItems.CountAsync());
        // One bell and one To do for each of the two items; the push is queued again with the same key, which the delivery turns into one push per device.
        Assert.Equal(2, await _rig.Db.InAppNotifications.CountAsync());
        Assert.Equal(2, await _rig.Db.InternalActivities.CountAsync());
        var pushesOfTheFirst = _rig.Push.Queued.Where(p => p.Payload.ApprovalItemId == first.Id).ToList();
        Assert.Equal(2, pushesOfTheFirst.Count);
        Assert.Single(pushesOfTheFirst.Select(p => p.DeliveryKey).Distinct());
    }

    [Fact]
    public async Task ReturnedToPending_WithANewText_IsAlertedAgainWithKeysOfItsOwn_AndTheOldEditIsDropped()
    {
        await _rig.SeedAsync();
        var service = _rig.Service();
        var itemId = Guid.NewGuid();
        var first = await service.SubmitForApprovalAsync(
            _rig.OrgId, ApprovalItemTypes.MessageDraft, itemId, ApprovalQueueRig.DraftText, true, propertyId: _rig.PropertyA.Id, threadKey: ApprovalQueueRig.OtaKey());
        await service.EditAsync(_rig.OrgWide, first.Id, "Un testo modificato", ApprovalQueueRig.Owner);
        await service.ApproveAsync(_rig.OrgWide, first.Id, ApprovalQueueRig.Owner);
        _rig.Clock.Advance(TimeSpan.FromMinutes(5));

        var returned = await service.ReturnToPendingIfChangedAsync(ApprovalItemTypes.MessageDraft, itemId, "Il testo nuovo del messaggio");

        Assert.True(returned);
        var stored = await _rig.Db.ApprovalQueueItems.AsNoTracking().SingleAsync();
        Assert.Equal(ApprovalStatus.Pending, stored.Status);
        Assert.Equal("Il testo nuovo del messaggio", stored.ContentText);
        Assert.Null(stored.EditedContentText);
        Assert.Equal(ApprovalOriginActionStatus.Pending, stored.OriginActionStatus);
        Assert.Null(stored.OriginActionExecutedAt);
        Assert.Null(stored.ApprovedByUserId);
        // A second alert, with its own bell and its own To do (the first one was closed by the approval).
        Assert.Equal(2, await _rig.Db.InAppNotifications.CountAsync());
        var todos = await _rig.Db.InternalActivities.AsNoTracking().OrderBy(a => a.CreatedAt).ToListAsync();
        Assert.Equal(2, todos.Count);
        Assert.NotNull(todos[0].CompletedAt);
        Assert.Null(todos[1].CompletedAt);
        Assert.Equal(todos[1].Id, stored.ActivityId);
    }

    private sealed class RefusingPushQueue : IPushNotificationService
    {
        public bool Enqueue(string deliveryKey, PushAudience audience, PushNotificationPayload payload) => false;
    }
}
