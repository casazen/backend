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

        // E05/F06/US01/T02 (NF-69, OCT-12, OCT-13): the read of the reviews of the bookings of a channel, the reply of the host and the host's own review, with the safe send; the
        // job reads the eligible bookings every six hours, within the cap of Octorate:Reviews:MaxBookingsPerRun. Scoped: they hold the database of the request or of the run.
        services.AddOptions<OctorateReviewsOptions>().BindConfiguration(OctorateReviewsOptions.SectionName);
        services.AddScoped<IOtaReviewService, OtaReviewService>();
        services.AddScoped<OtaReviewSyncJob>();

        // E05/F06/US01/T03 (NF-69, AI-06): the drafts of the replies. A guest's review (ReviewReceived, direct or of a channel) sends the assistant to work in a job of its own; the draft only goes to the
        // approval queue, and the reply leaves, once, from the action of the type ReviewReply when a person approves it. Behind Features:MessageAssistant (the handler, the job and the
        // endpoint ask the flag; the switches of the organization and of the property are asked by the drafter). The reviews in the hub need no registration of their own: the hub reads them.
        services.AddScoped<IReviewReplyDrafter, ReviewReplyDrafter>();
        services.AddScoped<IApprovalOriginAction, ReviewReplyApprovalAction>();
        services.AddScoped<IEventHandler<DomainEvent>, ReviewReceivedHandler>();
        services.AddScoped<ReviewReplyDraftJob>();

        return services;
    }
}
