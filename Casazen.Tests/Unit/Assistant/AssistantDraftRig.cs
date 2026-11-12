using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Services.ApprovalActions;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Push;
using Casazen.Tests.Unit.Services;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Casazen.Tests.Unit.Assistant;

/// <summary>
/// The pipeline of the drafts (E05/F05/US02/T01) over the EF InMemory provider, on the org of <see cref="HostScopeScenario"/> (the house given to the collaborator «Solo alcuni» and the
/// hidden one): the real services of the assistant, the real approval queue with its alerts and the real «To do» service; the AI provider is scripted and records every request, the
/// replies, the preferences and Hangfire are doubles. The guest is Giulia Verdi, the host Marco Bianchi: names the tests look for in every prompt.
/// </summary>
internal sealed class AssistantDraftRig : IDisposable
{
    public const string GuestFirst = "Giulia";
    public const string GuestLast = "Verdi";
    public const string GuestEmail = "giulia.verdi@example.com";
    public const string HostName = "Marco Bianchi";
    public const string BookingCode = "K7M2Q9X4BN";
    public const string ChannelReference = "HMXYZ987";
    public const string Signature = "Marco, Casa Rossi";

    public static readonly DateTimeOffset Now = HostScopeScenario.Now;

    public AppDbContext Db { get; } = AssistantTestKit.NewDb();

    public FixedTimeProvider Clock { get; } = new(Now);

    public ScriptedAiProvider Ai { get; } = new();

    public Mock<IAiRuntimeSettings> AiSettings { get; } = new();

    public Mock<IChannelMappingService> Mapping { get; } = new();

    public TestFeatureFlags Flags { get; } = new(FeatureFlags.OctorateConnector, FeatureFlags.MessageAssistant);

    public Mock<IMessageReplyService> Replies { get; } = new();

    public Mock<INotificationPreferenceService> Preferences { get; } = new();

    /// <summary>Every event the preferences were asked who to tell about.</summary>
    public List<NotificationEventContext> Asked { get; } = [];

    public Mock<IBackgroundJobClient> Jobs { get; } = new();

    public RecordingPushQueue Push { get; } = new();

    public RecordingEmailQueue Email { get; } = new();

    public HostScopeWorld World { get; private set; } = null!;

    public Guid OrgId => World.OrgId;

    public Guest Guest { get; private set; } = null!;

    public Booking ChannelBooking { get; private set; } = null!;

    /// <summary>The conversation of a channel on the house the collaborator was given, linked to the booking of Giulia Verdi.</summary>
    public OtaMessageThread Thread { get; private set; } = null!;

    /// <summary>A conversation of a channel on the house the collaborator was not given.</summary>
    public OtaMessageThread HiddenThread { get; private set; } = null!;

    public MessageThreadKey Key => MessageThreadKey.ForOta(Thread.Id);

