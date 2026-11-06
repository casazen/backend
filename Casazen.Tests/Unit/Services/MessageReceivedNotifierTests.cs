using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Push;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E05/F03/US01/T03 (NF-65, ADR-032-R12): a guest writes in a conversation of a channel and the hosts the preferences select are alerted through the
/// counter, the push and the email they chose, once per event, with the quiet hours held back and no text of the message anywhere. EF InMemory; the
/// push queue, the email queue, the bell and Hangfire are doubles that record what they are given.
/// </summary>
public class MessageReceivedNotifierTests : IDisposable
{
    private const string Owner = "auth0|owner";
    private const string Collaborator = "auth0|collaborator";
    private const string GuestName = "Zefirino Ospite";
    private const string GuestAddress = "zefirino.ospite@example.org";
    private const string SecretText = "Ciao, il mio IBAN è IT60X0542811101000000123456, chiamami al 333 1234567 o scrivi a zefirino.ospite@example.org";

    // 12:00 in Rome (November: UTC+1), outside the quiet hours.
    private static readonly DateTime Noon = new(2026, 11, 6, 11, 0, 0, DateTimeKind.Utc);

    // 23:00 in Rome, inside the quiet hours that end at 08:00 (07:00 UTC).
    private static readonly DateTime Night = new(2026, 11, 6, 22, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MorningDue = new(2026, 11, 7, 7, 0, 0, DateTimeKind.Utc);

    private readonly AppDbContext _db = OrgTeamTestData.NewDb();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Noon));
    private readonly RecordingPushQueue _push = new();
    private readonly RecordingEmailQueue _email = new();
    private readonly Mock<IInAppNotificationPublisher> _publisher = new();
    private readonly Mock<IBackgroundJobClient> _jobs = new();
    private readonly Mock<IContextAuthorizationService> _permissions = new();
    private readonly List<(Job Job, IState State)> _created = [];

    public MessageReceivedNotifierTests()
    {
        _permissions
            .Setup(p => p.HasPermissionOrThrowAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _jobs
            .Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Callback<Job, IState>((job, state) => _created.Add((job, state)))
            .Returns("job-1");
    }

    public void Dispose() => _db.Dispose();

    private NotificationPreferenceService Preferences() =>
        new(_db, _permissions.Object, _clock, NullLogger<NotificationPreferenceService>.Instance);

    private HostMessageAlertDelivery Delivery(IPushNotificationService? push = null) =>
        new(
            _db,
            Preferences(),
            push ?? _push,
            _email,
            _publisher.Object,
            _jobs.Object,
            EmailTestHelpers.Links(),
            NullLogger<HostMessageAlertDelivery>.Instance);

    private MessageReceivedNotifier Notifier(IPushNotificationService? push = null) =>
        new(Delivery(push), NullLogger<MessageReceivedNotifier>.Instance);

    private sealed record World(
        Guid OrgId,
        Property PropertyA,
        Property PropertyB,
        (OtaMessageThread Thread, OtaMessage Message) OnA,
        (OtaMessageThread Thread, OtaMessage Message) OnB,
        (OtaMessageThread Thread, OtaMessage Message) Unassigned,
        string OwnerEmail);

    /// <summary>
    /// A host org with an owner and a collaborator limited to the property A (named in charge of A and of B, granted A only); a conversation of a channel
    /// on A, one on B and one not mapped to any property, each with an inbound message that holds the guest's name and words.
    /// </summary>
    private async Task<World> SeedAsync()
    {
        var org = OrgTeamTestData.AddOrg(_db);
        var owner = OrgTeamTestData.AddUser(_db, Owner, org.Id, UserRole.PropertyOwner);
        OrgTeamTestData.AddUser(_db, Collaborator, org.Id, UserRole.None);
        OrgTeamTestData.AddMember(_db, Owner, org.Id, OrgRole.Owner);
        OrgTeamTestData.AddMember(_db, Collaborator, org.Id, OrgRole.Collaborator).PropertyScope = PropertyScope.Selected;

        var a = NewProperty(org.Id, "Casa Rosa", Collaborator);
        var b = NewProperty(org.Id, "Casa Blu", Collaborator);
        _db.Properties.AddRange(a, b);
        _db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = org.Id, UserId = Collaborator, PropertyId = a.Id });

        var onA = NewConversation(org.Id, a.Id, "7001");
        var onB = NewConversation(org.Id, b.Id, "7002");
        var unassigned = NewConversation(org.Id, null, "7003");
        _db.OtaMessageThreads.AddRange(onA.Thread, onB.Thread, unassigned.Thread);
        _db.OtaMessages.AddRange(onA.Message, onB.Message, unassigned.Message);
        await _db.SaveChangesAsync();
        return new World(org.Id, a, b, onA, onB, unassigned, owner.Email);
    }

    private static Property NewProperty(Guid orgId, string name, string? responsible) => new()
    {
        OrgId = orgId,
        OwnerId = Owner,
        ResponsibleUserId = responsible,
        Name = name,
        Address = "Via Segreta 1",
        City = "Roma",
        PostalCode = "00100",
        Bedrooms = 1,
        Bathrooms = 1,
        MaxGuests = 2,
        NightlyRate = 100m,
    };

    private static (OtaMessageThread Thread, OtaMessage Message) NewConversation(Guid orgId, Guid? propertyId, string octorateThreadId)
    {
        var thread = new OtaMessageThread
        {
            OrgId = orgId,
            PropertyId = propertyId,
            OctorateThreadId = octorateThreadId,
            OctorateAccommodationId = $"acc-{octorateThreadId}",
            ChannelSource = OtaChannel.Airbnb,
            Kind = OtaThreadKind.Request,
            GuestDisplayName = GuestName,
            LastMessageAt = Noon,
            LastMessageDirection = OtaMessageDirection.Inbound,
            UnreadCount = 1,
        };
        var message = new OtaMessage
        {
            OrgId = orgId,
            ThreadId = thread.Id,
            OctorateMessageId = $"m-{octorateThreadId}",
            Direction = OtaMessageDirection.Inbound,
            SenderName = GuestName,
            Text = SecretText,
            SentAt = Noon,
            DeliveryState = OtaMessageDeliveryState.Sent,
            Origin = MessageOrigin.Guest,
        };
        return (thread, message);
    }

    private static DomainEvent Received(Guid orgId, (OtaMessageThread Thread, OtaMessage Message) conversation, DateTime at, string source = "Ota") =>
        new(
            Guid.NewGuid(),
            orgId,
            DomainEventType.GuestMessageReceived,
            conversation.Message.Id,
            at,
            JsonSerializer.Serialize(new GuestMessageReceivedPayload(
                $"ota:{conversation.Thread.Id:D}",
                source,
                conversation.Thread.PropertyId,
                conversation.Thread.BookingId,
                nameof(OtaChannel.Airbnb))));

    private async Task ChooseAsync(string userId, NotificationEventType type, NotificationChannels channels) =>
        await Preferences().SaveAsync(userId, new NotificationPreferencesUpdate(null, [new NotificationEventUpdate(type, channels)]), CancellationToken.None);

    private List<(QueuedMessageAlert Args, DateTime EnqueueAt)> ScheduledAlerts() =>
        _created
            .Where(c => c.Job.Type == typeof(HostMessageAlertDueJob))
            .Select(c => ((QueuedMessageAlert)c.Job.Args[0], ((ScheduledState)c.State).EnqueueAt))
            .ToList();

    // ─── The defaults (AC-NF65-2) and the route (AC-NF65-5) ───

    [Fact]
    public async Task Handle_ANewMessage_GivesTheOwnerTheCounterAndThePush_ByDefault()
    {
        var world = await SeedAsync();

        await Notifier().HandleAsync(Received(world.OrgId, world.OnA, Noon), CancellationToken.None);

        var bell = (await _db.InAppNotifications.AsNoTracking().ToListAsync()).Single(n => n.UserId == Owner);
        Assert.Equal(PushTypes.OtaMessage, bell.Type);
        Assert.Equal(world.OnA.Thread.Id, bell.EntityId);
        Assert.Equal(PushDeliveryKeys.OtaMessage(world.OnA.Message.Id, Owner), bell.DeliveryKey);
        _publisher.Verify(p => p.PublishAsync(Owner, PushTypes.OtaMessage, world.OnA.Thread.Id, It.IsAny<CancellationToken>()), Times.Once);

        var push = Assert.Single(_push.Queued, p => p.Audience == PushAudience.User(world.OrgId, Owner));
        Assert.Equal(PushDeliveryKeys.OtaMessage(world.OnA.Message.Id, Owner), push.DeliveryKey);
        Assert.Equal(PushTypes.OtaMessage, push.Payload.Type);
        Assert.Equal($"/messages/ota:{world.OnA.Thread.Id:D}", push.Payload.Route);
        Assert.Equal(PushRoutes.MessageThread($"ota:{world.OnA.Thread.Id:D}"), push.Payload.Route);
        Assert.True(PushRoutes.IsAppRoute(push.Payload.Route));
        Assert.Contains("Casa Rosa", push.Payload.Body, StringComparison.Ordinal);
        Assert.Contains("Airbnb", push.Payload.Body, StringComparison.Ordinal);

        // The email is not a default.
        Assert.Empty(_email.Snapshot());
        Assert.Empty(ScheduledAlerts());
    }

    [Fact]
    public async Task Handle_TheSameEventTwice_NotifiesOnce()
    {
        var world = await SeedAsync();
        await ChooseAsync(Owner, NotificationEventType.NewGuestMessage, NotificationChannels.MobilePush | NotificationChannels.Email | NotificationChannels.CounterOnly);
        var received = Received(world.OrgId, world.OnA, Noon);

        await Notifier().HandleAsync(received, CancellationToken.None);
        await Notifier().HandleAsync(received, CancellationToken.None);

        // One row of the bell for each of the two people (the owner and the collaborator in charge), one tell to the open sessions of each, one
        // email; the push goes again with the same key, which the delivery turns into one push per device.
        Assert.Equal(2, await _db.InAppNotifications.CountAsync());
        _publisher.Verify(p => p.PublishAsync(Owner, PushTypes.OtaMessage, world.OnA.Thread.Id, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single(_email.Snapshot(), e => e.To == world.OwnerEmail);
        var ownerPushes = _push.Queued.Where(p => p.Audience == PushAudience.User(world.OrgId, Owner)).ToList();
        Assert.Equal(2, ownerPushes.Count);
        Assert.All(ownerPushes, p => Assert.Equal(PushDeliveryKeys.OtaMessage(world.OnA.Message.Id, Owner), p.DeliveryKey));
    }

    // ─── Email: the person's own address, once every ten minutes per conversation (AC-NF65-6) ───

    [Fact]
    public async Task Handle_TheEmailGoesToThePersonsOwnAddress_AtMostOneEveryTenMinutesForAConversation()
    {
        var world = await SeedAsync();
        await ChooseAsync(Owner, NotificationEventType.NewGuestMessage, NotificationChannels.Email | NotificationChannels.CounterOnly);
        var second = (world.OnA.Thread, NewFollowUp(world.OnA.Thread, "m-7001-b", Noon.AddMinutes(2)));
        var third = (world.OnA.Thread, NewFollowUp(world.OnA.Thread, "m-7001-c", Noon.AddMinutes(11)));
        var other = world.OnB;
        _db.OtaMessages.AddRange(second.Item2, third.Item2);
        await _db.SaveChangesAsync();

        await Notifier().HandleAsync(Received(world.OrgId, world.OnA, Noon), CancellationToken.None);
        await Notifier().HandleAsync(Received(world.OrgId, second, Noon.AddMinutes(2)), CancellationToken.None);
        Assert.Single(_email.Snapshot());

        await Notifier().HandleAsync(Received(world.OrgId, third, Noon.AddMinutes(11)), CancellationToken.None);
        await Notifier().HandleAsync(Received(world.OrgId, other, Noon.AddMinutes(11)), CancellationToken.None);

        var sent = _email.Snapshot();
        Assert.Equal(3, sent.Count);
        Assert.All(sent, e => Assert.Equal(world.OwnerEmail, e.To));
        Assert.All(sent, e => Assert.Equal(EmailTemplates.Names.HostMessageReceived, e.Template));
        Assert.Contains("Casa Rosa", sent[0].Content.Subject, StringComparison.Ordinal);
        Assert.Contains($"/app/short-rent/messages/ota:{world.OnA.Thread.Id:D}", sent[0].Content.HtmlBody, StringComparison.Ordinal);
        // No push to the owner, who chose the email and the counter (the collaborator in charge of A has the default).
        Assert.DoesNotContain(_push.Queued, p => p.Audience.UserId == Owner);
    }

    [Fact]
    public async Task Handle_AnEmailTheQueueCannotTake_GivesTheSlotBack()
    {
        var world = await SeedAsync();
        await ChooseAsync(Owner, NotificationEventType.NewGuestMessage, NotificationChannels.Email);
        _email.Refuse = true;

        await Notifier().HandleAsync(Received(world.OrgId, world.OnA, Noon), CancellationToken.None);
        _email.Refuse = false;
        await Notifier().HandleAsync(Received(world.OrgId, world.OnA, Noon.AddMinutes(1)), CancellationToken.None);

        // The first one was not queued (no provider): the next message tries again, it does not wait ten minutes.
        Assert.Single(_email.Snapshot());
    }

    // ─── Quiet hours (AC-NF65-3) ───

    [Fact]
    public async Task Handle_InTheQuietHours_PushAndEmailWaitForTheMorning_TheCounterDoesNot()
    {
        var world = await SeedAsync();
        await ChooseAsync(Owner, NotificationEventType.NewGuestMessage, NotificationChannels.MobilePush | NotificationChannels.Email | NotificationChannels.CounterOnly);

        await Notifier().HandleAsync(Received(world.OrgId, world.OnA, Night), CancellationToken.None);

        Assert.Single(await _db.InAppNotifications.AsNoTracking().Where(n => n.UserId == Owner).ToListAsync());
        Assert.Empty(_push.Queued);
        Assert.Empty(_email.Snapshot());
        var (args, enqueueAt) = Assert.Single(ScheduledAlerts(), a => a.Args.UserId == Owner);
        Assert.Equal(MorningDue, enqueueAt);
        Assert.Equal(MorningDue, args.DueAtUtc);
        Assert.Equal(Owner, args.UserId);
        Assert.Equal(NotificationChannels.MobilePush | NotificationChannels.Email, args.Channels);
    }

    [Fact]
    public async Task Handle_TheMessagesOfTheNight_AreOneEmailInTheMorning_AndAPushEach()
    {
        var world = await SeedAsync();
        await ChooseAsync(Owner, NotificationEventType.NewGuestMessage, NotificationChannels.MobilePush | NotificationChannels.Email | NotificationChannels.CounterOnly);
        var later = (world.OnA.Thread, NewFollowUp(world.OnA.Thread, "m-7001-n", Night.AddHours(2)));
        _db.OtaMessages.Add(later.Item2);
        await _db.SaveChangesAsync();

        await Notifier().HandleAsync(Received(world.OrgId, world.OnA, Night), CancellationToken.None);
        await Notifier().HandleAsync(Received(world.OrgId, later, Night.AddHours(2)), CancellationToken.None);

        var scheduled = ScheduledAlerts().Where(a => a.Args.UserId == Owner).ToList();
        Assert.Equal(2, scheduled.Count);
        Assert.Equal(NotificationChannels.MobilePush | NotificationChannels.Email, scheduled[0].Args.Channels);
        Assert.Equal(NotificationChannels.MobilePush, scheduled[1].Args.Channels);
    }

    [Fact]
    public async Task Handle_TheArgumentsOfTheScheduledJob_SurviveTheSerializationOfHangfire()
    {
        var world = await SeedAsync();
        await ChooseAsync(Owner, NotificationEventType.NewGuestMessage, NotificationChannels.MobilePush | NotificationChannels.Email | NotificationChannels.CounterOnly);
        await Notifier().HandleAsync(Received(world.OrgId, world.OnA, Night), CancellationToken.None);
        var job = _created.Select(c => c.Job).First(j => j.Type == typeof(HostMessageAlertDueJob) && ((QueuedMessageAlert)j.Args[0]).UserId == Owner);

        var stored = InvocationData.SerializeJob(job).DeserializeJob();

        var args = Assert.IsType<QueuedMessageAlert>(stored.Args[0]);
        Assert.Equal((QueuedMessageAlert)job.Args[0], args);
        Assert.Equal(HostMessageSource.Ota, args.Source);
        Assert.Equal(NotificationChannels.MobilePush | NotificationChannels.Email, args.Channels);
        Assert.Equal(MorningDue, args.DueAtUtc);
        Assert.Equal(nameof(HostMessageAlertDueJob.DeliverAsync), stored.Method.Name);
    }

    [Fact]
    public async Task DueJob_DeliversWhatWaited_ToThePersonWhoStillWantsIt()
    {
        var world = await SeedAsync();
        await ChooseAsync(Owner, NotificationEventType.NewGuestMessage, NotificationChannels.MobilePush | NotificationChannels.Email | NotificationChannels.CounterOnly);
        await Notifier().HandleAsync(Received(world.OrgId, world.OnA, Night), CancellationToken.None);
        var (args, _) = Assert.Single(ScheduledAlerts(), a => a.Args.UserId == Owner);
        _clock.SetUtcNow(new DateTimeOffset(MorningDue));

        await new HostMessageAlertDueJob(Delivery()).DeliverAsync(args, CancellationToken.None);

        var push = Assert.Single(_push.Queued);
        Assert.Equal(PushAudience.User(world.OrgId, Owner), push.Audience);
        Assert.Equal(PushDeliveryKeys.OtaMessage(world.OnA.Message.Id, Owner), push.DeliveryKey);
        var email = Assert.Single(_email.Snapshot());
        Assert.Equal(world.OwnerEmail, email.To);
    }

    [Fact]
    public async Task DueJob_ThePersonChangedItsChoiceMeanwhile_DeliversNothing()
    {
        var world = await SeedAsync();
        await ChooseAsync(Owner, NotificationEventType.NewGuestMessage, NotificationChannels.MobilePush | NotificationChannels.Email | NotificationChannels.CounterOnly);
        await Notifier().HandleAsync(Received(world.OrgId, world.OnA, Night), CancellationToken.None);
        var (args, _) = Assert.Single(ScheduledAlerts(), a => a.Args.UserId == Owner);
        await ChooseAsync(Owner, NotificationEventType.NewGuestMessage, NotificationChannels.CounterOnly);
        _clock.SetUtcNow(new DateTimeOffset(MorningDue));

        await new HostMessageAlertDueJob(Delivery()).DeliverAsync(args, CancellationToken.None);

        Assert.Empty(_push.Queued);
        Assert.Empty(_email.Snapshot());
    }

    [Fact]
    public async Task DueJob_ThePersonLostThePermissionMeanwhile_IsNotTold()
    {
        var world = await SeedAsync();
        await Notifier().HandleAsync(Received(world.OrgId, world.OnA, Night), CancellationToken.None);
        var (args, _) = Assert.Single(ScheduledAlerts(), a => a.Args.UserId == Owner);
        _permissions
            .Setup(p => p.HasPermissionOrThrowAsync(Owner, "short-rent", "booking.read", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _clock.SetUtcNow(new DateTimeOffset(MorningDue));

        await new HostMessageAlertDueJob(Delivery()).DeliverAsync(args, CancellationToken.None);

        Assert.Empty(_push.Queued);
    }

    // ─── Who is told (AC-NF65-4) ───

    [Fact]
    public async Task Handle_ACollaboratorLimitedToSomeProperties_IsNotToldAboutTheThreadsOfTheOthers()
    {
        var world = await SeedAsync();

        await Notifier().HandleAsync(Received(world.OrgId, world.OnA, Noon), CancellationToken.None);
        var forA = _push.Queued.Select(p => p.Audience.UserId).Order(StringComparer.Ordinal).ToList();
        await Notifier().HandleAsync(Received(world.OrgId, world.OnB, Noon), CancellationToken.None);
        var forAAndB = _push.Queued.Select(p => p.Audience.UserId).Order(StringComparer.Ordinal).ToList();

        Assert.Equal([Collaborator, Owner], forA);
        // The thread of B adds the owner only: the collaborator was never given B.
        Assert.Equal([Collaborator, Owner, Owner], forAAndB);
        Assert.Equal(
            [Collaborator, Owner, Owner],
            (await _db.InAppNotifications.AsNoTracking().Select(n => n.UserId).ToListAsync()).Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task Handle_AConversationNotMappedToAProperty_GoesToTheAdministratorsOnly()
    {
        var world = await SeedAsync();

        await Notifier().HandleAsync(Received(world.OrgId, world.Unassigned, Noon), CancellationToken.None);

        var push = Assert.Single(_push.Queued);
        Assert.Equal(PushAudience.User(world.OrgId, Owner), push.Audience);
        // No property to name: the text names the channel.
        Assert.DoesNotContain("Casa", push.Payload.Body, StringComparison.Ordinal);
        Assert.Contains("Airbnb", push.Payload.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handle_APersonWhoCannotOpenTheThread_IsNotTold()
    {
        var world = await SeedAsync();
        _permissions
            .Setup(p => p.HasPermissionOrThrowAsync(Owner, "short-rent", "booking.read", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await Notifier().HandleAsync(Received(world.OrgId, world.OnA, Noon), CancellationToken.None);

        Assert.Equal([Collaborator], _push.Queued.Select(p => p.Audience.UserId).ToList());
    }

    [Fact]
    public async Task Handle_AnErrorReadingThePermissions_Throws_SoTheBusDeliversTheEventAgain_AndNobodyIsDroppedForIt()
    {
        var world = await SeedAsync();
        _permissions
            .Setup(p => p.HasPermissionOrThrowAsync(Owner, "short-rent", "booking.read", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("the database does not answer"));

        await Assert.ThrowsAsync<TimeoutException>(() => Notifier().HandleAsync(Received(world.OrgId, world.OnA, Noon), CancellationToken.None));
        Assert.Empty(_push.Queued);

        // The event is delivered again, the database answers: the owner is told as if nothing had happened.
        _permissions
            .Setup(p => p.HasPermissionOrThrowAsync(Owner, "short-rent", "booking.read", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        await Notifier().HandleAsync(Received(world.OrgId, world.OnA, Noon), CancellationToken.None);

        Assert.Contains(_push.Queued, p => p.Audience.UserId == Owner);
    }

    [Fact]
    public async Task Handle_ThePreferencesOfTheUrgentKind_AreItsOwn()
    {
        var world = await SeedAsync();
        await ChooseAsync(Owner, NotificationEventType.NewGuestMessage, NotificationChannels.None);

        await Delivery().DeliverAsync(
            new HostMessageAlert(world.OrgId, HostMessageSource.Ota, NotificationEventType.NewGuestMessage, world.OnA.Thread.Id, world.OnA.Message.Id, Noon),
            CancellationToken.None);
        Assert.DoesNotContain(_push.Queued, p => p.Audience.UserId == Owner);

        await Delivery().DeliverAsync(
            new HostMessageAlert(world.OrgId, HostMessageSource.Ota, NotificationEventType.UrgentGuestMessage, world.OnA.Thread.Id, world.OnA.Message.Id, Noon),
            CancellationToken.None);
        Assert.Contains(_push.Queued, p => p.Audience.UserId == Owner);
    }

    // ─── What the handler ignores ───

    [Fact]
    public async Task Handle_AnotherSourceAnotherEventOrABrokenPayload_DoesNothing()
    {
        var world = await SeedAsync();
        var notifier = Notifier();

        await notifier.HandleAsync(Received(world.OrgId, world.OnA, Noon, source: "Direct"), CancellationToken.None);
        await notifier.HandleAsync(Received(world.OrgId, world.OnA, Noon) with { Type = DomainEventType.BookingConfirmed }, CancellationToken.None);
        await notifier.HandleAsync(Received(world.OrgId, world.OnA, Noon) with { PayloadJson = "not json" }, CancellationToken.None);
        await notifier.HandleAsync(Received(world.OrgId, world.OnA, Noon) with { PayloadJson = "[]" }, CancellationToken.None);
        await notifier.HandleAsync(Received(world.OrgId, world.OnA, Noon) with { EntityId = Guid.Empty }, CancellationToken.None);
        await notifier.HandleAsync(
            Received(world.OrgId, world.OnA, Noon) with { PayloadJson = JsonSerializer.Serialize(new GuestMessageReceivedPayload("direct:" + Guid.NewGuid(), "Ota", null, null, "Airbnb")) },
            CancellationToken.None);

        Assert.Empty(_push.Queued);
        Assert.Empty(await _db.InAppNotifications.ToListAsync());
    }

    [Fact]
    public async Task Handle_AThreadOrAMessageOfAnotherOrg_IsNeverRead()
    {
        var world = await SeedAsync();
        var otherOrg = OrgTeamTestData.AddOrg(_db);
        await _db.SaveChangesAsync();

        // The event says another org: the thread of this org is not found in it, and nobody is told.
        await Notifier().HandleAsync(Received(otherOrg.Id, world.OnA, Noon), CancellationToken.None);

        Assert.Empty(_push.Queued);
        Assert.Empty(await _db.InAppNotifications.ToListAsync());
    }

    [Fact]
    public async Task Handle_AMessageTheHostWrote_IsNeverAlerted()
    {
        var world = await SeedAsync();
        var outbound = await _db.OtaMessages.SingleAsync(m => m.Id == world.OnA.Message.Id);
        outbound.Direction = OtaMessageDirection.Outbound;
        await _db.SaveChangesAsync();

        await Notifier().HandleAsync(Received(world.OrgId, world.OnA, Noon), CancellationToken.None);

        Assert.Empty(_push.Queued);
    }

    // ─── Failures: the bus delivers the event again ───

    [Fact]
    public async Task Handle_APushThatCannotBeQueued_ThrowsAfterTheOthersWereTold_SoTheBusDeliversTheEventAgain()
    {
        var world = await SeedAsync();
        var refusing = new Mock<IPushNotificationService>();
        refusing
            .Setup(p => p.Enqueue(It.IsAny<string>(), It.Is<PushAudience>(a => a.UserId == Collaborator), It.IsAny<PushNotificationPayload>()))
            .Returns(false);
        refusing
            .Setup(p => p.Enqueue(It.IsAny<string>(), It.Is<PushAudience>(a => a.UserId == Owner), It.IsAny<PushNotificationPayload>()))
            .Returns(true);

        await Assert.ThrowsAsync<AggregateException>(
            () => Notifier(refusing.Object).HandleAsync(Received(world.OrgId, world.OnA, Noon), CancellationToken.None));

        // The owner was told in spite of the collaborator's failure; the counters of both are written, and a redelivery writes none again.
        refusing.Verify(p => p.Enqueue(It.IsAny<string>(), PushAudience.User(world.OrgId, Owner), It.IsAny<PushNotificationPayload>()), Times.Once);
        Assert.Equal(2, await _db.InAppNotifications.CountAsync());
        await Assert.ThrowsAsync<AggregateException>(
            () => Notifier(refusing.Object).HandleAsync(Received(world.OrgId, world.OnA, Noon), CancellationToken.None));
        Assert.Equal(2, await _db.InAppNotifications.CountAsync());
    }

    // ─── Privacy (ADR-022-R21, ADR-032-R13) ───

    /// <summary>
    /// PROOF TEST: privacy rule. A guest's message carries personal data (words, an IBAN, a phone number, an address, a name). Whatever the host
    /// chose (counter, push and email), none of them reaches a push, an email, a notification of the bell or the arguments of a job.
    /// </summary>
    [Fact]
    public async Task MessageNotifier_NoMessageText_InPushEmailOrNotification_Test()
    {
        var world = await SeedAsync();
        var everything = NotificationChannels.MobilePush | NotificationChannels.Email | NotificationChannels.CounterOnly;
        await ChooseAsync(Owner, NotificationEventType.NewGuestMessage, everything);
        await ChooseAsync(Collaborator, NotificationEventType.NewGuestMessage, everything);
        var atNight = (world.OnA.Thread, NewFollowUp(world.OnA.Thread, "m-7001-night", Night));
        _db.OtaMessages.Add(atNight.Item2);
        await _db.SaveChangesAsync();

        await Notifier().HandleAsync(Received(world.OrgId, world.OnA, Noon), CancellationToken.None);
        await Notifier().HandleAsync(Received(world.OrgId, atNight, Night), CancellationToken.None);
        await Notifier().HandleAsync(Received(world.OrgId, world.Unassigned, Night), CancellationToken.None);

        var payloads = new List<string>();
        payloads.AddRange(_push.Queued.Select(p => JsonSerializer.Serialize(p)));
        payloads.AddRange(_email.Snapshot().Select(e => $"{e.To}|{e.Content.Subject}|{e.Content.HtmlBody}"));
        payloads.AddRange((await _db.InAppNotifications.AsNoTracking().ToListAsync()).Select(n => JsonSerializer.Serialize(new { n.Type, n.EntityId, n.DeliveryKey, n.UserId })));
        payloads.AddRange(ScheduledAlerts().Select(s => JsonSerializer.Serialize(s.Args)));
        payloads.AddRange(_publisher.Invocations.Select(i => string.Join('|', i.Arguments)));

        // The three channels were really used, so the proof looks at something.
        Assert.NotEmpty(_push.Queued);
        Assert.NotEmpty(_email.Snapshot());
        Assert.NotEmpty(await _db.InAppNotifications.ToListAsync());
        Assert.NotEmpty(ScheduledAlerts());

        string[] forbidden =
        [
            SecretText,
            "IT60X0542811101000000123456",
            "333 1234567",
            GuestAddress,
            GuestName,
            "Zefirino",
            "Via Segreta",
        ];
        foreach (var payload in payloads)
        {
            foreach (var secret in forbidden)
                Assert.DoesNotContain(secret, payload, StringComparison.OrdinalIgnoreCase);
        }

        // The email names the property and the channel and sends the host to the conversation.
        var email = _email.Snapshot().First(e => e.Content.Subject.Contains("Casa Rosa", StringComparison.Ordinal));
        Assert.Contains("Airbnb", email.Content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Casa Rosa", email.Content.HtmlBody, StringComparison.Ordinal);
    }

    private static OtaMessage NewFollowUp(OtaMessageThread thread, string octorateMessageId, DateTime sentAt) => new()
    {
        OrgId = thread.OrgId,
        ThreadId = thread.Id,
        OctorateMessageId = octorateMessageId,
        Direction = OtaMessageDirection.Inbound,
        SenderName = GuestName,
        Text = SecretText,
        SentAt = sentAt,
        DeliveryState = OtaMessageDeliveryState.Sent,
        Origin = MessageOrigin.Guest,
    };
}
