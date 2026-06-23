// File: Casazen.Web/Extensions/ServiceCollectionExtensions.cs

using System.Security.Claims;
using Casazen.Core.Multitenancy;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.OTA;
using Casazen.Infrastructure.OTA.Resilience;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Casazen.Web.Middleware;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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
                    OnTokenValidated = context =>
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
                        }
                        return Task.CompletedTask;
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
        services.AddScoped<IContextAuthorizationService, ContextAuthorizationService>();
        services.AddScoped<IAuthorizationHandler, ContextAuthorizationHandler>();

        var builder = services.AddAuthorizationBuilder()
            .AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"))
            .AddPolicy("PropertyOwner", policy => policy.RequireAuthenticatedUser())
            .AddPolicy("PropertyManagerOrAdmin", policy => policy.RequireRole("PropertyManager", "Admin"))
            .AddPolicy("LongTermLandlord", policy => policy.RequireRole("LongTermLandlord"))
            .AddPolicy("RequireSupplier", policy => policy.RequireRole("Supplier"))
            .AddPolicy("RequireOrgBillingAdmin", policy =>
                policy.Requirements.Add(new OrgBillingAdminRequirement()));

        services.AddScoped<IAuthorizationHandler, OrgBillingAdminAuthorizationHandler>();

        RegisterContextPolicies(builder);

        return services;
    }

    private static void RegisterContextPolicies(AuthorizationBuilder builder)
    {
        var contextPermissions = new Dictionary<string, string[]>
        {
            ["short-rent"] =
            [
                "property.read", "property.write",
                "booking.read", "booking.write",
                "payment.read", "payment.write",
                "ota.read", "ota.write",
                "guest.read", "guest.write",
            ],
            ["long-rent"] =
            [
                "lease.read", "lease.create", "lease.sign", "lease.register",
                "rent.read", "rent.manage",
            ],
            ["admin"] =
            [
                "admin.stats.read", "admin.users.read", "admin.users.manage",
                "admin.cin.read", "admin.jobs.read", "admin.tax.manage", "admin.seo.read",
            ],
        };

        foreach (var pair in contextPermissions)
        {
            foreach (var permission in pair.Value)
            {
                var policyName = $"RequireContext:{pair.Key}:{permission}";
                builder.AddPolicy(policyName, policy =>
                    policy.Requirements.Add(new ContextPermissionRequirement(pair.Key, permission)));
            }
        }
    }

    public static IServiceCollection AddCasazenCors(this IServiceCollection services, IConfiguration configuration)
    {
        var allowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "http://localhost:3000",
            "http://localhost:5173",
            "http://localhost:5174",
            "http://localhost:5175",
            "https://casazen.app",
            "https://casazen-app.vercel.app",
        };

        var configOrigins = configuration["Cors:AllowedOrigins"];
        if (!string.IsNullOrWhiteSpace(configOrigins))
        {
            foreach (var origin in configOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                allowedOrigins.Add(origin);
            }
        }

        services.AddCors(options =>
        {
            options.AddPolicy("AllowFrontend", policy =>
            {
                policy
                    .SetIsOriginAllowed(origin =>
                    {
                        if (allowedOrigins.Contains(origin))
                        {
                            return true;
                        }

                        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                        {
                            return false;
                        }

                        return uri.Host.EndsWith(".vercel.app", StringComparison.OrdinalIgnoreCase);
                    })
                    .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
                    .WithHeaders(
                        "Authorization",
                        "Content-Type",
                        "Accept",
                        "X-Requested-With",
                        "X-Hangfire-ApiKey")
                    .AllowCredentials();
            });
        });
        return services;
    }

    public static IServiceCollection AddCasazenRepositories(this IServiceCollection services)
    {
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IPropertyRepository, PropertyRepository>();
        services.AddScoped<IBookingRepository, BookingRepository>();
        services.AddScoped<IPaymentRepository, PaymentRepository>();
        services.AddScoped<ITouristTaxRateRepository, TouristTaxRateRepository>();
        services.AddScoped<ISeoContentRepository, SeoContentRepository>();
        services.AddScoped<IOtaSyncLogRepository, OtaSyncLogRepository>();
        services.AddScoped<IAlloggiatiWebReportRepository, AlloggiatiWebReportRepository>();
        services.AddScoped<ITaxRateRepository, TaxRateRepository>();
        services.AddScoped<IOtaIntegrationRepository, OtaIntegrationRepository>();
        services.AddScoped<IPropertyDocumentRepository, PropertyDocumentRepository>();
        return services;
    }

    public static IServiceCollection AddCasazenServices(this IServiceCollection services)
    {
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IPropertyService, PropertyService>();
        services.AddScoped<IBookingService, BookingService>();
        services.AddScoped<IOtaManager, OtaManager>();
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<ITouristTaxService, TouristTaxService>();
        services.AddScoped<IGdprService, GdprService>();
        services.AddScoped<IOtaIntegrationService, OtaIntegrationService>();
        services.AddScoped<IPropertyDocumentService, PropertyDocumentService>();
        services.AddScoped<IImageStorageService, LocalImageStorageService>();
        services.AddScoped<IPropertyAuthorizationService, PropertyAuthorizationService>();
        services.AddScoped<IAdminAccessAuditService, AdminAccessAuditService>();

        // Multi-tenant Org boundary (US-004): tenant resolution + org/entitlement reads.
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<IOrgContextResolver, OrgContextResolver>();
        services.AddScoped<ISupplierOrgContextResolver, SupplierOrgContextResolver>();
        services.AddScoped<IOrgService, OrgService>();
        services.AddScoped<IPublicHostResolver, PublicHostResolver>();
        services.AddScoped<IEntitlementService, EntitlementService>();
        services.AddScoped<IStripeBillingService, StripeBillingService>();
        services.AddScoped<IVatCalculationService, VatCalculationService>();
        services.AddScoped<IViesService, ViesService>();
        services.AddScoped<ISdiEInvoiceService, SdiEInvoiceService>();
        services.AddScoped<IBillingEntryGate, BillingEntryGate>();
        services.AddScoped<IOssRevenueTracker, OssRevenueTracker>();
        services.AddScoped<IRentBillingService, NullRentBillingService>();
        services.AddScoped<ISeoContentService, SeoContentService>();
        services.AddScoped<IGuestAccessService, GuestAccessService>();
        services.AddSingleton<ILegalDocumentService, LegalDocumentService>();
        services.AddScoped<IOnboardingService, OnboardingService>();
        services.AddScoped<ISupplierService, Casazen.Infrastructure.Services.SupplierService>();
        return services;
    }

    public static IServiceCollection AddCasazenExternalServices(this IServiceCollection services)
    {
        // Note: Auth0Service was removed as dead code (never used)
        // JWT authentication is handled directly by AddCasazenAuthentication()
        services.AddScoped<HttpEmailService>();
        services.AddScoped<StripeService>();
        services.AddScoped<IAiProvider, StubAiProvider>();
        services.AddScoped<IStripeConnectGateway, StripeConnectGateway>();
        services.AddScoped<IConnectOnboardingService, ConnectOnboardingService>();
        services.AddScoped<StripeWebhookHandler>();
        return services;
    }

    public static IServiceCollection AddCasazenOtaIntegrations(this IServiceCollection services, IConfiguration configuration)
    {
        // Register rate limiter as singleton (shared across all OTA adapters)
        services.AddSingleton<OtaRateLimiter>();

        services.AddScoped<IChannelFactory, ChannelFactory>();

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
