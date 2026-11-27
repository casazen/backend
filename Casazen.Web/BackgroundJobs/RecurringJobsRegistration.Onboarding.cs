using Casazen.Core.Features;
using Casazen.Infrastructure.Jobs;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

public static partial class RecurringJobsRegistration
{
    /// <summary>
    /// The "To do" sync of the activation with Octorate and the background work of the owner portal operations.
    /// </summary>
    private static void RegisterOnboardingJobs(IRecurringJobManager recurringJobManager, IFeatureFlags featureFlags)
    {
        // E10/F01/US01/T04: the "To do" of the flows to complete and of a lost connection, only while the activation with Octorate is on.
        ConfigureOnboardingJobs(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.OctorateOnboarding));

        // E07/F07/US01 (NF-111): the background work of the owner portal operations, only while the agency owner portal is on.
        ConfigureOwnerPortalOperations(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.AgencyTier));
    }

    /// <summary>
    /// The "To do" sync of the activation of CasaZen with Octorate (E10/F01/US01/T04, NF-106, NF-108): only with
    /// <see cref="FeatureFlags.OctorateOnboarding"/> on, hourly at minute 20 UTC. With the flag off no "To do" of a flow is raised or closed and the
    /// schedule of an earlier deploy is removed (<c>RemoveIfExists</c>).
    /// </summary>
    private static void ConfigureOnboardingJobs(IRecurringJobManager recurringJobManager, bool enabled)
    {
        if (!enabled)
        {
            recurringJobManager.RemoveIfExists(OnboardingFlowTodoSyncJob.RecurringJobId);
            return;
        }

        recurringJobManager.AddOrUpdate<OnboardingFlowTodoSyncJob>(
            OnboardingFlowTodoSyncJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            OnboardingFlowTodoSyncJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }

    /// <summary>
    /// The owner portal operations (E07/F07/US01, NF-111): the automatic approval of the owner stays, the blocks to give back, the sync results and the
    /// «To do» of the agency, every five minutes, only with <see cref="FeatureFlags.AgencyTier"/> on. With the flag off nothing runs and the schedule of an
    /// earlier deploy is removed (<c>RemoveIfExists</c>).
    /// </summary>
    private static void ConfigureOwnerPortalOperations(IRecurringJobManager recurringJobManager, bool enabled)
    {
        if (!enabled)
        {
            recurringJobManager.RemoveIfExists(OwnerPortalOperationsJob.RecurringJobId);
            return;
        }

        recurringJobManager.AddOrUpdate<OwnerPortalOperationsJob>(
            OwnerPortalOperationsJob.RecurringJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            OwnerPortalOperationsJob.Cron,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
