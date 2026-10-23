using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;

namespace Casazen.Web.Extensions;

/// <summary>
/// The end of a long-term lease in force (LR-14): the service that records a notice of non-renewal or an early termination and renews a lease
/// by itself, and the hourly job that does the renewal. The endpoints and the job are always on, there is no flag. Runbook:
/// <c>docs/runbooks/long-rent-aggregates.md</c> (§ 10).
/// </summary>
public static class LongRentLeaseTermExtensions
{
    public static IServiceCollection AddCasazenLongRentLeaseTerm(this IServiceCollection services)
    {
        services.AddScoped<ILeaseTermService, LeaseTermService>();
        services.AddScoped<LeaseTermRenewalJob>();
        return services;
    }
}
