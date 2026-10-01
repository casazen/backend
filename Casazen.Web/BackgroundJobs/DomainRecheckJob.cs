using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Periodic check of the hosts' custom domains (BK-17, A3-25): domains waiting for their DNS records activate by themselves,
/// a verified domain whose records disappear is noticed, and a domain that was dropped leaves the Vercel project
/// (<see cref="IDomainRecheckService"/>). Every 15 minutes; what is due for each domain is decided by its own state.
/// </summary>
public class DomainRecheckJob(IDomainRecheckService domainRecheckService)
{
    public const string RecurringJobId = "domain-recheck";

    /// <summary>Cron of the schedule: the job only picks the domains that are due, so a short interval just means a short wait.</summary>
    public const string Cron = "*/15 * * * *";

    [DisableConcurrentExecution(JobLockTimeouts.FrequentSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken) =>
        await domainRecheckService.RunAsync(cancellationToken);
}
