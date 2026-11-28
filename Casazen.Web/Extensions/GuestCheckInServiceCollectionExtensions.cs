using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.OfficialData;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

/// <summary>
/// The SEO content service, the guest access and check-in, the check-in at the door of a property, the Alloggiati code tables, the official reference
/// data of the comuni and the guests of a stay.
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
public static class GuestCheckInServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenGuestCheckInServices(this IServiceCollection services)
    {
        services.AddScoped<ISeoContentService, SeoContentService>();
        services.AddScoped<IGuestAccessService, GuestAccessService>();
        services.AddScoped<IGuestCheckInService, GuestCheckInService>();
        services.AddScoped<IDoorCheckInService, DoorCheckInService>();

        services.AddSingleton<IDoorCheckInFailureTracker, DoorCheckInFailureTracker>();
        services.AddScoped<IAlloggiatiCodeTableService, AlloggiatiCodeTableService>();
        services.AddHttpClient<OfficialSourceDownloader>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CasaZen-official-reference-data/1.0");
        });
        services.AddScoped<IOfficialReferenceDataRefreshService, OfficialReferenceDataRefreshService>();
        services.AddScoped<IComuneOfficialProfileService, ComuneOfficialProfileService>();
        services.AddOptions<OfficialReferenceDataOptions>()
            .BindConfiguration(OfficialReferenceDataOptions.SectionName);
        services.AddScoped<IStayGuestService, StayGuestService>();

        return services;
    }
}
