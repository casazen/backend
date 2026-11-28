using Casazen.Core.Authorization;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;

namespace Casazen.Web.Extensions;

public static class AuthorizationPoliciesServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenAuthorizationPolicies(this IServiceCollection services)
    {
        var builder = services.AddAuthorizationBuilder()
            .AddPolicy(CasazenPolicies.Authenticated, policy => policy.RequireAuthenticatedUser())
            .AddPolicy(CasazenPolicies.AdminOnly, policy => policy.RequireRole("Admin"))
            .AddPolicy(CasazenPolicies.Supplier, policy => policy.RequireRole("Supplier"))
            .AddPolicy(CasazenPolicies.OrgBillingAdmin, policy =>
                policy.Requirements.Add(new OrgBillingAdminRequirement()))
            .AddPolicy(CasazenPolicies.GuestSession, policy => policy
                .AddAuthenticationSchemes(GuestSessionDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireClaim(GuestSessionDefaults.OrgIdClaim))
            .AddPolicy(CasazenPolicies.SiteConnection, policy => policy
                .AddAuthenticationSchemes(SiteTokenDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireClaim(SiteTokenDefaults.ConnectionIdClaim)
                .RequireClaim(SiteTokenDefaults.OrgIdClaim)
                .RequireClaim(SiteTokenDefaults.ScopeClaim, SiteTokenDefaults.DomainScope))
            // The person who books as a guest with a CasaZen account (the mobile app's guest area): a signed-in account with a subject
            // and nothing else, since a guest has no host org, no supplier link and no role. The actions check the verified email.
            .AddPolicy(CasazenPolicies.GuestAccount, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => !string.IsNullOrWhiteSpace(context.User.GetUserId())))
            // A long-rent tenant has no host organization or host role. Each tenant-area action additionally binds the
            // subject to a lease through the account's verified Auth0 email and the lease's tenant party.
            .AddPolicy(CasazenPolicies.TenantAccount, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => !string.IsNullOrWhiteSpace(context.User.GetUserId())))
            .AddPolicy(CasazenPolicies.OwnerPortal, policy =>
            {
                policy.Requirements.Add(new OwnerPortalRequirement());
                policy.Requirements.Add(new OwnerPortalHostOriginRequirement());
            });

        builder.AddPolicy(CasazenPolicies.TrustedSupplierManage, policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(new AgencyPermissionRequirement(AgencyPermissions.SupplierManage)));

        foreach (var policyName in CasazenPolicies.ContextPolicies)
        {
            var (contextKeys, permissionKey) = CasazenPolicies.ParseContextPolicy(policyName);
            builder.AddPolicy(policyName, policy =>
                policy.Requirements.Add(new ContextPermissionRequirement(contextKeys, permissionKey)));
        }

        var queuePermissions = CasazenPolicies.ApprovalQueuePolicies.Select(name => CasazenPolicies.ParseContextPolicy(name).PermissionKey).ToArray();
        builder.AddPolicy(CasazenPolicies.ApprovalQueueAccess, policy => policy.Requirements.Add(
            new ContextPermissionRequirement([OrgRoleCatalog.ShortRent], queuePermissions[0], queuePermissions[1..])));

        // Agency tier policies (ADR-010): Org.PlanTier == PlanTier.Agency and, since DEB-01, the agency permission of the policy
        // held by the caller's org role (OrgRoleCatalog.AgencyPermissionsOf).
        foreach (var policyName in CasazenPolicies.AgencyPolicies)
        {
            var permissionKey = CasazenPolicies.ParseAgencyPolicy(policyName);
            builder.AddPolicy(policyName, policy =>
            {
                policy.Requirements.Add(new AgencyTierRequirement());
                policy.Requirements.Add(new AgencyPermissionRequirement(permissionKey));
            });
        }

        return services;
    }
}
