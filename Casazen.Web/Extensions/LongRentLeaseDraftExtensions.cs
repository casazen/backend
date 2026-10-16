using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Extensions;

/// <summary>
/// The guided draft of a lease (LR-12): the service, the data of the landlord, the deletion shared with the retention job, the
/// retention job itself and its options (<c>LongRentLease:DraftRetentionDays</c>, validated at startup). The endpoints are behind
/// <c>Features:LongRentArea</c> and the job is registered only with it on; registering the services costs nothing with the flag off.
/// Runbook: <c>docs/runbooks/long-rent-lease-draft.md</c>.
/// </summary>
public static class LongRentLeaseDraftExtensions
{
    public static IServiceCollection AddCasazenLongRentLeaseDrafts(this IServiceCollection services)
    {
        services.AddOptions<LongRentLeaseOptions>()
            .BindConfiguration(LongRentLeaseOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<LongRentLeaseOptions>, LongRentLeaseOptionsValidator>();

        services.AddScoped<LeaseDraftDeleter>();
        services.AddScoped<ILeaseDraftService, LeaseDraftService>();
        services.AddScoped<ILongRentLandlordDefaultsService, LongRentLandlordDefaultsService>();
        services.AddScoped<ILeaseDraftRetentionService, LeaseDraftRetentionService>();
        services.AddScoped<LeaseDraftRetentionJob>();
        return services;
    }
}
