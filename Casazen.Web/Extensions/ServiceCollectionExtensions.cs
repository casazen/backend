// File: Casazen.Web/Extensions/ServiceCollectionExtensions.cs

using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.OTA;
using Casazen.Infrastructure.OTA.Resilience;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Web.Middleware;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Polly;

namespace Casazen.Web.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlServer(
                configuration.GetConnectionString("DefaultConnection"),
                x => x.MigrationsAssembly("Casazen.Infrastructure")
            )
        );
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
                            var rolesClaim = context.Principal.FindFirst("https://casazen.app/roles");
                            if (rolesClaim != null)
                            {
                                var roles = JsonSerializer.Deserialize<string[]>(rolesClaim.Value);
                                if (roles != null)
                                {
                                    foreach (var role in roles)
                                    {
                                        identity.AddClaim(new Claim(
                                            "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
                                            role));
                                    }
                                }
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
        services.AddAuthorizationBuilder()
            .AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"))
            .AddPolicy("PropertyOwner", policy => policy.RequireAuthenticatedUser())
            .AddPolicy("LongTermLandlord", policy => policy.RequireRole("LongTermLandlord"));

        return services;
    }

    public static IServiceCollection AddCasazenCors(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddCors(options =>
        {
            options.AddPolicy("AllowFrontend", policy =>
            {
                policy
                    .WithOrigins("http://localhost:3000", "http://localhost:5173", "http://localhost:5174", "http://localhost:5175", "https://casazen.app")
                    .AllowAnyMethod()
                    .AllowAnyHeader()
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
        return services;
    }

    public static IServiceCollection AddCasazenExternalServices(this IServiceCollection services)
    {
        // Note: Auth0Service was removed as dead code (never used)
        // JWT authentication is handled directly by AddCasazenAuthentication()
        services.AddScoped<SendGridService>();
        services.AddScoped<StripeService>();
        services.AddSingleton<StripeWebhookHandler>();
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
