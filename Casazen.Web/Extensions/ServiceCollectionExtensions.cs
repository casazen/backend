// File: Casazen.Web/Extensions/ServiceCollectionExtensions.cs

using System.Security.Claims;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Features;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.OTA;
using Casazen.Infrastructure.OTA.Resilience;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Services.ICal;
using Casazen.Web.Authorization;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Configuration;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Casazen.Web.Middleware;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Cors.Infrastructure;
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

    public static IServiceCollection AddCasazenAuthentication(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        // Domain and Audience validate every JWT: outside Development/Testing the app does not start without them (FD-12).
        var auth0Options = services.AddOptions<Auth0Options>()
            .Bind(configuration.GetSection(Auth0Options.SectionName));
        if (RequiredConfiguration.IsEnforced(environment))
        {
            services.AddSingleton<IValidateOptions<Auth0Options>, Auth0OptionsValidator>();
            auth0Options.ValidateOnStart();
        }

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                var domain = configuration["Auth0:Domain"];
                var audience = configuration["Auth0:Audience"];

                options.Authority = $"https://{domain}";
                options.Audience = audience;

                options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = $"https://{domain}/",
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    NameClaimType = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier",
                    RoleClaimType = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"
                };

                options.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = context =>
                    {
                        var logger = context.HttpContext.RequestServices
                            .GetRequiredService<ILogger<JwtBearerHandler>>();
                        logger.LogWarning("Authentication failed: {Error}", context.Exception.Message);
                        return Task.CompletedTask;
                    },
                    OnChallenge = context =>
                    {
                        var logger = context.HttpContext.RequestServices
                            .GetRequiredService<ILogger<JwtBearerHandler>>();
                        logger.LogWarning("Auth challenge — error: {Error}, description: {Description}",
                            context.Error, context.ErrorDescription);
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = async context =>
                    {
                        // Map Auth0 custom roles claim to standard .NET role claims
                        if (context.Principal?.Identity is ClaimsIdentity identity)
                        {
                            var roles = Auth0RolesClaimParser.Parse(
                                context.Principal.FindAll("https://casazen.app/roles").Select(c => c.Value));

                            foreach (var role in roles)
                            {
                                identity.AddClaim(new Claim(
                                    "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
                                    role));
                            }

                            // Backfill Supplier role from DB. A user just linked to a supplier
                            // org (registration, invite or claim, SU-02) has SupplierOrgId set but
                            // no Supplier role in the Auth0 JWT until a new token (or at all when
                            // the role sync failed). Adding the claim here lets the
                            // [Authorize(Policy="RequireSupplier")] filter pass right away.
                            var sub = context.Principal.FindFirstValue("sub")
                                ?? context.Principal.FindFirstValue(ClaimTypes.NameIdentifier);

                            if (!string.IsNullOrWhiteSpace(sub))
                            {
                                var hasSupplierRole = identity.Claims.Any(c =>
                                    c.Type == "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"
                                    && c.Value == "Supplier");

                                if (!hasSupplierRole)
                                {
                                    try
                                    {
                                        // Cached per request and for a short time per user (A4-30):
                                        // no DB round-trip on every authenticated call.
                                        var snapshots = context.HttpContext.RequestServices
                                            .GetRequiredService<IUserAuthorizationSnapshotStore>();
                                        var snapshot = await snapshots.GetAsync(
                                            sub, context.HttpContext.RequestAborted);

                                        if (snapshot.SupplierOrgId is not null)
                                        {
                                            identity.AddClaim(new Claim(
                                                "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
                                                "Supplier"));
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        var logger = context.HttpContext.RequestServices
                                            .GetRequiredService<ILogger<JwtBearerHandler>>();
                                        logger.LogWarning(ex,
                                            "Failed to backfill Supplier role for user {Sub}", sub);
                                    }
                                }
                            }
                        }
                    }
                };

                // Disable HTTPS requirement in development only
                if (environment.IsDevelopment())
                {
                    options.RequireHttpsMetadata = false;
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
        services.AddScoped<IContextAuthorizationService, ContextAuthorizationService>();
        // PL-02: host contexts only after the onboarding and the current consents; refusals answer 403 onboarding_required.
        services.AddScoped<IHostOnboardingGate, HostOnboardingGate>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, OnboardingRequiredAuthorizationResultHandler>();
        services.AddScoped<IAuthorizationHandler, ContextAuthorizationHandler>();

        services.AddScoped<IAuthorizationHandler, HostResourceAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, OrgBillingAdminAuthorizationHandler>();

        // The complete policy set (TN-3): see CasazenPolicies for how to choose one.
        var builder = services.AddAuthorizationBuilder()
            .AddPolicy(CasazenPolicies.Authenticated, policy => policy.RequireAuthenticatedUser())
            .AddPolicy(CasazenPolicies.AdminOnly, policy => policy.RequireRole("Admin"))
            .AddPolicy(CasazenPolicies.Supplier, policy => policy.RequireRole("Supplier"))
            .AddPolicy(CasazenPolicies.OrgBillingAdmin, policy =>
                policy.Requirements.Add(new OrgBillingAdminRequirement()));

        foreach (var policyName in CasazenPolicies.ContextPolicies)
        {
            var (contextKeys, permissionKey) = CasazenPolicies.ParseContextPolicy(policyName);
            builder.AddPolicy(policyName, policy =>
                policy.Requirements.Add(new ContextPermissionRequirement(contextKeys, permissionKey)));
        }

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
        services.AddScoped<ISeoContentRepository, SeoContentRepository>();
        services.AddScoped<IOtaSyncLogRepository, OtaSyncLogRepository>();
        services.AddScoped<IAlloggiatiWebReportRepository, AlloggiatiWebReportRepository>();
        services.AddScoped<IOtaIntegrationRepository, OtaIntegrationRepository>();
        services.AddScoped<IPropertyDocumentRepository, PropertyDocumentRepository>();
        return services;
    }

    public static IServiceCollection AddCasazenServices(this IServiceCollection services)
    {
        // Features:* flags (FD-20, docs/runbooks/feature-flags.md)
        services.AddSingleton<IFeatureFlags, ConfigurationFeatureFlags>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IPropertyService, PropertyService>();
        services.AddScoped<IBookingService, BookingService>();
        services.AddScoped<IOtaManager, OtaManager>();
        services.AddScoped<IPaymentService, PaymentService>();
        // Refunds and cancellations on Stripe Connect (BK-02, docs/runbooks/stripe.md "Refunds").
        services.AddScoped<PaymentRefundService>();
        services.AddScoped<IPaymentRefundService>(sp => sp.GetRequiredService<PaymentRefundService>());
        services.AddScoped<IBookingCancellationService, BookingCancellationService>();
        // Host changes to a booking: edit, confirm, check-out (PC-07).
        services.AddScoped<IHostBookingService, HostBookingService>();
        services.AddScoped<IPaymentRefundRetryScheduler, PaymentRefundRetryScheduler>();
        services.AddScoped<PaymentRefundSubmitJob>();
        // Late checkout payments: confirmed again or refunded in full (BK-04, docs/runbooks/stripe.md "Late payments").
        services.AddScoped<CheckoutPaymentSettlementService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IStayAlertService, StayAlertService>();
        services.AddScoped<IPushNotificationService, PushNotificationService>();
        services.AddHttpClient("ExpoPush");
        services.AddScoped<ITouristTaxService, TouristTaxService>();
        services.AddScoped<ITouristTaxQuoteService, TouristTaxQuoteService>();
        services.AddScoped<IGdprService, GdprService>();
        services.AddScoped<IOtaIntegrationService, OtaIntegrationService>();
        services.AddScoped<IPropertyDocumentService, PropertyDocumentService>();
        services.AddScoped<IApeDocumentInspector, ApeDocumentInspector>();
        services.AddScoped<IApeComplianceService, ApeComplianceService>();
        services.AddScoped<IPropertyAuthorizationService, PropertyAuthorizationService>();
        services.AddScoped<IHostResourceLookup, HostResourceLookup>();
        services.AddScoped<IAdminAccessAuditService, AdminAccessAuditService>();

        // Multi-tenant Org boundary (US-004): tenant resolution + org/entitlement reads.
        // One instance per request: the EF filter reads it, the middleware and the org resolver write it (A1-20).
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<IRequestTenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<IOrgContextResolver, OrgContextResolver>();
        services.AddScoped<ISupplierOrgContextResolver, SupplierOrgContextResolver>();
        services.AddScoped<IOrgService, OrgService>();
        services.AddScoped<IPublicHostResolver, PublicHostResolver>();
        services.AddScoped<IDnsTxtLookup, DnsClientTxtLookup>();
        services.AddScoped<IDomainVerificationService, DomainVerificationService>();
        services.AddScoped<IOrgDomainService, OrgDomainService>();
        services.AddScoped<IEntitlementService, EntitlementService>();
        services.AddScoped<IStripeBillingService, StripeBillingService>();
        services.AddScoped<IBillingCheckoutService, BillingCheckoutService>();
        services.AddScoped<IVatCalculationService, VatCalculationService>();
        services.AddScoped<IViesService, ViesService>();
        services.AddScoped<ISdiEInvoiceService, SdiEInvoiceService>();
        services.AddScoped<IBillingEntryGate, BillingEntryGate>();
        services.AddScoped<IOssRevenueTracker, OssRevenueTracker>();
        services.AddScoped<IRentBillingService, NullRentBillingService>();
        services.AddScoped<ISeoContentService, SeoContentService>();
        services.AddScoped<IGuestAccessService, GuestAccessService>();
        services.AddScoped<IGuestCheckInService, GuestCheckInService>();
        // Guests of a stay and official Alloggiati code tables (CO-12).
        services.AddScoped<IAlloggiatiCodeTableService, AlloggiatiCodeTableService>();
        services.AddScoped<IStayGuestService, StayGuestService>();
        services.AddScoped<IComplianceWizardService, ComplianceWizardService>();
        services.AddScoped<ICanoneConcordatoEligibilityService, CanoneConcordatoEligibilityService>();
        services.AddScoped<IAttestationGuidanceService, AttestationGuidanceService>();
        services.AddScoped<IComuneImuNotificationService, ComuneImuNotificationService>();
        services.AddScoped<ILeaseRegistrationAuthorizationRepository, LeaseRegistrationAuthorizationRepository>();
        services.AddScoped<ICedolareAdvisoryService, CedolareAdvisoryService>();
        services.AddScoped<IRliExportService, RliExportService>();
        services.AddScoped<IRliChecklistService, RliChecklistService>();
        // RLI registration (LT-01, D15): manual by default. No provider client exists yet (docs/runbooks/rli.md), so the
        // provider path stays unavailable even with Features:RliProvider on.
        services.AddScoped<IRliRegistrationService, RliRegistrationService>();
        services.AddSingleton<ILeaseRegistrationProvider, UnconfiguredLeaseRegistrationProvider>();
        services.AddScoped<IFiscalRegimeService, FiscalService>();
        services.AddScoped<IFiscalReportingService>(sp => (FiscalService)sp.GetRequiredService<IFiscalRegimeService>());
        services.AddSingleton<ILegalDocumentService, LegalDocumentService>();
        services.AddScoped<IOnboardingService, OnboardingService>();
        services.AddScoped<ISignupAttributionService, SignupAttributionService>();
        services.AddScoped<ISupplierService, Casazen.Infrastructure.Services.SupplierService>();

        // Pilot comuni of supplier self-serve registration (SU-01, runbook suppliers.md): no default, validated at startup.
        services.AddOptions<SupplierRegistrationOptions>()
            .BindConfiguration(SupplierRegistrationOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SupplierRegistrationOptions>, SupplierRegistrationOptionsValidator>();
        services.AddScoped<IServiceRequestRepository, ServiceRequestRepository>();
        services.AddScoped<IServiceRequestService, ServiceRequestService>();
        services.AddScoped<ISupplierMatchService, SupplierMatchService>();
        services.AddScoped<CalendarSyncService>();
        // iCal import (PC-10, runbook ical.md): recurrence window, optional section ICalImport.
        services.AddOptions<ICalImportOptions>().BindConfiguration(ICalImportOptions.SectionName);
        services.AddScoped<ICalImportService>();
        services.AddScoped<ICalExportService>();
        services.AddScoped<PropertyICalSyncService>();
        services.AddScoped<ICheckoutHoldExpiryService, CheckoutHoldExpiryService>();
        // "Pay at the property" requests approved by the host (BK-06, D5, docs/runbooks/direct-booking.md).
        services.AddScoped<OnSiteRequestNotifier>();
        services.AddScoped<IOnSiteBookingRequestService, OnSiteBookingRequestService>();
        // Outcome page of the public checkout, read with the checkout token (BK-07, A3-15).
        services.AddScoped<ICheckoutOutcomeService, CheckoutOutcomeService>();
        services.AddSingleton<QrCodeService>();
        services.AddScoped<NotificationRouter>();
        services.AddScoped<INotificationChannel, EmailNotificationChannel>();
        services.AddScoped<INotificationChannel, DashboardNotificationChannel>();

        // User-chosen URLs (iCal feeds) are downloaded only through the anti-SSRF client (FD-16).
        services.AddOptions<SafeExternalHttpOptions>().BindConfiguration(SafeExternalHttpOptions.SectionName);
        services.AddSingleton<IExternalHostResolver, SystemDnsHostResolver>();
        services.AddSingleton<ISafeExternalHttpClient, SafeExternalHttpClient>();
        return services;
    }

    public static IServiceCollection AddCasazenOtaIntegrations(this IServiceCollection services, IConfiguration configuration)
    {
        // Always registered: OtaManager depends on it (DynamicPricingJob uses OtaManager). With the flag off it has no
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
