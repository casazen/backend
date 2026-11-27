using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Infrastructure;

namespace Casazen.Web.Extensions;

/// <summary>
/// The onboarding, the people of an org (seats, invitations, members, property access, activity log, access requests, role matrix, overview), the
/// owner reports and portal, the signup attribution and the events of the SEO funnel.
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
public static class OrgTeamServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenOrgTeamServices(this IServiceCollection services)
    {
        services.AddScoped<IOnboardingService, OnboardingService>();
        // AM-02: the people of an org: seats of the plan, invitations and their acceptance, the members and the hourly
        // maintenance (reminders, expiry, deletion of the closed ones).
        services.AddScoped<IOrgSeatService, OrgSeatService>();
        services.AddScoped<IOrgEmptinessChecker, OrgEmptinessChecker>();
        services.AddScoped<IOrgInvitationService, OrgInvitationService>();
        services.AddScoped<IOwnerInvitationService, OwnerInvitationService>();
        // NF-96: the performance of an agency's properties cut by property owner, over the Home's numbers (GET /api/reports/by-owner).
        services.AddScoped<IOwnerReportService, OwnerReportService>();
        // ADR-011, NF-96: the owner portal (the owner's properties, and their overview and trend on the Home's numbers). It was never
        // registered: the controller of the portal could not be built.
        services.AddScoped<IOwnerPortalService, OwnerPortalService>();
        services.AddScoped<IOrgTeamService, OrgTeamService>();
        // AM-03: the properties each member reaches («Solo alcuni») and the member in charge of a property.
        services.AddScoped<IOrgPropertyAccessService, OrgPropertyAccessService>();
        services.AddScoped<IOrgInvitationMaintenanceService, OrgInvitationMaintenanceService>();
        // AM-02b: the activity log of the org (ids and codes only), written inside the unit of work of the services above and of
        // the plan and settings of the org; its list and CSV; the requests for access to the administrators; its retention.
        services.AddScoped<IActivityLog, ActivityLog>();
        services.AddScoped<IOrgActivityService, OrgActivityService>();
        services.AddScoped<IOrgAccessRequestService, OrgAccessRequestService>();
        services.AddScoped<IOrgActivityRetentionService, OrgActivityRetentionService>();
        // AM-02c: the matrix of the roles for the team screens, and the overview of the org for the account home.
        services.AddScoped<IOrgRoleMatrixService, OrgRoleMatrixService>();
        services.AddScoped<IOrgOverviewService, OrgOverviewService>();
        services.AddScoped<IAccountEmailResolver, AccountEmailResolver>();
        services.AddScoped<ISignupAttributionService, SignupAttributionService>();
        // SE-04: events of the SEO funnel (no personal data) and the featured properties of a comune.
        services.AddScoped<ISeoEventService, SeoEventService>();
        services.AddScoped<ISeoFeaturedPropertiesService, SeoFeaturedPropertiesService>();

        return services;
    }
}
