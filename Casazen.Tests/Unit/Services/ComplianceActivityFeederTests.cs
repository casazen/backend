using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The feed of the compliance cockpit into the unified "To do" (E03/F02/US01/T03, NF-52, AC-NF52-13, ADR-026): one To do for each red or yellow item,
/// with the action and the id of the cockpit item, deduplicated by (action, target, period), closed when the item turns green or no longer applies;
/// only against what the evaluation looked at; and the operational items of the cockpit left to their own producers.
/// </summary>
public class ComplianceActivityFeederTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Feed_RaisesOneTodoForEachItemThatNeedsAttention_WithTheActionAndTheIdOfTheCockpit()
    {
        var world = await FeedWorld.SeedAsync();
        var deadline = Start.UtcDateTime.AddHours(5);
        var stay = Guid.NewGuid();
        var entrusted = Guid.NewGuid();
        var manual = Guid.NewGuid();
        var identity = Guid.NewGuid();
        var done = Guid.NewGuid();
        var items = new[]
        {
            Stay(world.Property, stay, ComplianceCockpitAction.SendAlloggiati, Red(ComplianceReasonCodes.DeadlineOverdue), deadline),
            Stay(world.Property, entrusted, ComplianceCockpitAction.UploadAlloggiatiReceipt, Yellow(ComplianceReasonCodes.EntrustedToChannelManager)),
            Stay(world.Property, manual, ComplianceCockpitAction.SendAlloggiatiManually, Red(ComplianceReasonCodes.OverThirtyNightsNotSent)),
            Stay(world.Property, identity, ComplianceCockpitAction.VerifyIdentity, Red(ComplianceReasonCodes.IdentityMissing), kind: ComplianceItemKind.Identity),
            Stay(world.Property, done, ComplianceCockpitAction.UploadAlloggiatiReceipt, Green()),
            Property(world.Property, ComplianceCockpitAction.ActivateOctorateSend, ComplianceItemKind.Connection, Red(ComplianceReasonCodes.ConnectionInactive)),
            Property(world.Property, ComplianceCockpitAction.RenewPoliceCredentials, ComplianceItemKind.Credentials, Red(ComplianceReasonCodes.CredentialsRefused)),
            Property(world.Property, ComplianceCockpitAction.EnterMunicipalityTaxRate, ComplianceItemKind.MunicipalityRate, Red(ComplianceReasonCodes.MunicipalityRateMissing)),
            Property(world.Property, ComplianceCockpitAction.UploadIstatProof, ComplianceItemKind.Istat, Yellow(ComplianceReasonCodes.PeriodOpen), period: "2026-10"),
            Property(world.Property, ComplianceCockpitAction.DeclareTouristTax, ComplianceItemKind.TouristTax, Red(ComplianceReasonCodes.EvidenceMissing), period: "2026-10"),
            Property(world.Property, ComplianceCockpitAction.DeclareTouristTax, ComplianceItemKind.TouristTax, Red(ComplianceReasonCodes.EvidenceMissing), period: "2026-09"),
            // The operational items of the cockpit keep their own producers.
            Property(world.Property, ComplianceCockpitAction.ActivateProperty, ComplianceItemKind.Cin, Red(ComplianceReasonCodes.CinMissing)),
        };
        var status = Result(items, bookings: [stay, entrusted, manual, done], identity: [identity], properties: [world.Property.Id], periods: ["2026-09", "2026-10"]);

        await using var db = world.Db();
        await world.Feeder(db).FeedAsync(world.Scope, status, CancellationToken.None);

        var todos = await db.InternalActivities.AsNoTracking().Where(a => a.Action != null).ToListAsync();
        Assert.Equal(10, todos.Count);
        Assert.All(todos, t => Assert.Equal<(Guid?, Guid, string)>((world.Property.Id, world.Org.Id, InternalActivity.StatusPending), (t.PropertyId, t.OrgId, t.Status)));

        T Row<T>(Func<InternalActivity, bool> where, Func<InternalActivity, T> pick) => pick(Assert.Single(todos, t => where(t)));

        // The Alloggiati communication to send: the page of the proofs, a high urgency, the deadline and the reason of the item.
        var send = Assert.Single(todos, t => t.EntityId == stay);
        Assert.Equal((ActivityType.AlloggiatiDeadline, ActivityAction.UploadAlloggiatiProof, InternalActivity.BookingEntityType, ActivityUrgency.High, deadline, ComplianceReasonCodes.DeadlineOverdue),
            (Enum.Parse<ActivityType>(send.ActivityType), send.Action!.Value, send.EntityType!, send.Urgency!.Value, send.Deadline!.Value, send.Reason!));
        // Yellow is medium; the receipt to upload has the type the alert stages use; the stay entrusted to the channel manager is a To do too.
        var receipt = Assert.Single(todos, t => t.EntityId == entrusted);
        Assert.Equal((ActivityType.ReceiptToRetrieve.ToString(), ActivityUrgency.Medium), (receipt.ActivityType, receipt.Urgency!.Value));
        Assert.Equal(ActivityAction.SendAlloggiatiManually, Row(t => t.EntityId == manual, t => t.Action!.Value));
        Assert.Equal(ActivityAction.VerifyIdentity, Row(t => t.EntityId == identity, t => t.Action!.Value));
        Assert.DoesNotContain(todos, t => t.EntityId == done);

        // A property item is about the property.
        Assert.Equal(
            (ActivityType.ReconnectChannelManager.ToString(), ActivityAction.ActivateOctorateSend, "Property"),
            Row(t => t.Action == ActivityAction.ActivateOctorateSend, t => (t.ActivityType, t.Action!.Value, t.EntityType!)));
        Assert.Equal(ActivityAction.RenewPoliceCredentials, Row(t => t.ActivityType == nameof(ActivityType.CredentialsToRenew), t => t.Action!.Value));
        Assert.Equal(ActivityAction.EnterMunicipalityTaxRate, Row(t => t.ActivityType == nameof(ActivityType.MunicipalityTaxRateMissing), t => t.Action!.Value));

        // An item of a month is about the property and carries the month: (action, property, month), one for each month.
        var istat = Assert.Single(todos, t => t.Action == ActivityAction.UploadIstatProof);
        Assert.Equal(("PropertyPeriod:2026-10", world.Property.Id), (istat.EntityType, istat.EntityId));
        var taxes = todos.Where(t => t.Action == ActivityAction.DeclareTouristTax).OrderBy(t => t.EntityType, StringComparer.Ordinal).ToList();
        Assert.Equal(["PropertyPeriod:2026-09", "PropertyPeriod:2026-10"], taxes.Select(t => t.EntityType));
        Assert.All(taxes, t => Assert.Equal(world.Property.Id, t.EntityId));

        // The CIN is not one of the items that feed the list.
        Assert.DoesNotContain(todos, t => t.Action == ActivityAction.ResumeWizard);
    }

    [Fact]
    public async Task Feed_IsIdempotent_AndAYellowThatTurnsRedIsStillOneOpenTodo_NowTheRedOne()
    {
        var world = await FeedWorld.SeedAsync();
        var stay = Guid.NewGuid();
        var yellow = Result([Stay(world.Property, stay, ComplianceCockpitAction.UploadAlloggiatiReceipt, Yellow(ComplianceReasonCodes.AwaitingReceipt))], bookings: [stay]);
        var red = Result([Stay(world.Property, stay, ComplianceCockpitAction.UploadAlloggiatiReceipt, Red(ComplianceReasonCodes.ReceiptMissing))], bookings: [stay]);

        await using var db = world.Db();
        await world.Feeder(db).FeedAsync(world.Scope, yellow, CancellationToken.None);
        await world.Feeder(db).FeedAsync(world.Scope, yellow, CancellationToken.None);
        await world.Feeder(db).FeedAsync(world.Scope, red, CancellationToken.None);

        // The same reading twice raised one; the item that turned red has ONE open To do, the red one (high, with its reason): the yellow one it
        // replaced is closed by the system, not left on the list with the old urgency.
        var todos = await db.InternalActivities.AsNoTracking().Where(a => a.Action != null).ToListAsync();
        var only = Assert.Single(todos, t => t.CompletedAt is null);
        Assert.Equal((stay, InternalActivity.StatusPending, ActivityUrgency.High, ComplianceReasonCodes.ReceiptMissing), (only.EntityId, only.Status, only.Urgency!.Value, only.Reason));
        var replaced = Assert.Single(todos, t => t.CompletedAt is not null);
        Assert.Equal((InternalActivity.ClosedBySystem, ActivityUrgency.Medium, ComplianceReasonCodes.AwaitingReceipt), (replaced.ClosedBy, replaced.Urgency!.Value, replaced.Reason));
    }

    [Fact]
    public async Task Feed_ClosesATodoWhenItsItemTurnsGreenOrNoLongerApplies_AndOnlyWithinWhatWasLookedAt()
    {
        var world = await FeedWorld.SeedAsync();
        var turnsGreen = Guid.NewGuid();
        var gone = Guid.NewGuid();
        var stays = Guid.NewGuid();
        var outside = await world.AddStayAsync(checkInOffsetDays: 20);
        var raisedBySomeoneElse = Guid.NewGuid();
        var first = Result(
            [
                Stay(world.Property, turnsGreen, ComplianceCockpitAction.SendAlloggiati, Red(ComplianceReasonCodes.AlloggiatiNotSent)),
                Stay(world.Property, gone, ComplianceCockpitAction.SendAlloggiati, Red(ComplianceReasonCodes.AlloggiatiNotSent)),
                Stay(world.Property, stays, ComplianceCockpitAction.UploadAlloggiatiReceipt, Yellow(ComplianceReasonCodes.AwaitingReceipt)),
                Property(world.Property, ComplianceCockpitAction.ActivateOctorateSend, ComplianceItemKind.Connection, Red(ComplianceReasonCodes.ConnectionInactive)),
                Property(world.Property, ComplianceCockpitAction.DeclareTouristTax, ComplianceItemKind.TouristTax, Red(ComplianceReasonCodes.EvidenceMissing), period: "2026-10"),
            ],
            bookings: [turnsGreen, gone, stays],
            properties: [world.Property.Id],
            periods: ["2026-10"]);

        await using var db = world.Db();
        await world.Feeder(db).FeedAsync(world.Scope, first, CancellationToken.None);

        // A To do the alert stages raised for a stay this evaluation did not look at (it arrives later) and that is still active, and one another
        // producer raised for a stay it did look at, with the same action: the second is the item's To do, the first is nobody's business here.
        var activities = new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, world.Clock);
        await activities.CreateAsync(Todo(world.Property, ActivityType.ReceiptToRetrieve, ActivityAction.UploadAlloggiatiProof, outside), CancellationToken.None);
        await activities.CreateAsync(Todo(world.Property, ActivityType.ReceiptToRetrieve, ActivityAction.UploadAlloggiatiProof, raisedBySomeoneElse), CancellationToken.None);

        // Later: the first stay turns green, the second is gone (not listed any more), the receipt of the third is still awaited, the connection
        // is no longer an item (active), the tax of October is green; the stay of the other producer is in the universe and has no item.
        var second = Result(
            [
                Stay(world.Property, turnsGreen, ComplianceCockpitAction.UploadAlloggiatiReceipt, Green()),
                Stay(world.Property, stays, ComplianceCockpitAction.UploadAlloggiatiReceipt, Yellow(ComplianceReasonCodes.AwaitingReceipt)),
                Property(world.Property, ComplianceCockpitAction.DeclareTouristTax, ComplianceItemKind.TouristTax, Green(), period: "2026-10"),
            ],
            bookings: [turnsGreen, gone, stays, raisedBySomeoneElse],
            properties: [world.Property.Id],
            periods: ["2026-10"]);
        await world.Feeder(db).FeedAsync(world.Scope, second, CancellationToken.None);

        var todos = await db.InternalActivities.AsNoTracking().Where(a => a.Action != null).ToListAsync();
        foreach (var closedId in new[] { turnsGreen, gone, raisedBySomeoneElse })
        {
            var closed = Assert.Single(todos, t => t.EntityId == closedId);
            Assert.Equal((InternalActivity.StatusCompleted, InternalActivity.ClosedBySystem), (closed.Status, closed.ClosedBy));
            Assert.NotNull(closed.CompletedAt);
        }

        Assert.Null(Assert.Single(todos, t => t.EntityId == stays).CompletedAt);
        Assert.Null(Assert.Single(todos, t => t.EntityId == outside).CompletedAt);
        Assert.NotNull(Assert.Single(todos, t => t.Action == ActivityAction.ActivateOctorateSend).CompletedAt);
        Assert.NotNull(Assert.Single(todos, t => t.Action == ActivityAction.DeclareTouristTax).CompletedAt);
    }

    [Fact]
    public async Task Feed_AnItemThatChangesWhatItAsksFor_ReplacesItsTodoInTheSameRun()
    {
        var world = await FeedWorld.SeedAsync();
        var stay = Guid.NewGuid();
        var toSend = Result([Stay(world.Property, stay, ComplianceCockpitAction.SendAlloggiati, Red(ComplianceReasonCodes.AlloggiatiNotSent))], bookings: [stay]);
        var byHand = Result([Stay(world.Property, stay, ComplianceCockpitAction.SendAlloggiatiManually, Red(ComplianceReasonCodes.ConnectionInactive))], bookings: [stay]);

        await using var db = world.Db();
        await world.Feeder(db).FeedAsync(world.Scope, toSend, CancellationToken.None);
        await world.Feeder(db).FeedAsync(world.Scope, byHand, CancellationToken.None);

        var todos = await db.InternalActivities.AsNoTracking().Where(a => a.Action != null).ToListAsync();
        Assert.Equal(2, todos.Count);
        Assert.NotNull(Assert.Single(todos, t => t.Action == ActivityAction.UploadAlloggiatiProof).CompletedAt);
        var open = Assert.Single(todos, t => t.Action == ActivityAction.SendAlloggiatiManually);
        Assert.Null(open.CompletedAt);
        Assert.Equal(ComplianceReasonCodes.ConnectionInactive, open.Reason);
    }

    [Fact]
    public async Task Feed_AnOpenTodoThatNoLongerSaysWhatItsItemSays_IsReplaced_AndOneAnotherProducerRaisedNever()
    {
        var world = await FeedWorld.SeedAsync();
        var stay = Guid.NewGuid();
        var identity = Guid.NewGuid();
        await using var db = world.Db();

        async Task FeedAsync(ComplianceStatusItem item) =>
            await world.Feeder(db).FeedAsync(world.Scope, Result([item], bookings: [stay], identity: [identity]), CancellationToken.None);

        // The communication to send: a To do of the deadline, high.
        await FeedAsync(Stay(world.Property, stay, ComplianceCockpitAction.SendAlloggiati, Red(ComplianceReasonCodes.AlloggiatiNotSent)));
        var toSend = Assert.Single(await OpenOfAsync(db, stay));
        Assert.Equal((nameof(ActivityType.AlloggiatiDeadline), ActivityUrgency.High, ComplianceReasonCodes.AlloggiatiNotSent), (toSend.ActivityType, toSend.Urgency!.Value, toSend.Reason));

        // The host declares the send: the item asks for the receipt now (the same action and id of the To do list, another type, yellow). The To do
        // of the deadline does not stay on the list as it was.
        await FeedAsync(Stay(world.Property, stay, ComplianceCockpitAction.UploadAlloggiatiReceipt, Yellow(ComplianceReasonCodes.AwaitingReceipt)));
        var awaited = Assert.Single(await OpenOfAsync(db, stay));
        Assert.NotEqual(toSend.Id, awaited.Id);
        Assert.Equal((nameof(ActivityType.ReceiptToRetrieve), ActivityUrgency.Medium, ComplianceReasonCodes.AwaitingReceipt), (awaited.ActivityType, awaited.Urgency!.Value, awaited.Reason));

        // The same reading again: nothing changes, not even the row. The receipt is late: the same item turns red and so does its urgency.
        await FeedAsync(Stay(world.Property, stay, ComplianceCockpitAction.UploadAlloggiatiReceipt, Yellow(ComplianceReasonCodes.AwaitingReceipt)));
        Assert.Equal(awaited.Id, Assert.Single(await OpenOfAsync(db, stay)).Id);
        await FeedAsync(Stay(world.Property, stay, ComplianceCockpitAction.UploadAlloggiatiReceipt, Red(ComplianceReasonCodes.ReceiptMissing)));
        var late = Assert.Single(await OpenOfAsync(db, stay));
        Assert.Equal((ActivityUrgency.High, ComplianceReasonCodes.ReceiptMissing), (late.Urgency!.Value, late.Reason));

        // And back to yellow (the connection was fixed): the urgent count does not keep a To do that is no longer red.
        await FeedAsync(Stay(world.Property, stay, ComplianceCockpitAction.UploadAlloggiatiReceipt, Yellow(ComplianceReasonCodes.EntrustedToChannelManager)));
        var again = Assert.Single(await OpenOfAsync(db, stay));
        Assert.Equal((ActivityUrgency.Medium, ComplianceReasonCodes.EntrustedToChannelManager), (again.Urgency!.Value, again.Reason));
        Assert.Equal(1, await db.InternalActivities.CountAsync(a => a.Action != null && a.EntityId == stay && a.CompletedAt == null));

        // A To do another producer raised (the operators' alert: its reason is a text for the people, not a code of the status) is the item's To do as
        // it is: its urgency and its words are not rewritten by the feed.
        var activities = new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, world.Clock);
        const string alertReason = "Verifica dell'identita da decidere";
        var alert = await activities.CreateAsync(
            new CreateTodoRequest
            {
                Type = ActivityType.IdentityVerification,
                Action = ActivityAction.VerifyIdentity,
                EntityType = InternalActivity.BookingEntityType,
                EntityId = identity,
                Urgency = ActivityUrgency.High,
                PropertyId = world.Property.Id,
                Reason = alertReason,
            },
            CancellationToken.None);
        await FeedAsync(Stay(world.Property, identity, ComplianceCockpitAction.VerifyIdentity, Yellow(ComplianceReasonCodes.SelfieAwaited), kind: ComplianceItemKind.Identity));
        var kept = Assert.Single(await OpenOfAsync(db, identity));
        Assert.Equal((alert, ActivityUrgency.High, alertReason), (kept.Id, kept.Urgency!.Value, kept.Reason));
    }

    [Fact]
    public async Task Feed_TheThrottleIsTakenAtTheStart_SoARequestThatArrivesWhileTheFirstRunsDoesNotRepeatTheWork()
    {
        var world = await FeedWorld.SeedAsync();
        var stay = Guid.NewGuid();
        var gate = new TaskCompletionSource<IReadOnlyDictionary<Guid, InternalActivity>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activities = new Mock<IInternalActivityService>();
        activities
            .Setup(a => a.FindOpenAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .Returns(gate.Task);
        activities
            .Setup(a => a.FindOpenInScopeAsync(It.IsAny<HostScope>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        activities.Setup(a => a.CreateAsync(It.IsAny<CreateTodoRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());

        await using var db = world.Db();
        var feeder = new ComplianceActivityFeeder(activities.Object, db, new MemoryCache(new MemoryCacheOptions()), NullLogger<ComplianceActivityFeeder>.Instance);
        var status = Result([Stay(world.Property, stay, ComplianceCockpitAction.SendAlloggiati, Red(ComplianceReasonCodes.AlloggiatiNotSent))], bookings: [stay]);

        // The first run is reading what is open (it waits there); the same caller asks again at that moment, as the Home and the cockpit do: it is
        // left alone at once, and the work is done once.
        var first = feeder.FeedAsync(world.Scope, status, CancellationToken.None);
        await feeder.FeedAsync(world.Scope, status, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(first.IsCompleted);

        gate.SetResult(new Dictionary<Guid, InternalActivity>());
        await first;
        activities.Verify(a => a.CreateAsync(It.IsAny<CreateTodoRequest>(), It.IsAny<CancellationToken>()), Times.Once);

        // A run that fails gives the throttle back: the next read tries again at once.
        var failing = new Mock<IInternalActivityService>();
        failing
            .Setup(a => a.FindOpenAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, InternalActivity>());
        failing
            .Setup(a => a.FindOpenInScopeAsync(It.IsAny<HostScope>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        failing.SetupSequence(a => a.CreateAsync(It.IsAny<CreateTodoRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the To do list is down"))
            .ReturnsAsync(Guid.NewGuid());
        var retrying = new ComplianceActivityFeeder(failing.Object, db, new MemoryCache(new MemoryCacheOptions()), NullLogger<ComplianceActivityFeeder>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => retrying.FeedAsync(world.Scope, status, CancellationToken.None));
        await retrying.FeedAsync(world.Scope, status, CancellationToken.None);
        failing.Verify(a => a.CreateAsync(It.IsAny<CreateTodoRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Feed_ARunDoesAtMostTheLimitOfChanges_TheMostUrgentFirst_AndTheNextReadGoesOnWithWhatIsLeft()
    {
        var world = await FeedWorld.SeedAsync();
        var reds = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToArray();
        var yellows = Enumerable.Range(0, ComplianceActivityFeeder.MaxChangesPerRun).Select(_ => Guid.NewGuid()).ToArray();
        var status = Result(
            [
                .. yellows.Select(id => Stay(world.Property, id, ComplianceCockpitAction.UploadAlloggiatiReceipt, Yellow(ComplianceReasonCodes.AwaitingReceipt))),
                .. reds.Select(id => Stay(world.Property, id, ComplianceCockpitAction.SendAlloggiati, Red(ComplianceReasonCodes.AlloggiatiNotSent))),
            ],
            bookings: [.. reds, .. yellows]);

        await using var db = world.Db();
        await world.Feeder(db).FeedAsync(world.Scope, status, CancellationToken.None);

        // One run is bounded: the red ones are in, whatever the order of the evaluation was.
        var first = await db.InternalActivities.AsNoTracking().Where(a => a.Action != null).ToListAsync();
        Assert.Equal(ComplianceActivityFeeder.MaxChangesPerRun, first.Count);
        Assert.All(reds, id => Assert.Contains(first, t => t.EntityId == id));

        // The next read (a feeder with no throttle yet stands for the time that passed) goes on with the rest and raises nothing twice.
        await world.Feeder(db).FeedAsync(world.Scope, status, CancellationToken.None);
        Assert.Equal(reds.Length + yellows.Length, await db.InternalActivities.CountAsync(a => a.Action != null));
    }

    [Fact]
    public async Task Feed_TheConnectionTodoOfAPropertyWhoseSendWasNotReadIsLeftAlone_NeitherRaisedNorClosed()
    {
        var world = await FeedWorld.SeedAsync();
        await using var db = world.Db();

        var inactive = Result(
            [Property(world.Property, ComplianceCockpitAction.ActivateOctorateSend, ComplianceItemKind.Connection, Red(ComplianceReasonCodes.ConnectionInactive))],
            properties: [world.Property.Id]);
        await world.Feeder(db).FeedAsync(world.Scope, inactive, CancellationToken.None);
        Assert.Single(await db.InternalActivities.AsNoTracking().Where(a => a.Action == ActivityAction.ActivateOctorateSend && a.CompletedAt == null).ToListAsync());

        // This time Octorate was not asked about the property (over the limit of the call): no item, and no reason to close what Octorate said before.
        var notRead = Result([], properties: [world.Property.Id], connectionNotRead: [world.Property.Id]);
        await world.Feeder(db).FeedAsync(world.Scope, notRead, CancellationToken.None);
        Assert.Single(await db.InternalActivities.AsNoTracking().Where(a => a.Action == ActivityAction.ActivateOctorateSend && a.CompletedAt == null).ToListAsync());

        // Read and no longer an item (the send is active): now it is closed.
        await world.Feeder(db).FeedAsync(world.Scope, Result([], properties: [world.Property.Id]), CancellationToken.None);
        Assert.Empty(await db.InternalActivities.AsNoTracking().Where(a => a.Action == ActivityAction.ActivateOctorateSend && a.CompletedAt == null).ToListAsync());
    }

    [Fact]
    public async Task Feed_AnOrgAndScopeThatWereFedAMomentAgoAreLeftAlone()
    {
        var world = await FeedWorld.SeedAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var cache = new MemoryCache(new MemoryCacheOptions());

        await using var db = world.Db();
        var feeder = new ComplianceActivityFeeder(
            new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, world.Clock), db, cache, NullLogger<ComplianceActivityFeeder>.Instance);
        await feeder.FeedAsync(world.Scope, Result([Stay(world.Property, first, ComplianceCockpitAction.SendAlloggiati, Red(ComplianceReasonCodes.AlloggiatiNotSent))], bookings: [first]), CancellationToken.None);
        await feeder.FeedAsync(world.Scope, Result([Stay(world.Property, second, ComplianceCockpitAction.SendAlloggiati, Red(ComplianceReasonCodes.AlloggiatiNotSent))], bookings: [second]), CancellationToken.None);

        Assert.Equal([first], (await db.InternalActivities.AsNoTracking().Where(a => a.Action != null).ToListAsync()).Select(t => t.EntityId!.Value));

        // Another scope of the same org is a read of its own.
        var other = world.Scope with { GrantedToUserId = "auth0|collaborator" };
        await feeder.FeedAsync(other, Result([Stay(world.Property, second, ComplianceCockpitAction.SendAlloggiati, Red(ComplianceReasonCodes.AlloggiatiNotSent))], bookings: [second]), CancellationToken.None);
        Assert.Equal(2, await db.InternalActivities.CountAsync(a => a.Action != null));
    }

    [Fact]
    public async Task Feed_TheIdentityTodoHasTheTypeOfTheOperatorsAlert_SoABookingHasOneOpenItemWhicheverRaisesItFirst()
    {
        var world = await FeedWorld.SeedAsync();
        var raisedByAlert = Guid.NewGuid();
        var raisedByFeed = Guid.NewGuid();
        await using var db = world.Db();
        var activities = new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, world.Clock);

        // The alert to the operators (VerificationOperatorAlerts) came first for one booking: the feed finds it and raises nothing more for it.
        var alert = await activities.CreateAsync(
            Todo(world.Property, ActivityType.IdentityVerification, ActivityAction.VerifyIdentity, raisedByAlert), CancellationToken.None);
        var status = Result(
            [
                Stay(world.Property, raisedByAlert, ComplianceCockpitAction.VerifyIdentity, Red(ComplianceReasonCodes.OperatorDecisionRequired), kind: ComplianceItemKind.Identity),
                Stay(world.Property, raisedByFeed, ComplianceCockpitAction.VerifyIdentity, Yellow(ComplianceReasonCodes.SelfieAwaited), kind: ComplianceItemKind.Identity),
            ],
            identity: [raisedByAlert, raisedByFeed]);
        await world.Feeder(db).FeedAsync(world.Scope, status, CancellationToken.None);

        var todos = await db.InternalActivities.AsNoTracking().Where(a => a.Action == ActivityAction.VerifyIdentity).ToListAsync();
        Assert.Equal(2, todos.Count);
        Assert.Equal(alert, Assert.Single(todos, t => t.EntityId == raisedByAlert).Id);
        Assert.All(todos, t => Assert.Equal(nameof(ActivityType.IdentityVerification), t.ActivityType));

        // The feed came first for the other booking: the alert, when it comes, finds the item of the feed instead of raising another.
        var second = await activities.CreateAsync(
            Todo(world.Property, ActivityType.IdentityVerification, ActivityAction.VerifyIdentity, raisedByFeed), CancellationToken.None);
        Assert.Equal(Assert.Single(todos, t => t.EntityId == raisedByFeed).Id, second);
        Assert.Equal(2, await db.InternalActivities.CountAsync(a => a.Action == ActivityAction.VerifyIdentity));
    }

    [Fact]
    public void FedActions_AndTheOnesLeftToTheirOwnProducers_AreEveryActionOfTheCockpit()
    {
        var fed = ComplianceActivityFeeder.FedActions;
        var notFed = ComplianceActivityFeeder.NotFedActions;

        Assert.Empty(fed.Intersect(notFed));
        Assert.Equal(Enum.GetValues<ComplianceCockpitAction>().Order(), fed.Concat(notFed).Order());
        Assert.Equal(
            new[]
            {
                ComplianceCockpitAction.ActivateProperty, ComplianceCockpitAction.CompleteGuestCheckIn,
                ComplianceCockpitAction.CheckOut, ComplianceCockpitAction.ConfirmPropertyReady,
            }.Order(),
            notFed.Order());
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────────

    private static Task<List<InternalActivity>> OpenOfAsync(AppDbContext db, Guid bookingId) =>
        db.InternalActivities.AsNoTracking().Where(a => a.Action != null && a.EntityId == bookingId && a.CompletedAt == null).ToListAsync();

    private static ComplianceVerdict Red(string reason) => ComplianceVerdict.ActionNeeded(reason);

    private static ComplianceVerdict Yellow(string reason) => ComplianceVerdict.InProgress(reason);

    private static ComplianceVerdict Green() =>
        ComplianceVerdict.Proven(new ComplianceProof("manual-proof:test", ComplianceEvidenceSources.ManualUpload), ComplianceReasonCodes.ReceiptArchived);

    private static ComplianceStatusItem Stay(
        Property property,
        Guid bookingId,
        ComplianceCockpitAction action,
        ComplianceVerdict verdict,
        DateTime? dueAt = null,
        ComplianceItemKind kind = ComplianceItemKind.Alloggiati) =>
        new(kind, action, property.Id, bookingId, null, "Anna Verdi", verdict, dueAt);

    private static ComplianceStatusItem Property(
        Property property,
        ComplianceCockpitAction action,
        ComplianceItemKind kind,
        ComplianceVerdict verdict,
        string? period = null) =>
        new(kind, action, property.Id, null, period, property.Name, verdict, null);

    private static ComplianceStatusResult Result(
        IReadOnlyList<ComplianceStatusItem> items,
        Guid[]? bookings = null,
        Guid[]? identity = null,
        Guid[]? properties = null,
        string[]? periods = null,
        DateTime? windowStart = null,
        Guid[]? connectionNotRead = null) =>
        new(
            items,
            new ComplianceEvaluatedUniverse(
                (bookings ?? []).ToHashSet(),
                (identity ?? []).ToHashSet(),
                (properties ?? []).ToHashSet(),
                (periods ?? []).ToHashSet(StringComparer.Ordinal),
                windowStart,
                (connectionNotRead ?? []).ToHashSet()));

    private static CreateTodoRequest Todo(Property property, ActivityType type, ActivityAction action, Guid bookingId) =>
        new()
        {
            Type = type,
            Action = action,
            EntityType = InternalActivity.BookingEntityType,
            EntityId = bookingId,
            Urgency = ActivityUrgency.High,
            PropertyId = property.Id,
        };

    private sealed class FeedWorld
    {
        private readonly string _database = Guid.NewGuid().ToString();

        public required OrgEntity Org { get; init; }

        public required Property Property { get; init; }

        public FakeTimeProvider Clock { get; } = new(Start);

        public HostScope Scope => new(Org.Id);

        public AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options);

        public ComplianceActivityFeeder Feeder(AppDbContext db) =>
            new(
                new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, Clock),
                db,
                new MemoryCache(new MemoryCacheOptions()),
                NullLogger<ComplianceActivityFeeder>.Instance);

        /// <summary>A stay of the property (and its booker): active unless said otherwise, arriving <paramref name="checkInOffsetDays"/> days from the clock of the test.</summary>
        public async Task<Guid> AddStayAsync(int checkInOffsetDays, BookingStatus status = BookingStatus.Confirmed)
        {
            await using var db = Db();
            var guest = new Guest { OrgId = Org.Id, FirstName = "Anna", LastName = "Verdi", Email = $"{Guid.NewGuid():N}@example.com" };
            var checkIn = new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc).AddDays(checkInOffsetDays);
            var stay = new Booking
            {
                OrgId = Org.Id,
                PropertyId = Property.Id,
                GuestId = guest.Id,
                CheckInDate = checkIn,
                CheckOutDate = checkIn.AddDays(2),
                NumberOfGuests = 1,
                Status = status,
                Source = BookingSource.Manual,
                BasePrice = 300m,
                TotalPrice = 300m,
            };
            db.Guests.Add(guest);
            db.Bookings.Add(stay);
            await db.SaveChangesAsync();
            return stay.Id;
        }

        public static async Task<FeedWorld> SeedAsync()
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var org = HostScopeScenario.NewOrg("Host", $"host-{suffix}");
            var owner = HostScopeScenario.NewUser($"auth0|titolare-{suffix}", org.Id, UserRole.PropertyOwner);
            var property = HostScopeScenario.NewProperty(org.Id, owner.Id, "Trullo");
            var world = new FeedWorld { Org = org, Property = property };
            await using var db = world.Db();
            db.Orgs.Add(org);
            db.Users.Add(owner);
            db.Properties.Add(property);
            await db.SaveChangesAsync();
            return world;
        }
    }
}
