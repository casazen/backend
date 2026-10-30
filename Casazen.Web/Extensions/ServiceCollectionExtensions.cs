// File: Casazen.Web/Extensions/ServiceCollectionExtensions.cs

using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Documents;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Features;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.OTA;
using Casazen.Infrastructure.OTA.Resilience;
using Casazen.Core.Options;
using Casazen.Infrastructure.OfficialData;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Services.CfSimulator;
using Casazen.Infrastructure.Services.ICal;
using Casazen.Web.Authorization;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Configuration;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Casazen.Web.Middleware;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Authentication;
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
                    OnMessageReceived = context =>
                    {
                        // WebSockets and server-sent events carry the token of a SignalR connection in the query (hubs only).
                        if (context.Request.Headers.Authorization.Count == 0 && HubAccessToken.TryRead(context.Request, out var hubToken))
                        {
                            context.Token = hubToken;
                            return Task.CompletedTask;
                        }

                        // The token of a guest session (DB-06a) is not a JWT: only the GuestSession scheme reads it. The sign-in
                        // of the hosts leaves it alone, so it neither tries to validate it nor logs a failure at every call.
                        if (context.Request.Headers.Authorization.ToString()
                            .StartsWith("Bearer " + GuestSessionTokens.Prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            context.NoResult();
                        }

                        return Task.CompletedTask;
                    },
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
            })
            // The guest of a booking site with no account (DB-06a): its own scheme, used only by the policy GuestSession.
            .AddScheme<AuthenticationSchemeOptions, GuestSessionAuthenticationHandler>(
                GuestSessionDefaults.AuthenticationScheme,
                _ => { });

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
        services.AddScoped<IAuthorizationHandler, OwnerPortalAuthorizationHandler>();

        // The complete policy set (TN-3): see CasazenPolicies for how to choose one.
        var builder = services.AddAuthorizationBuilder()
            .AddPolicy(CasazenPolicies.Authenticated, policy => policy.RequireAuthenticatedUser())
            .AddPolicy(CasazenPolicies.AdminOnly, policy => policy.RequireRole("Admin"))
            .AddPolicy(CasazenPolicies.Supplier, policy => policy.RequireRole("Supplier"))
            .AddPolicy(CasazenPolicies.OrgBillingAdmin, policy =>
                policy.Requirements.Add(new OrgBillingAdminRequirement()))
            // The guest of a booking site with no account (DB-06a): its own authentication scheme, no role, no host context.
            .AddPolicy(CasazenPolicies.GuestSession, policy => policy
                .AddAuthenticationSchemes(GuestSessionDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireClaim(GuestSessionDefaults.OrgIdClaim))
            // Owner portal access (ADR-011): context=owner_portal claim, MUST NOT include host roles.
            .AddPolicy(CasazenPolicies.OwnerPortal, policy =>
                policy.Requirements.Add(new OwnerPortalRequirement()));

        foreach (var policyName in CasazenPolicies.ContextPolicies)
        {
            var (contextKeys, permissionKey) = CasazenPolicies.ParseContextPolicy(policyName);
            builder.AddPolicy(policyName, policy =>
                policy.Requirements.Add(new ContextPermissionRequirement(contextKeys, permissionKey)));
        }

        // Agency tier policies (ADR-010): all require Org.PlanTier == PlanTier.Agency
        foreach (var policyName in CasazenPolicies.AgencyPolicies)
        {
            builder.AddPolicy(policyName, policy =>
                policy.Requirements.Add(new AgencyTierRequirement()));
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
        services.AddScoped<IComuneImuChannelRepository, ComuneImuChannelRepository>();
        services.AddScoped<IRegulatoryDataAuditLogRepository, RegulatoryDataAuditLogRepository>();
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
        // Public availability of the booking site, same nights as the booking checks (BK-05).
        services.AddScoped<IPublicAvailabilityService, PublicAvailabilityService>();
        // The calendar of a property on the booking site: availability and price of every night, from the quote's own functions (DB-03b).
        services.AddScoped<IPublicCalendarService, PublicCalendarService>();
        // The search of a booking site (the houses for a stay, the calendar of the whole site, the places, the houses that look alike) and
        // the small cache that keeps its answers for a few seconds (DB-07a).
        services.AddSingleton<PublicReadCache>();
        services.AddScoped<IPublicSiteSearchService, PublicSiteSearchService>();
        // Host dashboard KPIs per period and iCal feeds widget (PC-16).
        services.AddScoped<IHostDashboardService, HostDashboardService>();
        // The host's day in one read: stays of the day, requests, things to do (SR-03). What the cockpit items lack is a service apart.
        services.AddScoped<IHostTodayService, HostTodayService>();
        services.AddScoped<IComplianceMissingService, ComplianceMissingService>();
        // The calendar of all the host's properties in one read, and the nights taken twice the Home puts among the things to do (SR-02a).
        services.AddScoped<IHostCalendarService, HostCalendarService>();
        // Lists that hold up with many rows: bookings by days, status and text, a page at a time; payments by booking and period (SR-03).
        services.AddScoped<IBookingSearchService, BookingSearchService>();
        // The history of one booking, derived from its timestamps and the rows tied to it (SR-05a, SR-D17).
        services.AddScoped<IBookingTimelineService, BookingTimelineService>();
        services.AddScoped<IPaymentListService, PaymentListService>();
        services.AddScoped<IOtaManager, OtaManager>();
        services.AddScoped<IPaymentService, PaymentService>();
        // A payment the host received outside CasaZen (cash, bank transfer): born Completed, capped to the balance, once per key (SR-06b).
        services.AddScoped<IManualPaymentService, ManualPaymentService>();
        // Refunds and cancellations on Stripe Connect (BK-02, docs/runbooks/stripe.md "Refunds").
        services.AddScoped<PaymentRefundService>();
        services.AddScoped<IPaymentRefundService>(sp => sp.GetRequiredService<PaymentRefundService>());
        services.AddScoped<IBookingCancellationService, BookingCancellationService>();
        // Guest self-service cancellation via signed link (BK-02, BK-07, PO 2026-10-08).
        services.AddScoped<IGuestBookingCancellationService, GuestBookingCancellationService>();
        // Host changes to a booking: edit, confirm, check-out (PC-07).
        services.AddScoped<IHostBookingService, HostBookingService>();
        services.AddScoped<IOtaStayService, OtaStayService>();
        // Whether a stay of the host's property is free, and the booking the host enters by hand with only the name and the email (SR-06a).
        services.AddScoped<IHostAvailabilityService, HostAvailabilityService>();
        services.AddScoped<IManualBookingService, ManualBookingService>();
        services.AddScoped<ICalendarBlockService, CalendarBlockService>();
        // Scheduled change of rental mode of a property: preview, creation, the hourly application (PM-02).
        services.AddScoped<IPropertyModeService, PropertyModeService>();
        // Global search of the palette: the groups the caller may read (policies) and the SQL (UI-13a, docs/runbooks/global-search.md).
        services.AddScoped<ISearchAccessResolver, SearchAccessResolver>();
        services.AddScoped<Casazen.Core.Search.IGlobalSearchService, Casazen.Infrastructure.Search.GlobalSearchService>();
        services.AddScoped<IStayLifecycleService, StayLifecycleService>();
        services.AddScoped<IPaymentRefundRetryScheduler, PaymentRefundRetryScheduler>();
        services.AddScoped<PaymentRefundSubmitJob>();
        // Late checkout payments: confirmed again or refunded in full (BK-04, docs/runbooks/stripe.md "Late payments").
        services.AddScoped<CheckoutPaymentSettlementService>();
        // Booking confirmation (guest + host) and cancellation emails (BK-10, docs/runbooks/email.md).
        services.AddScoped<BookingNotifier>();
        // Deferred charge of "Paga alla scadenza" bookings: job and webhooks (BK-08, docs/runbooks/direct-booking.md § 9).
        services.AddScoped<DeferredChargeService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IStayAlertService, StayAlertService>();
        // Approval queue for items requiring human decision before taking effect (NF-06)
        services.AddScoped<IApprovalQueueService, ApprovalQueueService>();
        // CIN deadline (CO-20, runbook cin-format.md): optional date, validated at startup; daily host alert.
        services.AddOptions<Casazen.Core.Options.CinOptions>()
            .BindConfiguration(Casazen.Core.Options.CinOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<Casazen.Core.Options.CinOptions>, Casazen.Core.Options.CinOptionsValidator>();
        services.AddSingleton<Casazen.Core.Regulatory.CinDeadlineCalendar>();
        services.AddScoped<ICinDeadlineAlertService, CinDeadlineAlertService>();
        services.AddScoped<ITouristTaxService, TouristTaxService>();
        services.AddScoped<ITouristTaxQuoteService, TouristTaxQuoteService>();
        services.AddScoped<IGdprService, GdprService>();
        // CO-15: guest data rights and retention per category (docs/runbooks/gdpr.md); no period has a default.
        services.AddScoped<GuestDataEraser>();
        services.AddScoped<IGuestDataRetentionService, GuestDataRetentionService>();
        services.AddScoped<ILeasePartyPrivacyService, LeasePartyPrivacyService>();
        // NF-10: Document OCR and MRZ extraction for guest check-in (ADR-009-R22, ADR-013-R10).
        services.AddScoped<IMrzExtractionService, MrzExtractionService>();
        services.AddScoped<IDocumentOcrService, DocumentOcrService>();
        // NF-11: Biometric verification for guest check-in (ADR-013).
        services.AddScoped<IAntiSpoofingService, AntiSpoofingService>();
        services.AddScoped<IBiometricVerificationService, BiometricVerificationService>();
        // NF-12: Operator verification queue for manual biometric decisions (ADR-013).
        services.AddScoped<IVerificationQueueService, VerificationQueueService>();
        // NF-13: Property access codes with encryption and verification gating (E06/F03/US01/T01).
        services.AddScoped<IAccessCodeService, AccessCodeService>();
        // NF-15: Swiss compliance files of the Ticino properties (ADR-016, E08): police notification XML, OTR overnight CSV and the
        // archive of the receipts the cantonal portal gives back. Used by SwissComplianceController.
        services.AddScoped<ITicinoNotificationService, TicinoNotificationService>();
        services.AddScoped<IOtrTaxService, OtrTaxService>();
        services.AddScoped<ISwissComplianceReceiptService, SwissComplianceReceiptService>();
        services.AddOptions<Casazen.Core.Options.GdprOptions>()
            .BindConfiguration(Casazen.Core.Options.GdprOptions.SectionName);
        // NF-05: Marketing suppression list with blind index (SHA-256) for one-click unsubscribe (ADR-007-R18, R22).
        services.AddScoped<ISuppressionService, SuppressionService>();
        // NF-24: Marketing campaigns and guest segmentation (ADR-014).
        services.AddScoped<IConditionEvaluator, ConditionEvaluator>();
        services.AddScoped<ICampaignService, CampaignService>();
        // NF-26: Automation routines (ADR-022).
        services.AddScoped<IRoutineService, RoutineService>();
        services.AddScoped<RoutineEventDispatcher>();
        services.AddScoped<IOtaIntegrationService, OtaIntegrationService>();
        services.AddScoped<IPropertyDocumentService, PropertyDocumentService>();
        services.AddScoped<IPropertyPhotoService, PropertyPhotoService>();
        // DB-11a: the optional facts and texts of the property page of the booking site (api/properties/{id}/site-profile).
        services.AddScoped<IPropertySiteProfileService, PropertySiteProfileService>();
        services.AddScoped<IApeDocumentInspector, ApeDocumentInspector>();
        services.AddScoped<IApeComplianceService, ApeComplianceService>();
        // The code of an APE the file does not show legibly, from the public CENED register of Lombardy (open data, no key).
        services.AddOptions<Casazen.Core.Options.ApeRegistryOptions>()
            .BindConfiguration(Casazen.Core.Options.ApeRegistryOptions.SectionName);
        services.AddHttpClient<IApeRegistryLookup, Casazen.Infrastructure.External.CenedApeRegistry>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CasaZen-ape-register/1.0");
        });
        services.AddScoped<IPropertyAuthorizationService, PropertyAuthorizationService>();
        services.AddScoped<IHostResourceLookup, HostResourceLookup>();
        services.AddScoped<IAdminAccessAuditService, AdminAccessAuditService>();
        services.AddScoped<IConcordatoFacileAdminService, ConcordatoFacileAdminService>();
        // Alloggiati Web credentials of a property: write-only, encrypted at rest (CO-14, A5-30).
        services.AddScoped<IQuesturaCredentialsService, QuesturaCredentialsService>();

        // Multi-tenant Org boundary (US-004): tenant resolution + org/entitlement reads.
        // One instance per request: the EF filter reads it, the middleware and the org resolver write it (A1-20).
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<IRequestTenantContext>(sp => sp.GetRequiredService<TenantContext>());
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
        services.AddScoped<IOrgDomainService, OrgDomainService>();
        services.AddScoped<IEntitlementService, EntitlementService>();
        services.AddScoped<IStripeBillingService, StripeBillingService>();
        services.AddScoped<IBillingCheckoutService, BillingCheckoutService>();
        // PL-13: VAT computed by Stripe Tax (no rate in code); no SDI provider is integrated in this build, so the
        // e-invoices are issued manually (health check "einvoicing", docs/runbooks/billing-tax.md).
        services.AddScoped<ISdiEInvoiceProvider, UnconfiguredSdiEInvoiceProvider>();
        services.AddScoped<IPlatformInvoiceService, PlatformInvoiceService>();
        services.AddScoped<IBillingEntryGate, BillingEntryGate>();
        services.AddScoped<IRentBillingService, RentBillingService>();
        // ConcordatoFacile one-time payment (ADR-005): Stripe Checkout with validated Price ID configuration.
        services.AddOptions<Casazen.Core.Options.ConcordatoFacileOptions>()
            .BindConfiguration(Casazen.Core.Options.ConcordatoFacileOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<Casazen.Core.Options.ConcordatoFacileOptions>, Casazen.Core.Options.ConcordatoFacileOptionsValidator>();
        services.AddScoped<IConcordatoFacileCheckoutService, ConcordatoFacileCheckoutService>();
        services.AddScoped<IConcordatoFacileAccessService, ConcordatoFacileAccessService>();
        services.AddScoped<IConcordatoFacileGiftService, ConcordatoFacileGiftService>();
        services.AddScoped<IConcordatoFacileLinkService, ConcordatoFacileLinkService>();
        // The privacy notice accepted for a lease before the guided form is opened; the signed link and the controller take it (CF-BE).
        services.AddScoped<IConcordatoFacilePrivacyService, ConcordatoFacilePrivacyService>();
        // The practices of a lease and the lookup of a practice, read from the ConcordatoFacile status API and kept in memory (ADR-008). The
        // controller of the module takes it in its constructor: without this registration every one of its actions fails (CF-FIX2).
        services.AddScoped<IConcordatoFacileStatusCacheService, ConcordatoFacileStatusCacheService>();
        // The simulated module and status API of the test environment (CF-SIM, docs/runbooks/concordatofacile-simulator.md). The status
        // cache reads the practices from IConcordatoFacileApiClient: the HTTP client of the real status API, except with
        // ConcordatoFacile:FormMode=Simulated, which the startup refuses in Production. Chosen when the client is asked for, not when the
        // application is built, like every other read of these options.
        services.AddScoped<ICfSimulatorService, CfSimulatorService>();
        services.AddScoped<SimulatedConcordatoFacileApiClient>();
        services.AddScoped<IConcordatoFacileApiClient>(provider =>
            provider.GetRequiredService<IOptions<ConcordatoFacileOptions>>().Value.FormMode == ConcordatoFacileFormMode.Simulated
                ? provider.GetRequiredService<SimulatedConcordatoFacileApiClient>()
                : provider.GetRequiredService<ConcordatoFacileApiClient>());
        // Rent register, agenda and overview of the long-term area (LR-01): read side, scope per property in SQL.
        services.AddScoped<IRentRegisterService, RentRegisterService>();
        services.AddScoped<ILongRentAgendaService, LongRentAgendaService>();
        // Fiscal report per lease for the accountant (LR-11b): the rules of the tax advisory (LT-08) on the leases of the scope.
        services.AddScoped<ILongRentFiscalReportService, LongRentFiscalReportService>();
        // The tenants of the area (LR-09): the people read from the parties of the leases, the copy of their data and their erasure.
        services.AddScoped<ILongRentTenantService, LongRentTenantService>();
        // The properties of the area with the APE, the cadastral data, who holds them and the change of mode programmed (LR-07a).
        services.AddScoped<ILongRentPropertyService, LongRentPropertyService>();
        services.AddScoped<ISeoContentService, SeoContentService>();
        services.AddScoped<IGuestAccessService, GuestAccessService>();
        services.AddScoped<IGuestCheckInService, GuestCheckInService>();
        // Guests of a stay and official Alloggiati code tables (CO-12).
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
        services.AddScoped<IComplianceWizardService, ComplianceWizardService>();
        // D.L. 145/2023 safety checklist of a short-stay property (CO-07).
        services.AddScoped<IPropertySafetyChecklistService, PropertySafetyChecklistService>();
        // Single evaluation of the activation blockers, suspension and nightly check of the published properties (CO-06).
        services.AddScoped<IPropertyComplianceStatusService, PropertyComplianceStatusService>();
        // What the host's list shows of each property besides its record: one state, what is missing, the nights (SR-07a).
        services.AddScoped<IPropertyOverviewService, PropertyOverviewService>();
        services.AddSingleton<IDemoEnvironmentGate, Casazen.Web.Demo.HostDemoEnvironmentGate>();
        services.AddScoped<IDemoEnvironmentService, DemoEnvironmentService>();
        services.AddScoped<ICanoneConcordatoEligibilityService, CanoneConcordatoEligibilityService>();
        services.AddScoped<IAttestationGuidanceService, AttestationGuidanceService>();
        // Single PDF renderer (LT-09, A7-14): A4, wrapping, pagination, embedded Unicode fonts. Stateless.
        services.AddSingleton<IPdfDocumentRenderer, MigraDocPdfDocumentRenderer>();
        services.AddScoped<IComuneImuNotificationService, ComuneImuNotificationService>();
        services.AddScoped<IRegulatoryReferenceDataAdminService, RegulatoryReferenceDataAdminService>();
        services.AddScoped<ILeaseRegistrationAuthorizationRepository, LeaseRegistrationAuthorizationRepository>();
        // LTR tax advisory (LT-08, docs/runbooks/rli.md): every rate and minimum from configuration, validated at startup.
        services.AddOptions<Casazen.Core.Options.CedolareAdvisoryOptions>()
            .BindConfiguration(Casazen.Core.Options.CedolareAdvisoryOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<Casazen.Core.Options.CedolareAdvisoryOptions>, Casazen.Core.Options.CedolareAdvisoryOptionsValidator>();
        services.AddScoped<ICedolareAdvisoryService, CedolareAdvisoryService>();
        services.AddScoped<IRliExportService, RliExportService>();
        services.AddScoped<IRliChecklistService, RliChecklistService>();
        // Questura communication for an extra-EU tenant (LT-07): declared by the landlord, never inferred from a reminder.
        services.AddScoped<IQuesturaCommunicationService, QuesturaCommunicationService>();
        // RLI registration (LT-01, D15): manual by default. No provider client exists yet (docs/runbooks/rli.md), so the
        // provider path stays unavailable even with Features:RliProvider on.
        services.AddScoped<IRliRegistrationService, RliRegistrationService>();
        services.AddSingleton<ILeaseRegistrationProvider, UnconfiguredLeaseRegistrationProvider>();
        services.AddScoped<IFiscalRegimeService, FiscalService>();
        services.AddScoped<IFiscalReportingService>(sp => (FiscalService)sp.GetRequiredService<IFiscalRegimeService>());
        services.AddSingleton<ILegalDocumentService, LegalDocumentService>();
        services.AddScoped<IOnboardingService, OnboardingService>();
        // AM-02: the people of an org: seats of the plan, invitations and their acceptance, the members and the hourly
        // maintenance (reminders, expiry, deletion of the closed ones).
        services.AddScoped<IOrgSeatService, OrgSeatService>();
        services.AddScoped<IOrgEmptinessChecker, OrgEmptinessChecker>();
        services.AddScoped<IOrgInvitationService, OrgInvitationService>();
        services.AddScoped<IOwnerInvitationService, OwnerInvitationService>();
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
        services.AddScoped<ISupplierService, Casazen.Infrastructure.Services.SupplierService>();
        // The supplier's catalog of services with prices (SP-02): rows keyed by the supplier org, not tenant-filtered.
        services.AddScoped<ISupplierServiceCatalogService, SupplierServiceCatalogService>();
        // The supplier's agenda (SP-03): hours, time off, blocks, rules, calendar and the input of the slot planner. Rows keyed
        // by the supplier org, not tenant-filtered; every write under the SupplierCalendarSync lock.
        services.AddScoped<ISupplierAgendaService, SupplierAgendaService>();
        // The content of the supplier's showcase (SP-13a): the draft, the published version, and the changes between them. Rows keyed
        // by the supplier org, not tenant-filtered; every change under the SupplierShowcaseContent lock.
        services.AddScoped<ISupplierShowcaseContentService, SupplierShowcaseContentService>();
        // The anonymous read side of the supplier showcase (SP-09): services, free slots and price estimate, behind the flag
        // SupplierShowcaseBooking. The slot plans are kept 30 seconds per replica (a singleton: not tied to a request).
        services.AddSingleton<PublicSupplierSlotCache>();
        services.AddScoped<IPublicSupplierShowcaseService, PublicSupplierShowcaseService>();
        // What the Portal's agenda reads on top of the planner (SP-03b): the conflicts of the jobs to come with a time to propose
        // instead, and the supplier's own preview of the slots of a service (no cache, no flag, draft and paused services too).
        services.AddScoped<ISupplierConflictService, SupplierConflictService>();
        services.AddScoped<ISupplierSlotPreviewService, SupplierSlotPreviewService>();
        // What the host reads of a supplier to ask it for an intervention: its published services and their free slots (SR-08a).
        services.AddScoped<IHostSupplierCatalogService, HostSupplierCatalogService>();
        // Admin list, suspension and invites of the suppliers (SU-12, A4-29).
        services.AddScoped<ISupplierAdminService, SupplierAdminService>();
        // Invitation of new independent hosts by admins and hosts: limits are configuration (HostInvites), validated at startup.
        services.AddOptions<Casazen.Core.Options.HostInviteOptions>()
            .BindConfiguration(Casazen.Core.Options.HostInviteOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<Casazen.Core.Options.HostInviteOptions>, Casazen.Core.Options.HostInviteOptionsValidator>();
        services.AddScoped<IHostInviteService, HostInviteService>();
        services.AddScoped<IHostInviteRetentionService, HostInviteRetentionService>();

        // Official ISTAT comuni list (SU-04): read side, import (admin upload and the seed file of the deploy), the SEO view,
        // the supplier matching by ISTAT code and the validated pilot comuni of the supplier registration.
        services.AddScoped<IComuneDirectory, ComuneDirectory>();
        services.AddScoped<IComuneImportService, ComuneImportService>();
        services.AddScoped<ISeoComuneCatalog, SeoComuneCatalog>();
        services.AddScoped<ISupplierComuneMatcher, SupplierComuneMatcher>();
        services.AddScoped<ISupplierPilotComuni, SupplierPilotComuni>();
        services.AddScoped<IPropertyComuneResolver, PropertyComuneResolver>();

        // Pilot comuni of supplier self-serve registration (SU-01, runbook suppliers.md): no default, validated at startup.
        services.AddOptions<SupplierRegistrationOptions>()
            .BindConfiguration(SupplierRegistrationOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SupplierRegistrationOptions>, SupplierRegistrationOptionsValidator>();
        services.AddScoped<IServiceRequestRepository, ServiceRequestRepository>();
        // Service requests with their time and price (SP-04): the emails and pushes to the two parties, the lifecycle after the
        // take, the photos of the work; the times and the tolerance are configuration (Suppliers:ServiceRequests), validated at startup.
        services.AddOptions<Casazen.Core.Options.ServiceRequestOptions>()
            .BindConfiguration(Casazen.Core.Options.ServiceRequestOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<Casazen.Core.Options.ServiceRequestOptions>, Casazen.Core.Options.ServiceRequestOptionsValidator>();
        services.AddScoped<ServiceRequestNotifier>();
        // Payment of the service requests inside CasaZen (SP-15a, decision D2): direct charge on the supplier's Stripe account with
        // the platform commission. The commission and the times are configuration (SupplierPayments), validated at startup: the
        // percentage has no default in code. The gateway is the only place that sets the application fee.
        services.AddOptions<Casazen.Core.Options.SupplierPaymentsOptions>()
            .BindConfiguration(Casazen.Core.Options.SupplierPaymentsOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<Casazen.Core.Options.SupplierPaymentsOptions>, Casazen.Core.Options.SupplierPaymentsOptionsValidator>();
        services.AddSingleton<ISupplierPaymentGateway, StripeSupplierPaymentGateway>();
        // The payment service holds the payer's side (SP-15a) and, in its own interfaces, the Stripe webhook, the jobs and the admin
        // refunds (SP-15b): one instance per scope, so they share the payment lock and the way a link is issued.
        services.AddScoped<SupplierPaymentService>();
        services.AddScoped<ISupplierPaymentService>(sp => sp.GetRequiredService<SupplierPaymentService>());
        services.AddScoped<ISupplierPaymentWebhookService>(sp => sp.GetRequiredService<SupplierPaymentService>());
        services.AddScoped<ISupplierPaymentJobService>(sp => sp.GetRequiredService<SupplierPaymentService>());
        services.AddScoped<ISupplierPaymentRefundService>(sp => sp.GetRequiredService<SupplierPaymentService>());
        services.AddScoped<ISupplierPaymentAdminService, SupplierPaymentAdminService>();
        services.AddScoped<ISupplierPaymentJobScheduler, SupplierPaymentJobScheduler>();
        // One instance per request for both doors (SP-11): the hosts' and the suppliers' operations, and the narrow set of what the
        // customer of a public showcase does to its own request, which only the customer's area (IShowcaseBookingManager) uses.
        services.AddScoped<ServiceRequestService>();
        services.AddScoped<IServiceRequestService>(provider => provider.GetRequiredService<ServiceRequestService>());
        services.AddScoped<IShowcaseRequestCustomerActions>(provider => provider.GetRequiredService<ServiceRequestService>());
        // Automatic cancellation of the requests nobody answered (SP-04, D8): the recurring job runs only with the flag on.
        services.AddScoped<IServiceRequestAutoCancelService, ServiceRequestAutoCancelService>();
        // Backup suppliers of a host's request (SP-BKP, flag SupplierRequestBackups for the creation of groups only): the choice of the backups
        // and the always-on pass over the groups that follows their windows.
        services.AddScoped<ServiceRequestBackupSelector>();
        services.AddScoped<IServiceRequestGroupRunService, ServiceRequestGroupRunService>();
        // The suppliers a host already asked for a job, for «Le mie app» of the marketplace (MKT, SR-09c): a view of its requests.
        services.AddScoped<IHostSupplierUsageReader, HostSupplierUsageReader>();

        // Booking from a supplier's public showcase (SP-10, flag SupplierShowcaseBooking): hold, e-mail check, request. The options
        // (Suppliers:Showcase, and Suppliers:CustomerIndexKey for the HMAC of the customers' e-mail) are validated at startup: with the
        // flag on, the version of the privacy notice and (outside Development and Testing) the key are required.
        services.AddOptions<Casazen.Core.Options.ShowcaseBookingOptions>()
            .BindConfiguration(Casazen.Core.Options.ShowcaseBookingOptions.SectionName)
            .ValidateOnStart();
        services.AddOptions<Casazen.Core.Options.ServiceCustomerIndexOptions>()
            .BindConfiguration(Casazen.Core.Options.ServiceCustomerIndexOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<Casazen.Core.Options.ShowcaseBookingOptionsValidator>();
        services.AddSingleton<IValidateOptions<Casazen.Core.Options.ShowcaseBookingOptions>>(
            provider => provider.GetRequiredService<Casazen.Core.Options.ShowcaseBookingOptionsValidator>());
        services.AddSingleton<IValidateOptions<Casazen.Core.Options.ServiceCustomerIndexOptions>>(
            provider => provider.GetRequiredService<Casazen.Core.Options.ShowcaseBookingOptionsValidator>());
        services.AddSingleton<IServiceCustomerIndex, ServiceCustomerIndex>();
        services.AddScoped<IShowcaseHoldReader, ShowcaseHoldReader>();
        services.AddScoped<IServiceCustomerReader, ServiceCustomerReader>();
        services.AddScoped<ShowcaseBookingNotifier>();
        services.AddScoped<IShowcaseBookingService, ShowcaseBookingService>();
        // The request on measure from the same showcase (SP-10b): no slot, the words of the customer, a thread with the supplier.
        services.AddScoped<IShowcaseInquiryService, ShowcaseInquiryService>();
        // The customer's own area of a booking (SP-11): find it with the code and the e-mail, cancel it, move it, answer a proposed time.
        services.AddScoped<IShowcaseBookingManager, ShowcaseBookingManager>();
        // The upkeep (expiry of holds and of unanswered showcase requests), the reminders of the day before and the retention of the
        // customers' data: their recurring jobs are registered whatever the flags say.
        services.AddScoped<IServiceRequestExpiryService, ServiceRequestExpiryService>();
        // The reminder of the day before to the supplier (SP-16d) is the second pass of the same job: the reminders service takes it in.
        services.AddScoped<SupplierDayBeforeReminders>();
        services.AddScoped<IServiceRequestReminderService, ServiceRequestReminderService>();
        services.AddScoped<IServiceCustomerPrivacyService, ServiceCustomerPrivacyService>();
        // The reads of the payments of a request for the supplier console: the summary of a job, the counters and the figures of the
        // home (SP-04e). The only reader of the payment table outside the payments service.
        services.AddScoped<ISupplierPaymentReadModels, SupplierPaymentReadModels>();
        // The supplier's earnings, the page "Incassi": its completed and paid jobs with gross, commission and net, the figures above them and
        // the CSV for its accountant (SP-16a).
        services.AddScoped<ISupplierEarningsService, SupplierEarningsService>();
        // Supplier dashboard KPIs from the service requests (SU-11, A4-15), the money of the console home (SP-04) and its overview (SP-04e).
        services.AddScoped<ISupplierKpiService, SupplierKpiService>();
        // The home ("Oggi") and the activation checklist of the supplier console (SP-04).
        services.AddScoped<ISupplierTodayService, SupplierTodayService>();
        // Supplier inbox, history and request detail: address, date and host contact after the take (SU-08, A4-14).
        services.AddScoped<ISupplierServiceRequestReader, SupplierServiceRequestReader>();
        // The host's list of interventions with the counters of its chips, and what a request says to its host: the stay, the contact of
        // the supplier and the action to do (SR-08a).
        services.AddScoped<IHostServiceRequestReader, HostServiceRequestReader>();
        // The supplier's customers, the page "Clienti": hosts and private customers of the showcase aggregated from the requests (SP-16a).
        services.AddScoped<ISupplierClientsService, SupplierClientsService>();
        // The ranking of the suppliers (SP-REL): who is recommended, the order of the list a host chooses from and, with the backups, who
        // is called. The weights are configuration (SupplierRanking), validated at startup, every one with a default; the events the
        // reliability is made of (answers in time, jobs without a dispute, missed deadlines, disputes) are recorded by the request flow.
        services.AddOptions<Casazen.Core.Options.SupplierRankingOptions>()
            .BindConfiguration(Casazen.Core.Options.SupplierRankingOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<Casazen.Core.Options.SupplierRankingOptions>, Casazen.Core.Options.SupplierRankingOptionsValidator>();
        services.AddScoped<ISupplierReliabilityService, SupplierReliabilityService>();
        services.AddScoped<ISupplierMatchService, SupplierMatchService>();
        // Internal activities performed by staff on properties (NF-22, agency tier).
        services.AddScoped<IInternalActivityService, InternalActivityService>();
        services.AddScoped<CalendarSyncService>();
        // The supplier's answers about the "Available" events of its calendar (they occupy until answered).
        services.AddScoped<SupplierFreeEventService>();
        // iCal import (PC-10, runbook ical.md): recurrence window, optional section ICalImport.
        services.AddOptions<ICalImportOptions>().BindConfiguration(ICalImportOptions.SectionName);
        services.AddScoped<ICalImportService>();
        services.AddScoped<ICalExportService>();
        services.AddScoped<PropertyICalSyncService>();
        services.AddScoped<ICheckoutHoldExpiryService, CheckoutHoldExpiryService>();
        services.AddScoped<ICheckoutPaymentReconciliationService, CheckoutPaymentReconciliationService>();
        // "Pay at the property" requests approved by the host (BK-06, D5, docs/runbooks/direct-booking.md).
        services.AddScoped<OnSiteRequestNotifier>();
        services.AddScoped<IOnSiteBookingRequestService, OnSiteBookingRequestService>();
        // Outcome page of the public checkout, read with the checkout token (BK-07, A3-15).
        services.AddScoped<ICheckoutOutcomeService, CheckoutOutcomeService>();
        // One instance per request for both: the session service reuses the proof and the view of "Le mie prenotazioni" (DB-06a).
        services.AddScoped<GuestBookingLookupService>();
        services.AddScoped<IGuestBookingLookupService>(sp => sp.GetRequiredService<GuestBookingLookupService>());
        // The guest's session with no account: code + email once, then a signed token (DB-06a, docs/runbooks/direct-booking.md § 8.4).
        // The tokens are sealed with Data Protection (the key ring in the database), so every replica reads them.
        services.AddSingleton<GuestSessionTokens>();
        services.AddScoped<IGuestSessionService, GuestSessionService>();
        // Verified reviews of the stays (DB-12a, docs/runbooks/site-reviews.md): the public reads and the guest's review, the host's
        // answers, reports and invitations, and the staff's decisions. The signed link is made by Data Protection, nothing is stored for it.
        services.AddSingleton<ReviewLinkTokens>();
        services.AddScoped<IPublicReviewService, PublicReviewService>();
        services.AddScoped<IHostReviewService, HostReviewService>();
        services.AddScoped<IAdminReviewService, AdminReviewService>();
        // Messages between the guest of a direct booking and the host (DB-14a): the thread, its two sides and the two emails
        // that are the consequence of a message (docs/runbooks/guest-messages.md).
        services.AddScoped<IGuestMessageService, GuestMessageService>();
        // «Scrivi a {host}» of the booking site: the visitor's message as one email to the host, answered from their mailbox (DB-03b).
        services.AddScoped<IHostContactService, HostContactService>();
        services.AddScoped<NotificationRouter>();
        services.AddScoped<INotificationChannel, EmailNotificationChannel>();
        services.AddScoped<INotificationChannel, DashboardNotificationChannel>();

        // User-chosen URLs (iCal feeds) are downloaded only through the anti-SSRF client (FD-16).
        services.AddOptions<SafeExternalHttpOptions>().BindConfiguration(SafeExternalHttpOptions.SectionName);
        services.AddSingleton<IExternalHostResolver, SystemDnsHostResolver>();
        services.AddSingleton<ISafeExternalHttpClient, SafeExternalHttpClient>();

        // ConcordatoFacile status API client with Polly resilience (ADR-008-R1 through ADR-008-R5).
        services.AddHttpClient<ConcordatoFacileApiClient>((serviceProvider, client) =>
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();

            var baseUrl = configuration["ConcordatoFacile:StatusApiUrl"] ?? "https://stato.concordatofacile.it";
            client.BaseAddress = new Uri(baseUrl);

            // Without the key no header is set and ConcordatoFacileApiClient.IsConfigured is false: the status service does not ask
            // ConcordatoFacile and says once (CF-BE) that the key is missing. This delegate runs at every creation of the client, so it
            // must not log: it used to warn at every request that reached the controller, checkout and access included.
            // A person pastes the key in Railway: a blank or a line break at its edge is not part of it and would make every read fail.
            var apiKey = configuration["ConcordatoFacile:StatusApiKey"]?.Trim();
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }

            client.Timeout = TimeSpan.FromSeconds(120); // Total timeout for all retries
        })
        .AddPolicyHandler((serviceProvider, request) =>
        {
            var logger = serviceProvider.GetRequiredService<ILogger<ConcordatoFacileApiClient>>();
            return GetCfRetryPolicy(logger);
        })
        .AddPolicyHandler(Policy.TimeoutAsync<HttpResponseMessage>(30))
        .ConfigurePrimaryHttpMessageHandler(() =>
        {
            var handler = new SocketsHttpHandler
            {
                // TLS 1.2+ enforced (ADR-008-R14)
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
                }
            };
            return handler;
        });

        return services;
    }

    /// <summary>
    /// Retry policy for ConcordatoFacile API: exponential backoff (1s, 2s, 4s) for transient errors
    /// (ADR-008-R3). Handles HTTP 5xx, HttpRequestException, TaskCanceledException, TimeoutException.
    /// </summary>
    private static IAsyncPolicy<HttpResponseMessage> GetCfRetryPolicy(ILogger logger)
    {
        return Policy
            .Handle<HttpRequestException>()
            .Or<TaskCanceledException>()
            .Or<TimeoutException>()
            .OrResult<HttpResponseMessage>(r => (int)r.StatusCode >= 500)
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt - 1)),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    if (outcome.Exception != null)
                    {
                        logger.LogWarning(outcome.Exception,
                            "CF API request failed (attempt {RetryCount}/3); retrying in {Delay}s",
                            retryCount, timespan.TotalSeconds);
                    }
                    else
                    {
                        logger.LogWarning(
                            "CF API returned {StatusCode} (attempt {RetryCount}/3); retrying in {Delay}s",
                            (int)outcome.Result.StatusCode, retryCount, timespan.TotalSeconds);
                    }
                });
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
