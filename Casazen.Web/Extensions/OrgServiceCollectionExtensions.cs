using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Web.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Casazen.Web.Extensions;

/// <summary>
/// The multi-tenant boundary and the org: tenant context, org services and branding, the public host resolver, the custom domains, the connection of
/// a site and the owner sign-in on an agency domain.
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
public static class OrgServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenOrgServices(this IServiceCollection services)
    {
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<IRequestTenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<IBackgroundTenantScope>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<IOrgContextResolver, OrgContextResolver>();
        services.AddScoped<ISupplierOrgContextResolver, SupplierOrgContextResolver>();
        services.AddScoped<IOrgService, OrgService>();
        services.AddScoped<IOrgBrandingService, OrgBrandingService>();
        services.AddScoped<IOrgSiteDocumentService, OrgSiteDocumentService>();
        services.AddSingleton<PublicHostMissCache>();
        services.AddScoped<IPublicHostResolver, PublicHostResolver>();
        services.AddScoped<ICorsOriginSource, OrgHostCorsOriginSource>();
        services.AddScoped<PublicOrgSiteUrls>();
        services.AddScoped<Casazen.Infrastructure.Email.AgencyDomainLinks>();
        services.AddScoped<Casazen.Web.Seo.IPublicSeoService, Casazen.Web.Seo.PublicSeoService>();
        services.AddScoped<IDnsTxtLookup, DnsClientTxtLookup>();
        services.AddScoped<IDnsRecordLookup, DnsClientRecordLookup>();
        // Custom domains on the Vercel project: add, verify, remove (BK-17, A3-25); no call without the token and project.
        // No redirects (the bearer token never follows one to another host) and a bounded answer.
        services.AddHttpClient<IVercelDomainsClient, Casazen.Infrastructure.External.VercelDomainsClient>(client =>
                client.MaxResponseContentBufferSize = 1024 * 1024)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddScoped<IDomainVerificationService, DomainVerificationService>();
        services.AddScoped<IDomainRecheckService, DomainRecheckService>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<Casazen.Infrastructure.Jobs.AgencyUsageJob>();
        services.AddScoped<IOrgDomainService, OrgDomainService>();
        services.AddScoped<ISiteConnectionService, SiteConnectionService>();
        services.AddScoped<ISiteAccountBuilder, SiteAccountBuilder>();
        // The owner sign-in on an agency's own domain (E40/F01/US02/T01, NF-141): the registrar of the hosts in the shared Auth0 tenant (Auth0:HostSync,
        // nothing is called without its client id or while Auth0HostSync is off) and the one place that moves the state and tells the org's administrators.
        services.AddOptions<Auth0HostSyncOptions>().BindConfiguration(Auth0HostSyncOptions.SectionName);
        services.AddSingleton<Auth0HostRegistrationGate>();
        services.AddScoped<IAuth0HostRegistrar, Auth0HostRegistrar>();
        services.AddScoped<OwnerLoginHostSync>();

        return services;
    }
}
