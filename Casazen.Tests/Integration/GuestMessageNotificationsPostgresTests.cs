using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Casazen.Tests.Unit.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// The notifications of the guest messages on a real PostgreSQL database (the service uses <c>ExecuteUpdate</c>, which the in-memory
/// provider does not run): one push to each host the preferences select (<c>INotificationPreferenceService</c>, NF-65) for every new
/// message of the guest (not for a replayed one, and without name or text), the counter and the email that the same preferences allow, the
/// unread notifications of the booking cleared when the host reads or answers, and the unread counters per booking.
/// </summary>
public class GuestMessageNotificationsPostgresTests : IAsyncLifetime
{
    private const string HostUser = "auth0|host-one";
    private const string OtherUser = "auth0|host-two";
    private const string GuestName = "Zefirino";
    private const string Secret = "secret-text-of-the-guest";

    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Now));
    private readonly Mock<IPushNotificationService> _push = new();
    private readonly Mock<IEmailQueue> _email = new();
    private readonly Mock<IInAppNotificationPublisher> _publisher = new();
    private readonly Mock<IBackgroundJobClient> _jobs = new();
    private readonly Mock<IContextAuthorizationService> _permissions = new();
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateMigratedAsync();
        _email.Setup(e => e.Enqueue(It.IsAny<string?>(), It.IsAny<EmailContent>(), It.IsAny<string>())).Returns(true);
        _push.Setup(p => p.Enqueue(It.IsAny<string>(), It.IsAny<PushAudience>(), It.IsAny<PushNotificationPayload>())).Returns(true);
        _permissions
            .Setup(p => p.HasPermissionOrThrowAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    // ─── The push ───

    [PostgresFact]
    public async Task SendAsGuest_NewMessage_QueuesOnePushToTheHostWhoReachesTheBooking_WithoutNameOrText()
    {
        var world = await SeedAsync();

        var sent = await SendAsGuestAsync(world, Secret, "key-00000001");

        Assert.False(sent.Replayed);
        var call = Assert.Single(_push.Invocations);
        Assert.Equal(PushDeliveryKeys.GuestMessage(sent.Message.Id), call.Arguments[0]);
        Assert.Equal(PushAudience.User(world.OrgId, HostUser), call.Arguments[1]);
        var payload = Assert.IsType<PushNotificationPayload>(call.Arguments[2]);
        Assert.Equal(PushTypes.GuestMessage, payload.Type);
        Assert.Equal(world.BookingId, payload.BookingId);
        Assert.Equal(PushRoutes.BookingMessages(world.BookingId), payload.Route);
        Assert.True(PushRoutes.IsAppRoute(payload.Route));
        Assert.Contains("Casa Messaggi", payload.Body, StringComparison.Ordinal);
        foreach (var forbidden in new[] { GuestName, Secret })
        {
            Assert.DoesNotContain(forbidden, payload.Title + payload.Body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [PostgresFact]
    public async Task SendAsGuest_SameIdempotencyKeyTwice_QueuesThePushOnce()
    {
        var world = await SeedAsync();

        await SendAsGuestAsync(world, "uno", "key-00000002");
        var replay = await SendAsGuestAsync(world, "uno", "key-00000002");

        Assert.True(replay.Replayed);
        Assert.Single(_push.Invocations);
    }

    [PostgresFact]
    public async Task SendAsGuest_TwoMessagesInTheEmailWindow_QueuesTwoPushesAndOneEmailToTheHostsOwnAddress()
    {
        var world = await SeedAsync();
        await ChooseEmailAsync(HostUser);

        var first = await SendAsGuestAsync(world, "uno", "key-00000003");
        _clock.Advance(TimeSpan.FromMinutes(1));
        var second = await SendAsGuestAsync(world, "due", "key-00000004");

        var keys = _push.Invocations.Select(i => (string)i.Arguments[0]).ToList();
        Assert.Equal([PushDeliveryKeys.GuestMessage(first.Message.Id), PushDeliveryKeys.GuestMessage(second.Message.Id)], keys);
        _email.Verify(e => e.Enqueue(It.IsAny<string?>(), It.IsAny<EmailContent>(), It.IsAny<string>()), Times.Once);
        _email.Verify(e => e.Enqueue(world.HostEmail, It.IsAny<EmailContent>(), EmailTemplates.Names.GuestMessageToHost), Times.Once);

        // The window is over ten minutes after the first email: the next message queues the next email.
        _clock.Advance(TimeSpan.FromMinutes(10));
        await SendAsGuestAsync(world, "tre", "key-00000010");
        _email.Verify(e => e.Enqueue(world.HostEmail, It.IsAny<EmailContent>(), It.IsAny<string>()), Times.Exactly(2));
    }

    [PostgresFact]
    public async Task SendAsGuest_ByDefault_TheHostGetsPushAndCounter_AndNoEmail()
    {
        var world = await SeedAsync();

        var sent = await SendAsGuestAsync(world, "uno", "key-00000011");

        await using var db = NewDb();
        var notification = await db.InAppNotifications.AsNoTracking().SingleAsync();
        Assert.Equal(HostUser, notification.UserId);
        Assert.Equal(PushTypes.GuestMessage, notification.Type);
        Assert.Equal(world.BookingId, notification.EntityId);
        Assert.Equal(PushDeliveryKeys.GuestMessage(sent.Message.Id), notification.DeliveryKey);
        _publisher.Verify(p => p.PublishAsync(HostUser, PushTypes.GuestMessage, world.BookingId, It.IsAny<CancellationToken>()), Times.Once);
        _email.Verify(e => e.Enqueue(It.IsAny<string?>(), It.IsAny<EmailContent>(), It.IsAny<string>()), Times.Never);
    }

    [PostgresFact]
    public async Task SendAsGuest_ACollaboratorWithoutTheProperty_IsNotAlerted()
    {
        var world = await SeedAsync(collaboratorInChargeWithoutAccess: true);

        await SendAsGuestAsync(world, "uno", "key-00000012");

        // The person named in charge does not reach the property (it was not given): only the administrators hear about it.
        var call = Assert.Single(_push.Invocations);
        Assert.Equal(PushAudience.User(world.OrgId, HostUser), call.Arguments[1]);
        await using var db = NewDb();
        Assert.Equal([HostUser], await db.InAppNotifications.AsNoTracking().Select(n => n.UserId).ToListAsync());
    }

    [PostgresFact]
    public async Task SendAsGuest_QuietHours_HoldThePushAndTheEmail_NotTheCounter()
    {
        var world = await SeedAsync();
        await ChooseEmailAsync(HostUser);
        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 9, 21, 30, 0, TimeSpan.Zero)); // 23:30 in Rome (summer time)

        await SendAsGuestAsync(world, "notte", "key-00000013");

        Assert.Empty(_push.Invocations);
        _email.Verify(e => e.Enqueue(It.IsAny<string?>(), It.IsAny<EmailContent>(), It.IsAny<string>()), Times.Never);
        await using var db = NewDb();
        Assert.Single(await db.InAppNotifications.AsNoTracking().ToListAsync());
        var job = Assert.Single(_jobs.Invocations, i => i.Method.Name == "Create");
        var state = Assert.IsType<Hangfire.States.ScheduledState>(job.Arguments[1]);
        Assert.Equal(new DateTime(2026, 10, 10, 6, 0, 0, DateTimeKind.Utc), state.EnqueueAt);
    }

    [PostgresFact]
    public async Task SendAsGuest_ThePushFails_TheMessageIsSavedAndTheSendSucceeds()
    {
        var world = await SeedAsync();
        _push.Reset();
        _push.Setup(p => p.Enqueue(It.IsAny<string>(), It.IsAny<PushAudience>(), It.IsAny<PushNotificationPayload>()))
            .Throws(new InvalidOperationException("queue down"));

        var sent = await SendAsGuestAsync(world, "uno", "key-00000005");

        Assert.False(sent.Replayed);
        await using var db = NewDb();
        Assert.Equal(1, await db.GuestMessages.CountAsync());

        // The alert is not lost: a job delivers it again (the keys of the notification and of the push and the slot of the email are idempotent).
        var retry = Assert.Single(_jobs.Invocations, i => i.Method.Name == "Create");
        var job = Assert.IsType<Hangfire.Common.Job>(retry.Arguments[0]);
        Assert.Equal(typeof(HostMessageAlertDueJob), job.Type);
        Assert.Equal(nameof(HostMessageAlertDueJob.RetryAsync), job.Method.Name);
        var alert = Assert.IsType<HostMessageAlert>(job.Args[0]);
        Assert.Equal((HostMessageSource.Direct, sent.Message.Id), (alert.Source, alert.MessageId));
    }

    // ─── Reading clears the notifications ───

    [PostgresFact]
    public async Task MarkRead_ClearsTheCallersUnreadGuestMessageNotificationsOfTheBooking_AndPublishesOnce()
    {
        var world = await SeedAsync();
        var otherBooking = Guid.NewGuid();
        var mine = await AddNotificationAsync(world, HostUser, PushTypes.GuestMessage, world.BookingId);
        var mineOtherBooking = await AddNotificationAsync(world, HostUser, PushTypes.GuestMessage, otherBooking);
        var mineOtherType = await AddNotificationAsync(world, HostUser, PushTypes.NewBooking, world.BookingId);
        var colleague = await AddNotificationAsync(world, OtherUser, PushTypes.GuestMessage, world.BookingId);

        await using (var db = NewDb())
            await Service(db).MarkReadAsync(world.BookingId, HostUser);

        Assert.NotNull((await ReloadAsync(mine)).ReadAt);
        Assert.Null((await ReloadAsync(mineOtherBooking)).ReadAt);
        Assert.Null((await ReloadAsync(mineOtherType)).ReadAt);
        Assert.Null((await ReloadAsync(colleague)).ReadAt);
        _publisher.Verify(p => p.PublishAsync(HostUser, PushTypes.GuestMessage, world.BookingId, It.IsAny<CancellationToken>()), Times.Once);
        _publisher.VerifyNoOtherCalls();
    }

    [PostgresFact]
    public async Task MarkRead_NothingUnreadToClear_PublishesNothing()
    {
        var world = await SeedAsync();

        await using (var db = NewDb())
            await Service(db).MarkReadAsync(world.BookingId, HostUser);

        _publisher.VerifyNoOtherCalls();
    }

    [PostgresFact]
    public async Task MarkRead_WithoutAUser_StillMarksTheThreadAndTouchesNoNotification()
    {
        var world = await SeedAsync();
        var mine = await AddNotificationAsync(world, HostUser, PushTypes.GuestMessage, world.BookingId);

        await using (var db = NewDb())
            await Service(db).MarkReadAsync(world.BookingId, null);

        Assert.Null((await ReloadAsync(mine)).ReadAt);
        _publisher.VerifyNoOtherCalls();
    }

    [PostgresFact]
    public async Task ReplyAsHost_CountsAsReading_ClearsTheCallersNotificationsAndPublishes()
    {
        var world = await SeedAsync();
        await SendAsGuestAsync(world, "uno", "key-00000006");
        var mine = await AddNotificationAsync(world, HostUser, PushTypes.GuestMessage, world.BookingId);

        // The arrival of the message of the guest is an event of its own: the delivery of the alert (HostMessageAlertDelivery, NF-65) writes the
        // counter of the bell of the host and tells its open sessions once, right there. It used to be a Hangfire job that nothing ran in this test,
        // so the publisher saw only the reply. What is under test starts here: one reply, one telling.
        Assert.Single(_publisher.Invocations, i => i.Method.Name == nameof(IInAppNotificationPublisher.PublishAsync));
        _publisher.Invocations.Clear();

        await using (var db = NewDb())
            await Service(db).ReplyAsHostAsync(world.BookingId, "ciao", "key-00000007", HostUser);

        // Answering is reading: both the row of the test and the one the alert wrote are read, and the reply tells the sessions once (a second
        // reply, with nothing left unread, would tell nobody: see MarkRead_NothingUnreadToClear_PublishesNothing).
        Assert.NotNull((await ReloadAsync(mine)).ReadAt);
        await using (var check = NewDb())
        {
            var rows = await check.InAppNotifications.AsNoTracking().Where(n => n.UserId == HostUser && n.EntityId == world.BookingId).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, n => Assert.NotNull(n.ReadAt));
        }

        _publisher.Verify(p => p.PublishAsync(HostUser, PushTypes.GuestMessage, world.BookingId, It.IsAny<CancellationToken>()), Times.Once);
        _publisher.VerifyNoOtherCalls();
    }

    [PostgresFact]
    public async Task MarkRead_ThePublisherFails_TheCallStillSucceeds()
    {
        var world = await SeedAsync();
        await AddNotificationAsync(world, HostUser, PushTypes.GuestMessage, world.BookingId);
        _publisher.Setup(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("hub down"));

        await using var db = NewDb();
        var view = await Service(db).MarkReadAsync(world.BookingId, HostUser);

        Assert.Equal(0, view.UnreadCount);
    }

    // ─── Unread per booking ───

    [PostgresFact]
    public async Task ListUnreadForHost_OpenThreadsWithUnreadMessages_GiveCountersOnly_AndReadingEmptiesThem()
    {
        var world = await SeedAsync();
        var readBooking = await SeedBookingAsync(world, checkOutInDays: 3);
        var closedBooking = await SeedBookingAsync(world, checkOutInDays: -30);
        await SendAsGuestAsync(world, Secret, "key-00000008");
        await SendAsGuestAsync(world, Secret + "2", "key-00000009");
        await SendAsGuestAsync(world, "letto", "key-0000000a", readBooking);
        await using (var db = NewDb())
            await Service(db).MarkReadAsync(readBooking.BookingId, null);
        await SendClosedThreadMessageAsync(closedBooking);

        IReadOnlyList<HostUnreadThread> unread;
        await using (var db = NewDb())
            unread = await Service(db).ListUnreadForHostAsync(new HostScope(world.OrgId));

        var item = Assert.Single(unread);
        Assert.Equal(world.BookingId, item.BookingId);
        Assert.Equal(2, item.UnreadCount);
        Assert.DoesNotContain(Secret, System.Text.Json.JsonSerializer.Serialize(unread), StringComparison.Ordinal);

        await using (var db = NewDb())
            await Service(db).MarkReadAsync(world.BookingId, null);
        await using (var db = NewDb())
            Assert.Empty(await Service(db).ListUnreadForHostAsync(new HostScope(world.OrgId)));
    }

    [PostgresFact]
    public async Task ListUnreadForHost_AnotherOrgOrAScopeWithoutTheProperty_SeesNothing()
    {
        var world = await SeedAsync();
        await SendAsGuestAsync(world, "uno", "key-0000000b");

        await using var db = NewDb();
        var service = Service(db);

        Assert.Empty(await service.ListUnreadForHostAsync(new HostScope(Guid.NewGuid())));
        Assert.Empty(await service.ListUnreadForHostAsync(new HostScope(world.OrgId, GrantedToUserId: "auth0|nobody")));
        Assert.Single(await service.ListUnreadForHostAsync(new HostScope(world.OrgId)));
    }

    // ─── Helpers ───

    private sealed record World(Guid OrgId, Guid BookingId, Guid PropertyId, string HostEmail);

    private AppDbContext NewDb() => _database!.CreateContext();

    private GuestMessageService Service(AppDbContext db)
    {
        var links = new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://public.example.test/" }));
        var delivery = new HostMessageAlertDelivery(
            db,
            new NotificationPreferenceService(db, _permissions.Object, _clock, NullLogger<NotificationPreferenceService>.Instance),
            _push.Object,
            _email.Object,
            _publisher.Object,
            _jobs.Object,
            links,
            NullLogger<HostMessageAlertDelivery>.Instance);
        return new GuestMessageService(
            db,
            _email.Object,
            links,
            delivery,
            new InAppNotificationService(db, _clock, NullLogger<InAppNotificationService>.Instance),
            _publisher.Object,
            NullLogger<GuestMessageService>.Instance,
            _clock);
    }

    private async Task<World> SeedAsync(bool collaboratorInChargeWithoutAccess = false)
    {
        await using var db = NewDb();
        var org = OrgTeamTestData.AddOrg(db);
        org.ContactEmail = "host@example.com";
        var host = OrgTeamTestData.AddUser(db, HostUser, org.Id, UserRole.PropertyOwner);
        OrgTeamTestData.AddUser(db, OtherUser, org.Id, UserRole.PropertyOwner);
        if (collaboratorInChargeWithoutAccess)
        {
            // The host is the owner of the org; the other person is a collaborator "Solo alcuni" with no property, named in charge of this one.
            OrgTeamTestData.AddMember(db, HostUser, org.Id, OrgRole.Owner);
            OrgTeamTestData.AddMember(db, OtherUser, org.Id, OrgRole.Collaborator).PropertyScope = PropertyScope.Selected;
        }

        var property = new Property
        {
            OwnerId = HostUser,
            ResponsibleUserId = collaboratorInChargeWithoutAccess ? OtherUser : null,
            OrgId = org.Id,
            Name = "Casa Messaggi",
            Address = $"Via Messaggi {Guid.NewGuid():N}",
            City = "Roma",
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 100m,
            CinCode = "IT058091C27G5FFZDZ",
            IsActive = true,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        var booking = await AddBookingAsync(db, org.Id, property.Id, checkOutInDays: 3);
        return new World(org.Id, booking, property.Id, host.Email);
    }

    /// <summary>The user chooses the email next to the defaults of a new guest message (a push and the counter).</summary>
    private async Task ChooseEmailAsync(string userId)
    {
        await using var db = NewDb();
        await new NotificationPreferenceService(db, _permissions.Object, _clock, NullLogger<NotificationPreferenceService>.Instance).SaveAsync(
            userId,
            new NotificationPreferencesUpdate(
                null,
                [new NotificationEventUpdate(
                    NotificationEventType.NewGuestMessage,
                    NotificationChannels.MobilePush | NotificationChannels.Email | NotificationChannels.CounterOnly)]),
            CancellationToken.None);
    }

    private async Task<World> SeedBookingAsync(World world, int checkOutInDays)
    {
        await using var db = NewDb();
        return world with { BookingId = await AddBookingAsync(db, world.OrgId, world.PropertyId, checkOutInDays) };
    }

    private static async Task<Guid> AddBookingAsync(AppDbContext db, Guid orgId, Guid propertyId, int checkOutInDays)
    {
        var guest = new Guest { OrgId = orgId, FirstName = GuestName, LastName = "Ospite", Email = $"{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            OrgId = orgId,
            PropertyId = propertyId,
            Guest = guest,
            Source = BookingSource.Direct,
            Status = BookingStatus.Confirmed,
            CheckInDate = Now.Date.AddDays(checkOutInDays - 2),
            CheckOutDate = Now.Date.AddDays(checkOutInDays),
        };
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking.Id;
    }

    private async Task<SentGuestMessage> SendAsGuestAsync(World world, string text, string key, World? other = null)
    {
        await using var db = NewDb();
        var bookingId = (other ?? world).BookingId;
        var booking = await db.Bookings.Include(b => b.Property).Include(b => b.Org).Include(b => b.Guest).SingleAsync(b => b.Id == bookingId);
        return await Service(db).SendAsGuestAsync(booking, text, key);
    }

    /// <summary>A message in a thread that is closed by now: written when the clock was inside the stay, then the clock moved on.</summary>
    private async Task SendClosedThreadMessageAsync(World closed)
    {
        // The service refuses to take a message on a closed thread, so the row is written directly, as an older message would be.
        await using var db = NewDb();
        var thread = new GuestThread
        {
            OrgId = closed.OrgId,
            PropertyId = closed.PropertyId,
            BookingId = closed.BookingId,
            CreatedAt = Now.AddDays(-40),
            LastMessageAt = Now.AddDays(-40),
            LastGuestMessageAt = Now.AddDays(-40),
        };
        db.GuestThreads.Add(thread);
        await db.SaveChangesAsync();
        db.GuestMessages.Add(new GuestMessage
        {
            OrgId = closed.OrgId,
            ThreadId = thread.Id,
            Sender = GuestMessageSender.Guest,
            Text = "vecchio",
            CreatedAt = Now.AddDays(-40),
            IdempotencyKey = "key-closed-0001",
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> AddNotificationAsync(World world, string userId, string type, Guid entityId)
    {
        await using var db = NewDb();
        var row = new InAppNotification
        {
            OrgId = world.OrgId,
            UserId = userId,
            Type = type,
            EntityId = entityId,
            DeliveryKey = $"test:{Guid.NewGuid():N}",
            CreatedAt = Now,
        };
        db.InAppNotifications.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private async Task<InAppNotification> ReloadAsync(Guid id)
    {
        await using var db = NewDb();
        return await db.InAppNotifications.AsNoTracking().SingleAsync(n => n.Id == id);
    }
}
