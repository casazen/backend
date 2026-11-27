using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Services.ApprovalActions;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Push;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The approval queue (E05/F04/US01/T02) over the EF InMemory provider, with every neighbour a test can steer or watch: the service of the replies (a fake that keeps the rule of
/// its key: a key that was sent is replayed, never sent again), the campaigns, the queue of the pushes and of the e-mails, the bell, Hangfire and the check of the permission
/// <c>approval.decide</c>. The "To do" service and the preferences are the real ones. The org has an owner (an administrator), a property manager (in charge of the property B), and a
/// collaborator «Solo alcuni» (in charge of A and B, granted A only); the owner and the manager decide, the collaborator does not.
/// </summary>
internal sealed class ApprovalQueueRig : IDisposable
{
    public const string Owner = "auth0|owner";
    public const string Manager = "auth0|manager";
    public const string Collaborator = "auth0|collaborator";

    public static readonly DateTime Noon = new(2026, 11, 10, 11, 0, 0, DateTimeKind.Utc);

    public static readonly DateTime Night = new(2026, 11, 10, 22, 0, 0, DateTimeKind.Utc);

    public static readonly DateTime MorningDue = new(2026, 11, 11, 7, 0, 0, DateTimeKind.Utc);

    public const string DraftText = "Buongiorno, il check-in è dalle 15:00. A presto!";
    public const string Situation = "early_checkin_request";

    public AppDbContext Db { get; } = OrgTeamTestData.NewDb();

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(Noon));

    public RecordingPushQueue Push { get; } = new();

    public RecordingEmailQueue Email { get; } = new();

    public Mock<IInAppNotificationPublisher> Publisher { get; } = new();

    public Mock<IBackgroundJobClient> Jobs { get; } = new();

    public Mock<IContextAuthorizationService> Permissions { get; } = new();

    public Mock<IMessageReplyService> Replies { get; } = new();

    public Mock<ICampaignService> Campaigns { get; } = new();

    public TestFeatureFlags Flags { get; } = new(Casazen.Core.Features.FeatureFlags.CrmMarketing);

    public List<(Job Job, IState State)> Created { get; } = [];

    public List<SentMessage> Sent { get; } = [];

    private readonly HashSet<string> _delivered = [];

    private readonly HashSet<string> _refused = [];

    private readonly HashSet<string> _refusedBefore = [];

    public HashSet<string> Deciders { get; } = [Owner, Manager];

    public Guid OrgId { get; private set; }

    public Property PropertyA { get; private set; } = null!;

    public Property PropertyB { get; private set; } = null!;

    public ApprovalQueueRig()
    {
        Permissions
            .Setup(p => p.HasPermissionOrThrowAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string userId, string context, string permission, CancellationToken _) =>
                context == OrgRoleCatalog.ShortRent && permission == HostPermissions.ApprovalDecide
                    ? Deciders.Contains(userId)
                    : permission != HostPermissions.ApprovalDecide);
        Jobs
            .Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Callback<Job, IState>((job, state) => Created.Add((job, state)))
            .Returns("job-1");
        Replies
            .Setup(r => r.ReplyAsync(
                It.IsAny<HostScope>(),
                It.IsAny<MessageThreadKey>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<MessageOrigin>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((HostScope scope, MessageThreadKey key, string? text, string? idempotencyKey, string? userId, MessageOrigin origin, string? ruleCode, CancellationToken _) =>
                Reply(scope, key, text, idempotencyKey, userId, origin, ruleCode));
    }

    public void Dispose() => Db.Dispose();

    public void RefuseKey(string idempotencyKey) => _refused.Add(idempotencyKey);

    private MessageReplyResult Reply(
        HostScope scope,
        MessageThreadKey key,
        string? text,
        string? idempotencyKey,
        string? userId,
        MessageOrigin origin,
        string? ruleCode)
    {
        Sent.Add(new SentMessage(scope, key, text, idempotencyKey, userId, origin, ruleCode));

        if (idempotencyKey is not null && _refusedBefore.Contains(idempotencyKey))
            return new MessageReplyResult(MessageContactChannel.Ota, key, null, OtaMessageDeliveryState.Error, true, null);

        if (idempotencyKey is not null && _refused.Remove(idempotencyKey))
        {
            _refusedBefore.Add(idempotencyKey);
            return new MessageReplyResult(MessageContactChannel.Ota, key, null, OtaMessageDeliveryState.Error, false, "octorate_request_rejected");
        }

        var replayed = idempotencyKey is not null && !_delivered.Add(idempotencyKey);
        return new MessageReplyResult(MessageContactChannel.Ota, key, null, OtaMessageDeliveryState.Sent, replayed, null);
    }

    public async Task SeedAsync()
    {
        var org = OrgTeamTestData.AddOrg(Db);
        OrgId = org.Id;
        OrgTeamTestData.AddUser(Db, Owner, org.Id, UserRole.PropertyOwner);
        OrgTeamTestData.AddUser(Db, Manager, org.Id, UserRole.None);
        OrgTeamTestData.AddUser(Db, Collaborator, org.Id, UserRole.None);
        OrgTeamTestData.AddMember(Db, Owner, org.Id, OrgRole.Owner);
        OrgTeamTestData.AddMember(Db, Manager, org.Id, OrgRole.PropertyManager);
        OrgTeamTestData.AddMember(Db, Collaborator, org.Id, OrgRole.Collaborator).PropertyScope = PropertyScope.Selected;

        PropertyA = NewProperty(org.Id, "Casa Rosa", Collaborator);
        PropertyB = NewProperty(org.Id, "Casa Blu", Manager);
        Db.Properties.AddRange(PropertyA, PropertyB);
        Db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = org.Id, UserId = Collaborator, PropertyId = PropertyA.Id });
        await Db.SaveChangesAsync();
    }

    private static Property NewProperty(Guid orgId, string name, string responsible) => new()
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

    public HostScope OrgWide => new(OrgId);

    public HostScope OnlyA => new(OrgId, GrantedToUserId: Collaborator);

    public NotificationPreferenceService Preferences() =>
        new(Db, Permissions.Object, Clock, NullLogger<NotificationPreferenceService>.Instance);

    public InternalActivityService Activities() => new(Db, NullLogger<InternalActivityService>.Instance, Clock);

    public ApprovalAlertDelivery Alerts() =>
        new(
            Db,
            Preferences(),
            Activities(),
            Push,
            Email,
            Publisher.Object,
            Jobs.Object,
            EmailTestHelpers.Links(),
            NullLogger<ApprovalAlertDelivery>.Instance);

    public ApprovalOriginActionRegistry Registry() =>
        new(() =>
        [
            new MessageDraftApprovalAction(Replies.Object, NullLogger<MessageDraftApprovalAction>.Instance),
            new CampaignApprovalAction(Campaigns.Object, Flags, NullLogger<CampaignApprovalAction>.Instance),
        ]);

    public ApprovalQueueService Service(IApprovalOriginActionRegistry? registry = null) =>
        new(Db, Clock, registry ?? Registry(), Activities(), Alerts(), NullLogger<ApprovalQueueService>.Instance);

    public static string OtaKey(Guid? id = null) => $"ota:{(id ?? Guid.NewGuid()):D}";

    public Task<ApprovalQueueItem> SubmitDraftAsync(
        ApprovalQueueService service,
        Property? property = null,
        string text = DraftText,
        string? threadKey = null) =>
        service.SubmitForApprovalAsync(
            OrgId,
            ApprovalItemTypes.MessageDraft,
            Guid.NewGuid(),
            text,
            isAiGenerated: true,
            propertyId: property?.Id,
            originAction: ApprovalOriginActions.SendGuestMessage,
            confidenceScore: 0.912m,
            situationCode: Situation,
            threadKey: threadKey ?? OtaKey());

    public List<(T Args, DateTime EnqueueAt)> Scheduled<T>(Type jobType)
        where T : class =>
        Created
            .Where(c => c.Job.Type == jobType && c.Job.Args.OfType<T>().Any())
            .Select(c => (c.Job.Args.OfType<T>().First(), c.State is ScheduledState scheduled ? scheduled.EnqueueAt : default))
            .ToList();

    public async Task ChooseAsync(string userId, NotificationChannels channels) =>
        await Preferences().SaveAsync(
            userId,
            new NotificationPreferencesUpdate(null, [new NotificationEventUpdate(NotificationEventType.AiDraftToApprove, channels)]),
            CancellationToken.None);

    public sealed record SentMessage(
        HostScope Scope,
        MessageThreadKey Key,
        string? Text,
        string? IdempotencyKey,
        string? UserId,
        MessageOrigin Origin,
        string? RuleCode);
}
