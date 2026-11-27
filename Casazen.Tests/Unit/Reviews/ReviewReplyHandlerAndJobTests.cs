using System.Reflection;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Services.ApprovalActions;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Extensions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Reviews;

/// <summary>
/// A guest's review sends the assistant to work in a job of its own (E05/F06/US01/T03, NF-69, AI-06): only behind the flag and the switch of the organization, for the review of a channel and for the
/// one of the booking site, once; the job runs only behind the flag, under the org of the event, and <b>only writes the draft</b>: nothing of the path that drafts holds a way to send a reply.
/// </summary>
public sealed class ReviewReplyHandlerAndJobTests
{
    private readonly Mock<IBackgroundJobClient> _jobs = new();
    private readonly List<Job> _created = [];
    private readonly TestFeatureFlags _flags = new(FeatureFlags.MessageAssistant);

    public ReviewReplyHandlerAndJobTests() =>
        _jobs.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>())).Callback<Job, IState>((job, _) => _created.Add(job)).Returns("job-1");

    private static DomainEvent Received(Guid orgId, Guid reviewId, DomainEventType type = DomainEventType.ReviewReceived) =>
        new(
            Guid.NewGuid(),
            orgId,
            type,
            reviewId,
            DateTime.UtcNow,
            JsonSerializer.Serialize(new ReviewReceivedPayload(reviewId, Guid.NewGuid(), Guid.NewGuid())));

    private static async Task SwitchOnAsync(OtaReviewHarness h, Guid orgId)
    {
        await using var db = h.NewDb();
        db.OrgAssistantRules.Add(new OrgAssistantRules { OrgId = orgId, Enabled = true });
        await db.SaveChangesAsync();
    }

    private async Task HandleAsync(OtaReviewHarness h, DomainEvent domainEvent)
    {
        await using var db = h.NewDb();
        await new ReviewReceivedHandler(db, _flags, _jobs.Object, NullLogger<ReviewReceivedHandler>.Instance).HandleAsync(domainEvent, CancellationToken.None);
    }

    [Fact]
    public async Task AReviewOfAChannelOrOfTheBookingSite_QueuesTheJobOfTheAssistant_WithTheOrgAndTheKeyOfTheReview()
    {
        var h = await OtaReviewHarness.CreateAsync();
        await SwitchOnAsync(h, h.Granted.OrgId);
        h.ChannelHasReview(h.Granted);
        var channel = await h.SyncAsync(h.Granted);
        var stay = await h.AddStayAsync(h.Granted.OrgId, h.Granted.PropertyId, "585009", "HMDIRECT01", BookingSource.Direct);
        var site = await h.AddSiteReviewAsync(stay);

        await HandleAsync(h, Received(h.Granted.OrgId, channel.Id));
        await HandleAsync(h, Received(h.Granted.OrgId, site.Id));

        Assert.Equal(2, _created.Count);
        Assert.All(_created, job => Assert.Equal((typeof(ReviewReplyDraftJob), nameof(ReviewReplyDraftJob.RunAsync)), (job.Type, job.Method.Name)));
        Assert.Equal([h.Granted.OrgId, MessageThreadKey.ForOtaReview(channel.Id).ToString()], _created[0].Args.Take(2));
        Assert.Equal([h.Granted.OrgId, MessageThreadKey.ForReview(site.Id).ToString()], _created[1].Args.Take(2));
    }

    [Fact]
    public async Task TheFlagOff_TheSwitchOfTheOrgOff_AnotherEvent_OrAReviewThatIsNotOfTheOrg_QueueNothing()
    {
        var h = await OtaReviewHarness.CreateAsync();
        h.ChannelHasReview(h.Granted);
        var review = await h.SyncAsync(h.Granted);
        var foreign = await PublishOtherOrgAsync(h);

        // The organization never switched its assistant on: its reviews are not looked at.
        await HandleAsync(h, Received(h.Granted.OrgId, review.Id));

        await SwitchOnAsync(h, h.Granted.OrgId);
        _flags.Set(FeatureFlags.MessageAssistant, false);
        await HandleAsync(h, Received(h.Granted.OrgId, review.Id));
        _flags.Set(FeatureFlags.MessageAssistant, true);

        // Another event, no id, an id that is no review, and the review of another org named by the event of this one.
        await HandleAsync(h, Received(h.Granted.OrgId, review.Id, DomainEventType.BookingConfirmed));
        await HandleAsync(h, Received(h.Granted.OrgId, Guid.Empty));
        await HandleAsync(h, Received(h.Granted.OrgId, Guid.NewGuid()));
        await HandleAsync(h, Received(h.Granted.OrgId, foreign.Id));

        Assert.Empty(_created);
    }

    private static async Task<OtaReview> PublishOtherOrgAsync(OtaReviewHarness h)
    {
        h.ChannelHasReview(h.Other);
        return await h.SyncAsync(h.Other);
    }

    [Fact]
    public async Task TheHandler_DoesNotSwallowAFailureOfTheQueue_SoTheEventIsDeliveredAgain()
    {
        var h = await OtaReviewHarness.CreateAsync();
        await SwitchOnAsync(h, h.Granted.OrgId);
        h.ChannelHasReview(h.Granted);
        var review = await h.SyncAsync(h.Granted);
        _jobs.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>())).Throws(new InvalidOperationException("the queue is down"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => HandleAsync(h, Received(h.Granted.OrgId, review.Id)));
    }

    [Fact]
    public async Task TheJob_RunsOnlyBehindTheFlag_UnderTheOrgOfTheEvent_AndWritesTheDraftOnlyOnce()
    {
        var drafter = new Mock<IReviewReplyDrafter>();
        var tenant = new Mock<IBackgroundTenantScope>();
        var orgId = Guid.NewGuid();
        var key = MessageThreadKey.ForOtaReview(Guid.NewGuid());
        drafter
            .Setup(d => d.DraftAsync(orgId, key, It.Is<ReviewDraftOptions?>(o => o != null && !o.Requested), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReviewDraftOutcome.NotDrafted(ReviewDraftReasons.AssistantDisabled));
        var job = new ReviewReplyDraftJob(drafter.Object, _flags, tenant.Object, NullLogger<ReviewReplyDraftJob>.Instance);

        await job.RunAsync(orgId, key.ToString(), CancellationToken.None);
        _flags.Set(FeatureFlags.MessageAssistant, false);
        await job.RunAsync(orgId, key.ToString(), CancellationToken.None);
        _flags.Set(FeatureFlags.MessageAssistant, true);

        // The key of a conversation is not the one of a review, and a key that is not one is nothing.
        await job.RunAsync(orgId, MessageThreadKey.ForOta(Guid.NewGuid()).ToString(), CancellationToken.None);
        await job.RunAsync(orgId, "not a key", CancellationToken.None);

        tenant.Verify(t => t.EnterOrg(orgId), Times.Once);
        drafter.Verify(d => d.DraftAsync(It.IsAny<Guid>(), It.IsAny<MessageThreadKey>(), It.IsAny<ReviewDraftOptions?>(), It.IsAny<CancellationToken>()), Times.Once);

        var run = typeof(ReviewReplyDraftJob).GetMethod(nameof(ReviewReplyDraftJob.RunAsync))!;
        Assert.Contains("{1}", run.GetCustomAttribute<DisableConcurrentExecutionAttribute>()!.Resource, StringComparison.Ordinal);
        Assert.Equal(ReviewReplyDraftJob.Attempts, run.GetCustomAttribute<AutomaticRetryAttribute>()!.Attempts);
    }

    /// <summary>
    /// PROOF TEST: data rule (AC-NF69-4). The path that drafts — the handler, the job and the drafter — has no way to send a reply, by structure: none of them names a client of the channel manager,
    /// the service of the replies of a channel (<see cref="IOtaReviewService"/>, which sends), the service of the replies of the booking site or a call that sends; no parameter of their
    /// constructors is, or implements, one of them; the drafter reads a review through <see cref="IOtaReviewReader"/>, which has no member that writes. So a reply can only leave through the
    /// approve action of the queue, after a person approved it.
    /// </summary>
    [Fact]
    public void TheDraftingPath_HoldsNoWayToSendAReply_OnlyTheApproveActionDoes()
    {
        var sources = new Dictionary<string, string>
        {
            ["Casazen.Infrastructure/Events/ReviewReceivedHandler.cs"] = string.Empty,
            ["Casazen.Infrastructure/Jobs/ReviewReplyDraftJob.cs"] = string.Empty,
            ["Casazen.Infrastructure/Services/ReviewReplyDrafter.cs"] = string.Empty,
        };
        foreach (var path in sources.Keys.ToList())
        {
            var code = File.ReadAllLines(Path.Combine(SolutionRoot(), path))
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .ToList();
            sources[path] = string.Join('\n', code);
        }

        foreach (var (path, code) in sources)
        {
            foreach (var forbidden in new[] { ".ReplyAsync(", "SendHostReplyAsync", "SendHostReviewAsync", "IOtaReviewService", "IHostReviewService", "IOctorateApiClient" })
                Assert.False(code.Contains(forbidden, StringComparison.Ordinal), $"{path} must not hold a way to send a reply: it mentions {forbidden}.");
        }

        // The approve action is the one that does.
        var action = File.ReadAllText(Path.Combine(SolutionRoot(), "Casazen.Infrastructure/Services/ApprovalActions/ReviewReplyApprovalAction.cs"));
        Assert.Contains(".ReplyAsync(", action, StringComparison.Ordinal);

        // The constructors say the same, by type: none takes a client, the service of the replies of a channel or the one of the booking site, nor anything that implements them.
        Type[] senders = [typeof(IOctorateApiClient), typeof(IOtaReviewService), typeof(IHostReviewService)];
        foreach (var type in new[] { typeof(ReviewReplyDraftJob), typeof(ReviewReceivedHandler), typeof(ReviewReplyDrafter) })
        {
            var parameters = type.GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();
            Assert.DoesNotContain(parameters, parameter => senders.Any(sender => sender.IsAssignableFrom(parameter)));
        }

        // The drafter reads a review through the interface that only reads, and that interface can never gain a member that writes.
        var drafterParameters = typeof(ReviewReplyDrafter).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();
        Assert.Contains(typeof(IOtaReviewReader), drafterParameters);
        Assert.Equal([nameof(IOtaReviewReader.GetAsync)], typeof(IOtaReviewReader).GetMethods().Select(m => m.Name));
        Assert.Empty(typeof(IOtaReviewReader).GetProperties());
    }

    [Fact]
    public void AddOtaReviews_RegistersTheDrafterTheActionTheHandlerAndTheJob_AsTheWholeListOfServices()
    {
        var services = new ServiceCollection();

        services.AddOtaReviews();

        Assert.Equal(
            ServiceLifetime.Scoped,
            Assert.Single(services, d => d.ServiceType == typeof(IReviewReplyDrafter) && d.ImplementationType == typeof(ReviewReplyDrafter)).Lifetime);
        Assert.Equal(
            ServiceLifetime.Scoped,
            Assert.Single(services, d => d.ServiceType == typeof(IApprovalOriginAction) && d.ImplementationType == typeof(ReviewReplyApprovalAction)).Lifetime);
        Assert.Equal(
            ServiceLifetime.Scoped,
            Assert.Single(services, d => d.ServiceType == typeof(IEventHandler<DomainEvent>) && d.ImplementationType == typeof(ReviewReceivedHandler)).Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, d => d.ServiceType == typeof(ReviewReplyDraftJob)).Lifetime);

        // Of the types of the application, and nothing else: a registration added to the feature is added to this list.
        Assert.Equal(
            [
                nameof(IApprovalOriginAction),
                "IEventHandler`1",
                nameof(IOtaReviewReader),
                nameof(IOtaReviewService),
                nameof(IReviewReplyDrafter),
                nameof(OtaReviewSyncJob),
                nameof(ReviewReplyDraftJob),
            ],
            services.Select(d => d.ServiceType).Where(t => t.Namespace?.StartsWith("Casazen.", StringComparison.Ordinal) == true).Select(t => t.Name).Order(StringComparer.Ordinal));
    }

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
