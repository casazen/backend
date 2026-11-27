using Casazen.Core.Services;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Services.ApprovalActions;

namespace Casazen.Web.Extensions;

/// <summary>
/// The approval queue that executes (epic E05, feature F04, NF-68, ADR-032, ADR-026): the registry of the actions an approval runs and the two actions of the queue itself
/// (the draft of a message, the campaign), and the alert of a new item (the "To do" and the notice to the people who may decide, through the preferences of F03). An extension
/// class of its own, so this feature never collides with the shared <c>ServiceCollectionExtensions</c> (where <c>IApprovalQueueService</c> has been registered since NF-06);
/// <c>Program.cs</c> calls it once. The assistant (F05), the replies to reviews (F06) and epic E07 register the action of <b>their own</b> item types from <b>their own</b>
/// extension classes with <c>services.AddScoped&lt;IApprovalOriginAction, TheirAction&gt;()</c>: the registry reads every one of them, and a type has one (two make the
/// registry refuse to answer). A type with no action is a pure decision.
/// </summary>
public static class ApprovalQueueServiceCollectionExtensions
{
    public static IServiceCollection AddApprovalQueueActions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The actions of the queue itself: approving the draft of a message sends it to the conversation (IMessageReplyService, one send for each item); approving a campaign
        // approves it and schedules it when it has a time. Scoped: they hold the database and the services of the request.
        services.AddScoped<IApprovalOriginAction, MessageDraftApprovalAction>();
        services.AddScoped<IApprovalOriginAction, CampaignApprovalAction>();

        // The registry finds an action by item type. It asks the container for the actions when the first approval needs one, not when it is built: the campaign action needs the
        // campaign service, which needs the queue, which needs the registry.
        services.AddScoped<IApprovalOriginActionRegistry>(provider =>
            new ApprovalOriginActionRegistry(() => provider.GetServices<IApprovalOriginAction>()));

        // The alert of a new item: the "To do" and the bell, the push and the e-mail of each person the preferences select. What the quiet hours hold back, and an alert that failed
        // for somebody, is delivered by the job. HostNotifyingPathsArchitectureTests: the delivery asks INotificationPreferenceService.
        services.AddScoped<ApprovalAlertDelivery>();
        services.AddScoped<ApprovalAlertJob>();

        return services;
    }
}
