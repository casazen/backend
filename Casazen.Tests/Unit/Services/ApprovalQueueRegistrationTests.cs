using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Services.ApprovalActions;
using Casazen.Web.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E05/F04/US01/T02: what <c>AddApprovalQueueActions</c> registers (the exact list is photographed here: a registration added to it is added to this test), the registry of the
/// actions by item type, and the validation of a submission.
/// </summary>
public class ApprovalQueueRegistrationTests
{
    // ─── The registrations ───

    [Fact]
    public void AddApprovalQueueActions_RegistersTheTwoActionsTheRegistryAndTheAlert_AsTheWholeListOfServices()
    {
        var services = new ServiceCollection();

        services.AddApprovalQueueActions();

        var actions = services.Where(d => d.ServiceType == typeof(IApprovalOriginAction)).ToList();
        Assert.Equal(
            [typeof(CampaignApprovalAction), typeof(MessageDraftApprovalAction)],
            actions.Select(d => d.ImplementationType!).OrderBy(t => t.Name, StringComparer.Ordinal));
        Assert.All(actions, d => Assert.Equal(ServiceLifetime.Scoped, d.Lifetime));

        Assert.All(
            new[] { typeof(IApprovalOriginActionRegistry), typeof(ApprovalAlertDelivery), typeof(ApprovalAlertJob) },
            type => Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, d => d.ServiceType == type).Lifetime));

        // Of the types of the application: the registry and the alert, the actions twice (one each), and nothing else. The queue itself (IApprovalQueueService) is
        // registered where it has always been, in the shared registration.
        Assert.Equal(
            [
                nameof(ApprovalAlertDelivery),
                nameof(ApprovalAlertJob),
                nameof(IApprovalOriginAction),
                nameof(IApprovalOriginAction),
                nameof(IApprovalOriginActionRegistry),
            ],
            services.Select(d => d.ServiceType).Where(t => t.Namespace?.StartsWith("Casazen.", StringComparison.Ordinal) == true).Select(t => t.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheRegistry_FindsTheActionOfATypeThroughTheContainer_AndATypeWithNoneIsAPureDecision()
    {
        var services = new ServiceCollection();
        services.AddApprovalQueueActions();
        services.AddLogging();
        services.AddScoped(_ => Mock.Of<IMessageReplyService>());
        services.AddScoped(_ => Mock.Of<ICampaignService>());
        services.AddScoped(_ => Mock.Of<Casazen.Core.Features.IFeatureFlags>());
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var registry = scope.ServiceProvider.GetRequiredService<IApprovalOriginActionRegistry>();

        Assert.IsType<MessageDraftApprovalAction>(registry.Find(ApprovalItemTypes.MessageDraft));
        Assert.IsType<CampaignApprovalAction>(registry.Find(ApprovalItemTypes.Campaign));
        Assert.Equal(ApprovalOriginActions.SendGuestMessage, registry.Find(ApprovalItemTypes.MessageDraft)!.OriginAction);
        Assert.Equal(ApprovalOriginActions.ApproveCampaign, registry.Find(ApprovalItemTypes.Campaign)!.OriginAction);
        Assert.Null(registry.Find(ApprovalItemTypes.Routine));
        Assert.Null(registry.Find(ApprovalItemTypes.AiText));
        Assert.Null(registry.Find("TypeALaterEpicAdds"));
    }

    /// <summary>
    /// The circle the registry is lazy for: the action of the campaigns needs the campaign service, which needs the queue to create its items, which needs the registry. Built
    /// in any order, the container must not see a circle.
    /// </summary>
    [Fact]
    public void TheCampaignService_TheQueueAndTheRegistry_AreNotACircleInTheContainer()
    {
        var services = new ServiceCollection();
        services.AddApprovalQueueActions();
        services.AddLogging();
        services.AddScoped(_ => Mock.Of<IMessageReplyService>());
        services.AddScoped(_ => Mock.Of<Casazen.Core.Features.IFeatureFlags>());
        services.AddScoped<ICampaignService, CampaignThatNeedsTheQueue>();
        services.AddScoped<IApprovalQueueService, QueueThatNeedsTheRegistry>();
        using var provider = services.BuildServiceProvider(validateScopes: true);

        foreach (var first in new[] { typeof(ICampaignService), typeof(IApprovalQueueService), typeof(IApprovalOriginActionRegistry) })
        {
            using var scope = provider.CreateScope();
            _ = scope.ServiceProvider.GetRequiredService(first);

            Assert.IsType<CampaignApprovalAction>(scope.ServiceProvider.GetRequiredService<IApprovalOriginActionRegistry>().Find(ApprovalItemTypes.Campaign));
        }
    }

    [Fact]
    public void TheRegistry_TwoActionsForOneType_AreRefusedAtTheFirstUse()
    {
        var registry = new ApprovalOriginActionRegistry(() => [new FakeAction("Campaign", "A"), new FakeAction("Campaign", "B"), new FakeAction("Other", "C")]);

        var refused = Assert.Throws<InvalidOperationException>(() => registry.Find("Other"));

        Assert.Contains("'Campaign' has two actions", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRegistry_AnActionOfALaterFeature_IsFoundNextToTheOnesOfTheQueue()
    {
        var registry = new ApprovalOriginActionRegistry(() => [new FakeAction("ReviewReply", ApprovalOriginActions.SendReviewReply), new FakeAction("MessageDraft", "SendGuestMessage")]);

        Assert.Equal(ApprovalOriginActions.SendReviewReply, registry.Find("ReviewReply")!.OriginAction);
        Assert.Null(registry.Find("Campaign"));
        Assert.Throws<ArgumentException>(() => registry.Find(" "));
    }

    // ─── The doubles ───

    private sealed class FakeAction(string itemType, string originAction) : IApprovalOriginAction
    {
        public string ItemType => itemType;

        public string OriginAction => originAction;

        public Task<ApprovalActionResult> ExecuteAsync(ApprovalQueueItem item, string finalText, string userId, CancellationToken ct) =>
            Task.FromResult(ApprovalActionResult.Executed());

        public Task OnRejectedAsync(ApprovalQueueItem item, string reason, string userId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>The shape of the real campaign service: it needs the queue.</summary>
    private sealed class CampaignThatNeedsTheQueue(IApprovalQueueService queue) : ICampaignService
    {
        public IApprovalQueueService Queue => queue;

        public Task<Campaign> CreateDraftAsync(Guid orgId, Guid segmentId, string subject, string body, bool isAiGenerated, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SubmitForApprovalAsync(Guid campaignId, Guid orgId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ScheduleAsync(Guid campaignId, DateTime scheduledAt, Guid orgId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Campaign> MarkApprovedAsync(Guid campaignId, Guid orgId, string? subject, string? body, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReturnToDraftAsync(Guid campaignId, Guid orgId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<List<string>> GetRecipientsAsync(Guid segmentId, Guid orgId, HostScope scope, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SendCampaignAsync(Guid campaignId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> PreviewCountAsync(Guid segmentId, Guid orgId, HostScope scope, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>The shape of the real queue: it needs the registry.</summary>
    private sealed class QueueThatNeedsTheRegistry(IApprovalOriginActionRegistry registry) : IApprovalQueueService
    {
        public IApprovalOriginActionRegistry Registry => registry;

        public Task<ApprovalQueueItem> SubmitForApprovalAsync(
            Guid orgId,
            string itemType,
            Guid itemId,
            string contentText,
            bool isAiGenerated,
            string? contentVersion = null,
            Guid? propertyId = null,
            string? originAction = null,
            decimal? confidenceScore = null,
            string? situationCode = null,
            string? threadKey = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ApprovalQueueItem> EditAsync(HostScope scope, Guid itemId, string? text, string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ApprovalDecision> ApproveAsync(HostScope scope, Guid itemId, string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ApprovalQueueItem> RejectAsync(HostScope scope, Guid itemId, string userId, string? reason, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<List<ApprovalQueueItem>> GetPendingAsync(HostScope scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> ReturnToPendingIfChangedAsync(string itemType, Guid itemId, string currentContentText, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
