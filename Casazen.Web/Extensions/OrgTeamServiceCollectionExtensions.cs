using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Infrastructure;

namespace Casazen.Web.Extensions;

public static class OrgTeamServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenOrgTeamServices(this IServiceCollection services)
    {
        services.AddScoped<IOnboardingService, OnboardingService>();
        services.AddScoped<IOrgSeatService, OrgSeatService>();
        services.AddScoped<IOrgEmptinessChecker, OrgEmptinessChecker>();
        services.AddScoped<IOrgInvitationService, OrgInvitationService>();
        services.AddScoped<IOwnerInvitationService, OwnerInvitationService>();
        services.AddScoped<IOwnerReportService, OwnerReportService>();
        services.AddScoped<IOwnerPortalService, OwnerPortalService>();
        services.AddScoped<IOrgTeamService, OrgTeamService>();
        services.AddScoped<IOrgPropertyAccessService, OrgPropertyAccessService>();
        services.AddScoped<IOrgInvitationMaintenanceService, OrgInvitationMaintenanceService>();
        services.AddScoped<IActivityLog, ActivityLog>();
        services.AddScoped<IOrgActivityService, OrgActivityService>();
        services.AddScoped<IOrgAccessRequestService, OrgAccessRequestService>();
        services.AddScoped<IOrgActivityRetentionService, OrgActivityRetentionService>();
        services.AddScoped<IOrgRoleMatrixService, OrgRoleMatrixService>();
        services.AddScoped<IOrgOverviewService, OrgOverviewService>();
        services.AddScoped<IAccountEmailResolver, AccountEmailResolver>();
        services.AddScoped<ISignupAttributionService, SignupAttributionService>();
        services.AddScoped<ISeoEventService, SeoEventService>();
        services.AddScoped<ISeoFeaturedPropertiesService, SeoFeaturedPropertiesService>();

        return services;
    }
}
