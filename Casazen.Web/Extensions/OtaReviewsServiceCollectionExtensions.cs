using Casazen.Core.Events;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Services.ApprovalActions;

namespace Casazen.Web.Extensions;

/// <summary>
/// The reviews of the channels (epic E05, feature F06, ADR-032, NF-69, OCT-12): the one place where its services are registered. An extension class of its own, so this feature never
/// collides with the shared <c>ServiceCollectionExtensions</c> or with the other features of the epic; <c>Program.cs</c> calls it once and the later tasks of the feature (the
/// drafts of the replies) add their registrations here. Nothing here is conditional: what is not ready sits behind <c>Features:OctorateConnector</c> where it is used (the job is only
/// scheduled while it is on, <c>RecurringJobsRegistration</c>, and the service does nothing without it), and the drafts behind <c>Features:MessageAssistant</c>.
/// </summary>
public static class OtaReviewsServiceCollectionExtensions
{
    public static IServiceCollection AddOtaReviews(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<OctorateReviewsOptions>().BindConfiguration(OctorateReviewsOptions.SectionName);
        services.AddScoped<IOtaReviewService, OtaReviewService>();
        services.AddScoped<OtaReviewSyncJob>();

        services.AddScoped<IOtaReviewReader>(provider => provider.GetRequiredService<IOtaReviewService>());
        services.AddScoped<IReviewReplyDrafter, ReviewReplyDrafter>();
        services.AddScoped<IApprovalOriginAction, ReviewReplyApprovalAction>();
        services.AddScoped<IEventHandler<DomainEvent>, ReviewReceivedHandler>();
        services.AddScoped<ReviewReplyDraftJob>();

        return services;
    }
}
