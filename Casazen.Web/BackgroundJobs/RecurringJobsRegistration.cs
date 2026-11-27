using Casazen.Core.Features;
using Casazen.Core.Options;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Recurring jobs, registered (<c>AddOrUpdate</c>) on every startup in the environment's own Hangfire schema.
/// Every job listed here must carry <see cref="DisableConcurrentExecutionAttribute"/> (FD-11): a run that is
/// still in progress, a retry or a manual trigger from the dashboard must never overlap with the next run.
/// A job behind a feature flag that is off is removed (<c>RemoveIfExists</c>), so an earlier deploy's schedule stops.
/// </summary>
public static partial class RecurringJobsRegistration
{
    public const string OtaSyncAllJobId = "ota-sync-all";
    public const string BookingPullAllJobId = "booking-pull-all";

    /// <param name="recurringJobManager">The Hangfire schedule of the environment.</param>
    /// <param name="featureFlags">The flags that decide the jobs behind a feature.</param>
    /// <param name="routineOptions">The interval of the time triggers of the routines (section <c>Routine</c>); the defaults when omitted.</param>
    public static void Configure(IRecurringJobManager recurringJobManager, IFeatureFlags featureFlags, RoutineOptions? routineOptions = null)
    {
        RegisterPlatformJobs(recurringJobManager, featureFlags);
        RegisterChannelJobs(recurringJobManager, featureFlags);
        RegisterFiscalJobs(recurringJobManager, featureFlags);
        RegisterOnboardingJobs(recurringJobManager, featureFlags);
        RegisterEventJobs(recurringJobManager, routineOptions);
        RegisterSupplierJobs(recurringJobManager, featureFlags);
        RegisterComplianceJobs(recurringJobManager);
        RegisterOrgTeamJobs(recurringJobManager);
        RegisterLongRentJobs(recurringJobManager, featureFlags);
        RegisterBookingJobs(recurringJobManager);
        RegisterGuestCheckInJobs(recurringJobManager, featureFlags);
        RegisterMaintenanceJobs(recurringJobManager);
    }
}
