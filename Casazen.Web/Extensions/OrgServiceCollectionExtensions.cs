using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
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
        // Multi-tenant Org boundary (US-004): tenant resolution + org/entitlement reads.
        // One instance per request: the EF filter reads it, the middleware and the org resolver write it (A1-20).
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<IRequestTenantContext>(sp => sp.GetRequiredService<TenantContext>());
        // A scope opened outside a request (a handler of the event bus) is put under one org through the same instance (ADR-022-R6).
        services.AddScoped<IBackgroundTenantScope>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<IOrgContextResolver, OrgContextResolver>();
        services.AddScoped<ISupplierOrgContextResolver, SupplierOrgContextResolver>();
        services.AddScoped<IOrgService, OrgService>();
        services.AddScoped<IOrgBrandingService, OrgBrandingService>();
        services.AddScoped<IOrgSiteDocumentService, OrgSiteDocumentService>();
        // The hosts that resolve to no org are remembered, bounded, across requests (BK-16).
        services.AddSingleton<PublicHostMissCache>();
        services.AddScoped<IPublicHostResolver, PublicHostResolver>();
        // Origins of the orgs' own sites (subdomains and verified custom domains), public endpoints only (BK-16).
        services.AddScoped<ICorsOriginSource, OrgHostCorsOriginSource>();
        // Where an org's public site lives (own host or platform path) and its canonical URLs (BK-16).
        services.AddScoped<PublicOrgSiteUrls>();
        // The links CasaZen sends for an org, on the agency's own domain or on the CasaZen one (E40/F01/US01/T03).
        services.AddScoped<Casazen.Infrastructure.Email.AgencyDomainLinks>();
        // Pages and sitemaps of the public site as crawlers read them (BK-15).
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
        // The connection of a site to the org (S022): the one-time code, its exchange for the plugin's credential, the checks of the SiteToken scheme.
        services.AddScoped<ISiteConnectionService, SiteConnectionService>();
        // The account of a connected plugin (S023): the org, the address of its embeddable pages and its domain settings, made once for the four endpoints that answer it.
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
