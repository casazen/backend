using Casazen.Core.Features;
using Casazen.Infrastructure.Jobs;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

public static partial class RecurringJobsRegistration
{
    private static void RegisterOnboardingJobs(IRecurringJobManager recurringJobManager, IFeatureFlags featureFlags)
    {
        ConfigureOnboardingJobs(recurringJobManager, featureFlags.IsEnabled(FeatureFlags.OctorateOnboarding));

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
