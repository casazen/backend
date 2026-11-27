// File: Casazen.Web/Extensions/ServiceCollectionExtensions.cs

using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Features;
using Casazen.Infrastructure.OTA;
using Casazen.Infrastructure.OTA.Resilience;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Configuration;
using Casazen.Web.Infrastructure;
using Casazen.Web.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Polly;

namespace Casazen.Web.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = NpgsqlConnectionStringNormalizer.Normalize(
            configuration.GetConnectionString("DefaultConnection"));
        // Bounded Npgsql pool (HOSTING): the EF contexts of requests and jobs share Database:MaxPoolSize connections.
        if (!string.IsNullOrEmpty(connectionString))
        {
            connectionString = DatabaseConnectionSettings
                .Resolve(configuration, connectionString, hangfireRegistered: false)
                .ForEntityFramework();
        }

        services.AddDbContext<AppDbContext>(options =>
        {
            if (!string.IsNullOrEmpty(connectionString))
            {
                options.UseNpgsql(
                    connectionString,
                    npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"));
            }
            else
            {
                options.UseInMemoryDatabase("CasazenTest");
                options.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
            }
        });
        return services;
    }

    public static IServiceCollection AddCasazenAuthorization(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddMemoryCache();
        services.AddScoped<UserAuthorizationSnapshotStore>();
        services.AddScoped<IUserAuthorizationSnapshotStore>(sp => sp.GetRequiredService<UserAuthorizationSnapshotStore>());
        services.AddScoped<IUserAuthorizationCache>(sp => sp.GetRequiredService<UserAuthorizationSnapshotStore>());
        services.AddScoped<IUserContextMembershipService, UserContextMembershipService>();
        // AM-01: the org's people (OrgMember) and, in the same transaction, the memberships that project their role.
        services.AddScoped<IOrgMembershipService, OrgMembershipService>();
        // AM-03: which properties a caller reaches (from the org membership, in the authorization snapshot), and who is the
        // holder of the org for the acts the law ties to the landlord (RLI delega, IMU communication).
        services.AddScoped<IHostScopeResolver, HostScopeResolver>();
        services.AddScoped<IOrgHolderService, OrgHolderService>();
        services.AddScoped<IContextAuthorizationService, ContextAuthorizationService>();
        // PL-02: host contexts only after the onboarding and the current consents; refusals answer 403 onboarding_required.
        services.AddScoped<IHostOnboardingGate, HostOnboardingGate>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, OnboardingRequiredAuthorizationResultHandler>();
        services.AddScoped<IAuthorizationHandler, ContextAuthorizationHandler>();

        services.AddScoped<IAuthorizationHandler, HostResourceAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, OrgBillingAdminAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, AgencyTierAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, AgencyPermissionAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, OwnerPortalAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, OwnerPortalHostOriginAuthorizationHandler>();

        // The complete policy set (TN-3), in its own file.
        services.AddCasazenAuthorizationPolicies();

        return services;
    }

    /// <summary>
    /// CORS restricted to the configured origins (<c>Cors:AllowedOrigins</c>, optional <c>Cors:VercelPreviewPattern</c>)
    /// plus the origin of the public web app (<c>App:PublicSiteBaseUrl</c>), without credentials (FD-17, A3-29 / A9-28,
    /// SE-02). No origin in code (decision D3): see <c>docs/runbooks/cors-security-headers.md</c>. Custom host domains
    /// plug in through <see cref="ICorsOriginSource"/>.
    /// </summary>
    public static IServiceCollection AddCasazenCors(this IServiceCollection services)
    {
        // Read from the final configuration (IConfiguration from DI), and validated when the host starts.
        services.AddOptions<CorsOriginOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                CorsOriginOptions.Configure(options, configuration.GetSection(CorsOriginOptions.SectionName));
                CorsOriginOptions.AddPublicSiteOrigin(options, configuration);
            })
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<CorsOriginOptions>, CorsOriginOptionsValidator>();
        services.AddSingleton<CorsOriginAllowList>();

        services.AddCors();
        // Replaces the default provider registered by AddCors; scoped so an ICorsOriginSource may use the DbContext.
        services.Replace(ServiceDescriptor.Scoped<ICorsPolicyProvider, CasazenCorsPolicyProvider>());
        return services;
    }

    public static IServiceCollection AddCasazenRepositories(this IServiceCollection services)
    {
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IPropertyRepository, PropertyRepository>();
        services.AddScoped<IBookingRepository, BookingRepository>();
        services.AddScoped<IPaymentRepository, PaymentRepository>();
        services.AddScoped<ITouristTaxRateRepository, TouristTaxRateRepository>();
        services.AddScoped<ITerritorialRentAgreementRepository, TerritorialRentAgreementRepository>();
        services.AddScoped<IHighTensionAreaComuneRepository, HighTensionAreaComuneRepository>();
        services.AddScoped<IComuneImuChannelRepository, ComuneImuChannelRepository>();
        services.AddScoped<IRegulatoryDataAuditLogRepository, RegulatoryDataAuditLogRepository>();
        services.AddScoped<ISeoContentRepository, SeoContentRepository>();
        services.AddScoped<IOtaSyncLogRepository, OtaSyncLogRepository>();
        services.AddScoped<IAlloggiatiWebReportRepository, AlloggiatiWebReportRepository>();
        services.AddScoped<IOtaIntegrationRepository, OtaIntegrationRepository>();
        services.AddScoped<IPropertyDocumentRepository, PropertyDocumentRepository>();
        return services;
    }

    /// <summary>
    /// The application services of the web host. Each area registers its own services in its own file of this folder
    /// (<c>*ServiceCollectionExtensions.cs</c>); the calls below follow the order in which the registrations stood when they were all in
    /// this one method. The order matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services: a new
    /// registration goes into the area it belongs to, in the position it needs.
    /// </summary>
    public static IServiceCollection AddCasazenServices(this IServiceCollection services)
    {
        services.AddCasazenBookingCoreServices();
        services.AddCasazenHostDashboardServices();
        services.AddCasazenBookingLifecycleServices();
        services.AddCasazenComplianceServices();
        services.AddCasazenCheckInIdentityServices();
        services.AddCasazenSwissComplianceServices();
        services.AddCasazenMarketingServices();
        services.AddCasazenRoutineServices();
        services.AddCasazenPropertyServices();
        services.AddCasazenPropertySenderServices();
        services.AddCasazenOrgServices();
        services.AddCasazenBillingServices();
        services.AddCasazenConcordatoFacileServices();
        services.AddCasazenLongRentAreaServices();
        services.AddCasazenGuestCheckInServices();
        services.AddCasazenComplianceCockpitServices();
        services.AddCasazenPropertyComplianceServices();
        services.AddCasazenLeaseRegistrationServices();
        services.AddCasazenAccountantPackageServices();
        services.AddCasazenOrgTeamServices();
        services.AddCasazenSupplierServices();
        services.AddCasazenComuniServices();
        services.AddCasazenSupplierRequestServices();
        services.AddCasazenShowcaseBookingServices();
        services.AddCasazenSupplierConsoleServices();
        services.AddCasazenCalendarSyncServices();
        services.AddCasazenDirectBookingServices();
        services.AddCasazenExternalHttpClients();

        return services;
    }

    /// <summary>
    /// The five-year retention of the documents of the Ticino police rules (NF-128): the daily job, scoped like the other jobs. A registration test resolves
    /// it with validated scopes.
    /// </summary>
    public static IServiceCollection AddSwissComplianceRetention(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<Casazen.Infrastructure.Jobs.SwissComplianceRetentionJob>();
        return services;
    }

    /// <summary>
    /// The document archive (NF-99, E07/F03/US03): the service, the sources it reads and the caps of the export (<c>DocumentArchive</c>, checked at
    /// startup). A source is a class that knows one table and who may read it: the property documents, the files of the leases, the receipts
    /// of the tourist tax payments and the proofs of the compliance duties the host uploaded (the Alloggiati receipts and the ISTAT proofs,
    /// «Prove di adempimento», E20/F02/US01/T01) are here; the documents of the fiscal epic add theirs when their tasks come. The guest identity
    /// documents and selfies are never a source.
    /// </summary>
    public static IServiceCollection AddDocumentArchive(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<DocumentArchiveOptions>()
            .BindConfiguration(DocumentArchiveOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DocumentArchiveOptions>, DocumentArchiveOptionsValidator>();
        services.AddScoped<IDocumentArchiveSource, PropertyDocumentArchiveSource>();
        services.AddScoped<IDocumentArchiveSource, LeaseDocumentArchiveSource>();
        services.AddScoped<IDocumentArchiveSource, TouristTaxRemittanceArchiveSource>();
        services.AddScoped<IDocumentArchiveSource, AlloggiatiReceiptArchiveSource>();
        services.AddScoped<IDocumentArchiveSource, IstatProofArchiveSource>();
        services.AddScoped<IDocumentArchiveService, DocumentArchiveService>();
        return services;
    }

    public static IServiceCollection AddCasazenOtaIntegrations(this IServiceCollection services, IConfiguration configuration)
    {
        // Always registered: OtaManager depends on it. With the flag off it has no
        // adapter to return, so nothing can call an OTA partner API.
        services.AddScoped<IChannelFactory, ChannelFactory>();

        // OTA partner API in freeze (D10): adapters, HTTP clients and rate limiter only with Features:OtaPartnerApi on.
        if (!ConfigurationFeatureFlags.IsEnabled(configuration, FeatureFlags.OtaPartnerApi))
            return services;

        // Register rate limiter as singleton (shared across all OTA adapters)
        services.AddSingleton<OtaRateLimiter>();

        // Configure HttpClients for each OTA adapter with Polly policies
        ConfigureOtaHttpClient<AirbnbAdapter>(services, configuration, "Airbnb");
        ConfigureOtaHttpClient<BookingComAdapter>(services, configuration, "BookingCom");
        ConfigureOtaHttpClient<ExpediaAdapter>(services, configuration, "Expedia");
        ConfigureOtaHttpClient<VrboAdapter>(services, configuration, "Vrbo");
        ConfigureOtaHttpClient<TripAdvisorAdapter>(services, configuration, "TripAdvisor");
        ConfigureOtaHttpClient<AgodaAdapter>(services, configuration, "Agoda");

        return services;
    }

    private static void ConfigureOtaHttpClient<TAdapter>(
        IServiceCollection services,
        IConfiguration configuration,
        string platform) where TAdapter : class
    {
        var resilienceConfig = configuration.GetSection($"OTA:Resilience:{platform}");
        var retryCount = resilienceConfig.GetValue<int>("RetryCount", 3);
        var circuitBreakerFailures = resilienceConfig.GetValue<int>("CircuitBreakerFailures", 5);
        var circuitBreakerDuration = TimeSpan.FromSeconds(resilienceConfig.GetValue<int>("CircuitBreakerDurationSeconds", 60));
        var timeout = TimeSpan.FromSeconds(resilienceConfig.GetValue<int>("TimeoutSeconds", 30));

        services.AddHttpClient<TAdapter>(client =>
        {
            client.Timeout = timeout.Add(TimeSpan.FromSeconds(5)); // Add buffer for retries
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.DefaultRequestHeaders.Add("User-Agent", "CasaZen/1.0");
        })
        .AddPolicyHandler((serviceProvider, request) =>
        {
            var logger = serviceProvider.GetRequiredService<ILogger<TAdapter>>();
            var context = new Context { ["Platform"] = platform };

            return PollyPolicies.GetCombinedPolicy(
                retryCount,
                circuitBreakerFailures,
                circuitBreakerDuration,
                timeout,
                logger
            ).WithPolicyKey($"{platform}-resilience");
        });
    }

    public static IApplicationBuilder UseCasazenMiddleware(this IApplicationBuilder app)
    {
        app.UseErrorHandling();
        return app;
    }
}
