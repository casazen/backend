using Casazen.Core.Features;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

public static partial class RecurringJobsRegistration
{
    /// <summary>
    /// The marketing studio dispatch, the monthly agency usage and the expiry of the activation credentials.
    /// </summary>
    private static void RegisterPlatformJobs(IRecurringJobManager recurringJobManager, IFeatureFlags featureFlags)
    {
        if (featureFlags.IsEnabled(FeatureFlags.CrmMarketing))
            recurringJobManager.AddOrUpdate<MarketingStudioJob>("marketing-studio", job => job.DispatchAsync(CancellationToken.None),
                Cron.Hourly(), new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        else
            recurringJobManager.RemoveIfExists("marketing-studio");

        recurringJobManager.AddOrUpdate<AgencyUsageJob>(
            "agency-monthly-unit-usage", job => job.ExecuteAsync(CancellationToken.None),
            "*/5 * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        recurringJobManager.AddOrUpdate<PropertyActivationAccounts>(
            "expire-activation-credentials", accounts => accounts.PurgeExpiredAsync(CancellationToken.None),
            "10 3 * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