    public AssistantDraftRig()
    {
        AiSettings
            .Setup(s => s.GetEffectiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiOptions { Provider = AiOptions.DeepSeekProvider, ApiKey = "test-key", Model = "deepseek-v4-flash" });
        Mapping.Setup(m => m.IsWriteBlockedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        Preferences
            .Setup(p => p.ResolveAsync(It.IsAny<NotificationEventContext>(), It.IsAny<CancellationToken>()))
            .Callback<NotificationEventContext, CancellationToken>((context, _) => Asked.Add(context))
            .ReturnsAsync([]);
        Jobs.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("job-1");
    }

    public void Dispose() => Db.Dispose();

    // ─── The services ───

    public InternalActivityService Activities() => new(Db, NullLogger<InternalActivityService>.Instance, Clock);

    public ApprovalQueueService Queue(IEnumerable<IApprovalDecisionObserver>? observers = null) =>
        new(
            Db,
            Clock,
            new ApprovalOriginActionRegistry(() => [new MessageDraftApprovalAction(Replies.Object, NullLogger<MessageDraftApprovalAction>.Instance)]),
            Activities(),
            new ApprovalAlertDelivery(
                Db,
                Preferences.Object,
                Activities(),
                Push,
                Email,
                Mock.Of<IInAppNotificationPublisher>(),
                Jobs.Object,
                EmailTestHelpers.Links(),
                NullLogger<ApprovalAlertDelivery>.Instance),
            NullLogger<ApprovalQueueService>.Instance,
            observers);

    public HostMessageAlertDelivery HostAlerts() =>
        new(
            Db,
            Preferences.Object,
            Push,
            Email,
            Mock.Of<IInAppNotificationPublisher>(),
            Jobs.Object,
            EmailTestHelpers.Links(),
            NullLogger<HostMessageAlertDelivery>.Instance);

    public AssistantDraftService Service(IAutoSendGate? gate = null, AssistantOptions? options = null) =>
        new(
            Db,
            AssistantTestKit.Rules(Db, Clock),
            AssistantTestKit.Knowledge(Db, Clock),
            AssistantTestKit.Situations(Db, Clock),
            Ai,
            AiSettings.Object,
            Mapping.Object,
            Flags,
            Queue(),
            Replies.Object,
            Activities(),
            gate ?? new NeverAutoSendGate(),
            HostAlerts(),
            Microsoft.Extensions.Options.Options.Create(options ?? new AssistantOptions()),
            NullLogger<AssistantDraftService>.Instance,
            Clock);

    // ─── The data ───

    /// <summary>
    /// The org with the host's name, Giulia Verdi's booking from Airbnb on the given house, its conversation, a conversation on the hidden house, a fact of the parking that names the
    /// host and his phone, and the rules of the org (the assistant on unless <paramref name="enabled"/> is false; Italian and English; a signature).
    /// </summary>
    public async Task SeedAsync(bool enabled = true, IReadOnlyList<string>? forbidden = null)
    {
        World = await HostScopeScenario.SeedAsync(Db);
        var org = await Db.Orgs.FirstAsync(o => o.Id == World.OrgId);
        org.HostName = HostName;

        Guest = new Guest { OrgId = OrgId, FirstName = GuestFirst, LastName = GuestLast, Email = GuestEmail, Country = "IT" };
        Db.Guests.Add(Guest);
        ChannelBooking = new Booking
        {
            OrgId = OrgId,
            PropertyId = World.Granted.Id,
            GuestId = Guest.Id,
            BookingCode = BookingCode,
            ExternalId = ChannelReference,
            OctorateReservationId = "RES-1",
            CheckInDate = HostScopeScenario.Day(2026, 10, 12),
            CheckOutDate = HostScopeScenario.Day(2026, 10, 15),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Airbnb,
            BasePrice = 300m,
            TotalPrice = 300m,
        };
        Db.Bookings.Add(ChannelBooking);
        Thread = NewThread(World.Granted.Id, ChannelBooking.Id, "Giulia V.");
        HiddenThread = NewThread(World.Hidden.Id, null, "Anna S.");
        Db.OtaMessageThreads.AddRange(Thread, HiddenThread);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();

        await AssistantTestKit.Rules(Db, Clock).SaveAsync(
            OrgId,
            AssistantTestKit.RulesInput(enabled: enabled, languages: ["it", "en"], signature: Signature, threshold: 0.9m, forbidden: forbidden),
            World.OwnerUserId);
        await AssistantTestKit.Knowledge(Db, Clock).CreateEntryAsync(
            OrgId,
            World.Granted.Id,
            AssistantTestKit.Fact(KnowledgeTopic.Parking, "Parcheggio gratuito nel cortile; per problemi chiamate Marco al 347 9876543"),
            World.OwnerUserId);
        Db.ChangeTracker.Clear();
    }

    public OtaMessageThread NewThread(Guid? propertyId, Guid? bookingId, string guestName) => new()
    {
        OrgId = OrgId,
        PropertyId = propertyId,
        BookingId = bookingId,
        OctorateThreadId = $"T-{Guid.NewGuid():N}"[..20],
        OctorateAccommodationId = "ACC-1",
        ChannelSource = OtaChannel.Airbnb,
        Kind = OtaThreadKind.Reservation,
        GuestDisplayName = guestName,
        LastMessageAt = Now.UtcDateTime,
        CreatedAt = Now.UtcDateTime.AddDays(-1),
        UpdatedAt = Now.UtcDateTime.AddDays(-1),
    };

    /// <summary>A message of the guest in <paramref name="thread"/> (the conversation of Giulia Verdi when not given), <paramref name="minutesAgo"/> minutes before now.</summary>
    public Task<OtaMessage> GuestWritesAsync(string text, int minutesAgo = 5, OtaMessageThread? thread = null) =>
        AddMessageAsync(thread ?? Thread, OtaMessageDirection.Inbound, MessageOrigin.Guest, text, minutesAgo, OtaMessageDeliveryState.Sent);

    /// <summary>A message of the host side in the conversation of Giulia Verdi.</summary>
    public Task<OtaMessage> HostWritesAsync(string text, int minutesAgo, OtaMessageDeliveryState state = OtaMessageDeliveryState.Sent) =>
        AddMessageAsync(Thread, OtaMessageDirection.Outbound, MessageOrigin.Person, text, minutesAgo, state);

    private async Task<OtaMessage> AddMessageAsync(
        OtaMessageThread thread,
        OtaMessageDirection direction,
        MessageOrigin origin,
        string text,
        int minutesAgo,
        OtaMessageDeliveryState state)
    {
        var at = Now.UtcDateTime.AddMinutes(-minutesAgo);
        var message = new OtaMessage
        {
            OrgId = thread.OrgId,
            ThreadId = thread.Id,
            OctorateMessageId = $"M-{Guid.NewGuid():N}"[..20],
            Direction = direction,
            SenderName = direction == OtaMessageDirection.Inbound ? thread.GuestDisplayName ?? string.Empty : "Host",
            Text = text,
            SentAt = at,
            DeliveryState = state,
            Origin = origin,
            CreatedAt = at,
        };
        Db.OtaMessages.Add(message);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return message;
    }

    /// <summary>Everything the provider was given: the instructions and the request of every call.</summary>
    public string EverythingSentToTheProvider() =>
        string.Join("\n", Ai.Calls.Select(call => $"{call.Request.SystemPrompt}\n{call.Request.Prompt}"));
}

/// <summary>
/// An AI provider that answers what the test set: the classification to a call of the Economy tier, the draft to a call of the Standard tier, and records every request. It can say
/// it is not configured, or throw.
/// </summary>
internal sealed class ScriptedAiProvider : IAiProvider
{
    public const string ParkingClassification =
        """{"situation":"parking","situationMatch":0.92,"urgency":"normal","sensitiveCategories":[],"intentFlags":[],"language":"it"}""";

    public const string ParkingDraft =
        """{"draft":"Ciao {{guest_name}}, il parcheggio è gratuito nel cortile.","language":"it","missingFacts":false,"topics":["Parking"]}""";

    public List<(AiRequest Request, AiModelTier Tier)> Calls { get; } = [];

    public string Classification { get; set; } = ParkingClassification;

    public string Draft { get; set; } = ParkingDraft;

    public Exception? Throw { get; set; }

    public bool Configured { get; set; } = true;

    public Task<AiGenerationResult> GenerateAsync(string prompt, AiModelTier tier, string cacheKey, CancellationToken cancellationToken = default) =>
        GenerateAsync(new AiRequest(prompt, []), tier, cacheKey, cancellationToken);

    public Task<AiGenerationResult> GenerateAsync(AiRequest request, AiModelTier tier, string cacheKey, CancellationToken cancellationToken = default)
    {
        Calls.Add((request, tier));
        if (Throw is { } failure)
            throw failure;

        var content = tier == AiModelTier.Economy ? Classification : Draft;
        return Task.FromResult(new AiGenerationResult(content, 10, 10, tier, FromCache: false, ProviderConfigured: Configured));
    }
}
