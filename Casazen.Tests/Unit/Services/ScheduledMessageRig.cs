using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Push;
using Hangfire;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// What the tests of the scheduled messages (E05/F07/US01) share: one in-memory database, the plan and the dispatcher built over it with the real "To do" service and the real alert delivery, and the message
/// layer, the access code, the check-in link and the preferences as fakes a test can steer and watch. The message layer keeps the rule of its key: a key that was sent is replayed, never sent again, a key that was
/// refused replays the refusal. The org has an owner (a recipient of the alerts, in charge of the property) and a second person in charge, so the alert to the others has someone to tell.
/// </summary>
internal sealed class ScheduledMessageRig
{
    public static readonly Guid OrgA = Guid.Parse("1a2b3c4d-0001-4000-8000-00000000000a");
    public static readonly Guid OrgB = Guid.Parse("1a2b3c4d-0002-4000-8000-00000000000b");

    public const string Owner = "auth0|owner";
    public const string Manager = "auth0|manager";

    public static readonly DateTimeOffset Now = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);

    private readonly string _database = $"scheduled-messages-{Guid.NewGuid()}";
    private readonly EphemeralDataProtectionProvider _keys = TestDataProtection.Shared;
    private readonly HashSet<string> _delivered = [];
    private readonly HashSet<string> _refused = [];
    private readonly HashSet<string> _refusedBefore = [];
    private int _links;

    public ScheduledMessageRig()
    {
        Replies
            .Setup(r => r.ChooseChannelAsync(It.IsAny<HostScope>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HostScope _, Guid bookingId, CancellationToken _) =>
            {
                if (ChannelThrows is { } outage)
                    throw outage;

                return Channel ?? new MessageContactOptions(MessageContactChannel.Direct, MessageThreadKey.ForDirect(bookingId), null, true, false);
            });
        Replies
            .Setup(r => r.StartConversationAsync(
                It.IsAny<HostScope>(),
                It.IsAny<Guid>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<MessageOrigin>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (HostScope scope, Guid bookingId, string? text, string? key, string? userId, MessageOrigin origin, CancellationToken _) =>
            {
                if (SendGate is { } gate)
                    await gate();

                return Send(scope, bookingId, text, key, userId, origin);
            });
        AccessCodes
            .Setup(a => a.GetReleasableCodeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string channel, CancellationToken _) =>
            {
                Taken.Add(channel);
                return AccessCode;
            });
        AccessCodes
            .Setup(a => a.RecordReleaseAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, string channel, CancellationToken _) =>
            {
                Released.Add(channel);
                return Task.CompletedTask;
            });
        AccessCodes
            .Setup(a => a.GetHoldReasonAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        CheckIn
            .Setup(c => c.IssueLinkAsync(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .ReturnsAsync(() => new IssuedCheckInLink(Guid.NewGuid(), $"token-{++_links}", Now.UtcDateTime.AddDays(7)));
        Preferences
            .Setup(p => p.ResolveAsync(It.IsAny<NotificationEventContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((NotificationEventContext context, CancellationToken _) =>
            {
                Contexts.Add(context);
                return (IReadOnlyList<NotificationRecipient>)
                [
                    new NotificationRecipient(Owner, "owner@example.com", NotificationChannels.CounterOnly, NotificationChannels.None, null),
                    new NotificationRecipient(Manager, "manager@example.com", NotificationChannels.CounterOnly | NotificationChannels.MobilePush, NotificationChannels.None, null),
                ];
            });
        Jobs
            .Setup(j => j.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()))
            .Returns("job-1");
    }

    public TestFeatureFlags Flags { get; } = new(FeatureFlags.ScheduledMessages, FeatureFlags.Routines);

    public ConditionEvaluator Conditions { get; } = new(NullLogger<ConditionEvaluator>.Instance);

    public ScheduledMessageOptions Options { get; } = new();

    public Mock<IMessageReplyService> Replies { get; } = new();

    public Mock<IAccessCodeService> AccessCodes { get; } = new();

    public Mock<IGuestCheckInService> CheckIn { get; } = new();

    public Mock<INotificationPreferenceService> Preferences { get; } = new();

    public Mock<IInAppNotificationPublisher> Publisher { get; } = new();

    public Mock<IBackgroundJobClient> Jobs { get; } = new();

    public RecordingPushQueue Push { get; } = new();

    public RecordingEmailQueue Emails { get; } = new();

    public MessageContactOptions? Channel { get; set; }

    public Exception? ChannelThrows { get; set; }

    public string? AccessCode { get; set; }

    public Func<string?, string?, MessageReplyResult?>? Answer { get; set; }

    public Exception? SendThrows { get; set; }

    public Func<Task>? SendGate { get; set; }

    public OtaMessageDeliveryState State { get; set; } = OtaMessageDeliveryState.Sent;

    public List<SentMessage> Sent { get; } = [];

    public List<string> Released { get; } = [];

    public List<string> Taken { get; } = [];

    public List<NotificationEventContext> Contexts { get; } = [];

    public int Delivered => _delivered.Count;

    public AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options);

    public void Refuse(string key) => _refused.Add(key);

    public ScheduledMessageAlertDelivery Alerts(AppDbContext db) => new(
        db,
        Preferences.Object,
        Push,
        Emails,
        Publisher.Object,
        Jobs.Object,
        EmailTestHelpers.Links(),
        NullLogger<ScheduledMessageAlertDelivery>.Instance);

    public InternalActivityService Activities(AppDbContext db, TimeProvider? clock = null) =>
        new(db, NullLogger<InternalActivityService>.Instance, clock ?? new FixedTimeProvider(Now));

    public IInternalActivityService? ActivitiesDouble { get; set; }

    public ScheduledMessageDispatcher Dispatcher(AppDbContext db, TimeProvider? clock = null) => new(
        db,
        Replies.Object,
        AccessCodes.Object,
        CheckIn.Object,
        ActivitiesDouble ?? Activities(db, clock),
        Alerts(db),
        Conditions,
        EmailTestHelpers.Links(),
        new ReviewLinkTokens(_keys),
        Microsoft.Extensions.Options.Options.Create(Options),
        Flags,
        NullLogger<ScheduledMessageDispatcher>.Instance);

    public ScheduledMessageService Service(AppDbContext db, TimeProvider? clock = null)
    {
        var time = clock ?? new FixedTimeProvider(Now);
        return new ScheduledMessageService(
            db,
            Dispatcher(db, time),
            Replies.Object,
            ActivitiesDouble ?? Activities(db, time),
            Alerts(db),
            Conditions,
            Microsoft.Extensions.Options.Options.Create(Options),
            Flags,
            time,
            NullLogger<ScheduledMessageService>.Instance);
    }

    private MessageReplyResult Send(HostScope scope, Guid bookingId, string? text, string? key, string? userId, MessageOrigin origin)
    {
        Sent.Add(new SentMessage(scope, bookingId, text, key, userId, origin));
        if (SendThrows is { } exception)
            throw exception;

        var thread = (Channel?.ThreadKey) ?? MessageThreadKey.ForDirect(bookingId);
        var channel = Channel?.Channel ?? MessageContactChannel.Direct;
        if (Answer?.Invoke(text, key) is { } answer)
            return answer;

        if (key is not null && _refusedBefore.Contains(key))
            return new MessageReplyResult(channel, thread, null, OtaMessageDeliveryState.Error, true, null);

        if (key is not null && _refused.Remove(key))
        {
            _refusedBefore.Add(key);
            return new MessageReplyResult(channel, thread, null, OtaMessageDeliveryState.Error, false, "octorate_request_rejected");
        }

        var replayed = key is not null && !_delivered.Add(key);
        return new MessageReplyResult(channel, thread, null, State, replayed, null);
    }

    internal sealed record SentMessage(HostScope Scope, Guid BookingId, string? Text, string? Key, string? UserId, MessageOrigin Origin);

    public async Task<Property> SeedPropertyAsync(Guid orgId, string name = "Casa Rosa (TEST)", TimeOnly? checkInFrom = null, TimeOnly? checkOutBy = null, string? siteProfileJson = null)
    {
        await using var db = NewDb();
        if (!await db.Orgs.AnyAsync(o => o.Id == orgId))
            db.Orgs.Add(new OrgEntity { Id = orgId, Slug = orgId.ToString("N")[..12], HostName = "Marco (TEST)" });

        var property = new Property
        {
            OrgId = orgId,
            OwnerId = Owner,
            ResponsibleUserId = Manager,
            Name = name,
            Address = "Via Roma 1",
            City = "Roma",
            PostalCode = "00100",
            CheckInFrom = checkInFrom ?? new TimeOnly(15, 0),
            CheckOutBy = checkOutBy ?? new TimeOnly(10, 0),
            SiteProfileJson = siteProfileJson,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property;
    }

    public async Task<Booking> SeedBookingAsync(
        Property property,
        BookingStatus status = BookingStatus.Confirmed,
        BookingSource source = BookingSource.Direct,
        DateTime? checkIn = null,
        string guestCountry = "IT",
        string guestFirstName = "Giulia")
    {
        await using var db = NewDb();
        var guest = new Guest { OrgId = property.OrgId, FirstName = guestFirstName, LastName = "Bianchi", Email = $"guest.{Guid.NewGuid():N}@example.com", Country = guestCountry };
        db.Guests.Add(guest);
        var arrival = checkIn ?? new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);
        var booking = new Booking
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            Source = source,
            Status = status,
            CheckInDate = arrival,
            CheckOutDate = arrival.AddDays(3),
            NumberOfGuests = 2,
            CreatedAt = new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc),
            ConfirmedAt = new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc),
        };
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking;
    }

    public async Task SetMarketingConsentAsync(Booking booking, bool granted)
    {
        await using var db = NewDb();
        var guest = await db.Guests.SingleAsync(g => g.Id == booking.GuestId);
        guest.MarketingConsent = granted;
        await db.SaveChangesAsync();
    }

    public async Task<MessageTemplate> SeedTemplateAsync(Guid orgId, string body, string name = "Arrivo (TEST)", string language = "it", bool active = true)
    {
        await using var db = NewDb();
        var template = new MessageTemplate { OrgId = orgId, Name = name, Language = language, Body = body, IsActive = active };
        db.MessageTemplates.Add(template);
        await db.SaveChangesAsync();
        return template;
    }

    public async Task<Routine> SeedRoutineAsync(
        Guid orgId,
        MessageTemplate template,
        bool active = true,
        bool testMode = false,
        RoutineEventType eventType = RoutineEventType.BeforeCheckin,
        string eventConfig = """{"days":1}""",
        string conditionJson = "{}",
        string? actionsJson = null)
    {
        await using var db = NewDb();
        var routine = new Routine
        {
            OrgId = orgId,
            Name = "Messaggio di arrivo (TEST)",
            EventType = eventType,
            EventConfig = eventConfig,
            ConditionJson = conditionJson,
            ActionsJson = actionsJson ?? $$$"""[{"type":"MessageToGuest","config":{"templateId":"{{{template.Id}}}"}}]""",
            Active = active,
            TestMode = testMode,
        };
        db.Routines.Add(routine);
        await db.SaveChangesAsync();
        return routine;
    }

    public async Task<ScheduledMessage> SeedRowAsync(
        Booking booking,
        Routine routine,
        MessageTemplate template,
        ScheduledMessageStatus status = ScheduledMessageStatus.Scheduled,
        DateTime? plannedAt = null,
        string? textOverride = null,
        int attempts = 0,
        string? blockedReason = null,
        DateTime? updatedAt = null)
    {
        await using var db = NewDb();
        var row = new ScheduledMessage
        {
            OrgId = booking.OrgId,
            PropertyId = booking.PropertyId,
            BookingId = booking.Id,
            RoutineId = routine.Id,
            RoutineName = routine.Name,
            ActionIndex = 0,
            TemplateRef = template.Id.ToString(),
            PlannedAtUtc = plannedAt ?? Now.UtcDateTime.AddMinutes(-30),
            Status = status,
            TextOverride = textOverride,
            Attempts = attempts,
            BlockedReason = blockedReason,
            CreatedAt = Now.UtcDateTime.AddDays(-1),
            UpdatedAt = updatedAt ?? Now.UtcDateTime.AddDays(-1),
        };
        db.ScheduledMessages.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    public async Task<ScheduledMessage> RowAsync(Guid id)
    {
        await using var db = NewDb();
        return await db.ScheduledMessages.AsNoTracking().SingleAsync(m => m.Id == id);
    }

    public async Task<List<ScheduledMessage>> RowsAsync(Guid bookingId)
    {
        await using var db = NewDb();
        return await db.ScheduledMessages.AsNoTracking().Where(m => m.BookingId == bookingId).OrderBy(m => m.ActionIndex).ToListAsync();
    }

    public async Task<List<InternalActivity>> OpenTodosAsync(Guid bookingId)
    {
        await using var db = NewDb();
        return await db.InternalActivities.AsNoTracking().Where(a => a.EntityId == bookingId && a.Action != null && a.CompletedAt == null).ToListAsync();
    }

    public async Task<List<InAppNotification>> BellAsync()
    {
        await using var db = NewDb();
        return await db.InAppNotifications.AsNoTracking().ToListAsync();
    }
}
