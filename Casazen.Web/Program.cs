using System.Reflection;
using System.Text.Json.Serialization;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.OTA;
using Casazen.Infrastructure.OTA.Resilience;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Configuration;
using Casazen.Web.Extensions;
using Casazen.Web.HostedServices;
using Casazen.Web.Infrastructure;
using Casazen.Web.Middleware;
using Hangfire;
using Hangfire.PostgreSql;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

using Stripe;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDataProtection()
    .SetApplicationName("Casazen");

// Database
builder.Services.AddCasazenDatabase(builder.Configuration);
var connectionString = NpgsqlConnectionStringNormalizer.Normalize(
    builder.Configuration.GetConnectionString("DefaultConnection"));

// Hangfire Configuration (skipped when no connection string, e.g. in CI/test)
if (!string.IsNullOrEmpty(connectionString))
{
    builder.Services.AddHangfire(configuration => configuration
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(
            options => options.UseNpgsqlConnection(connectionString),
            new PostgreSqlStorageOptions
            {
                SchemaName = "hangfire",
            }));

    builder.Services.AddHangfireServer();
}

// Repositories
builder.Services.AddCasazenRepositories();
builder.Services.AddScoped<IGuestRepository, GuestRepository>();
builder.Services.AddScoped<IPropertyRepository, PropertyRepository>();
builder.Services.AddScoped<IPaymentRepository, PaymentRepository>();
builder.Services.AddScoped<IBookingRepository, BookingRepository>();
builder.Services.AddScoped<ITouristTaxRateRepository, TouristTaxRateRepository>();
builder.Services.AddScoped<IPricingAdapterConfigRepository, PricingAdapterConfigRepository>();
builder.Services.AddScoped<IPricingHistoryRepository, PricingHistoryRepository>();
// Lease repositories
builder.Services.AddScoped<ILeaseContractRepository, LeaseContractRepository>();
builder.Services.AddScoped<ILeaseRegistrationRepository, LeaseRegistrationRepository>();
builder.Services.AddScoped<ILeaseEventRepository, LeaseEventRepository>();

// External Services
builder.Services.AddHttpClient<PublicHolidayService>();
builder.Services.AddMemoryCache();

// Stripe configuration — set API key globally for all Stripe services
var stripeSecretKey = builder.Configuration["Stripe:SecretKey"];
if (!string.IsNullOrEmpty(stripeSecretKey))
{
    StripeConfiguration.ApiKey = stripeSecretKey;
}

// Services
builder.Services.AddCasazenServices();
builder.Services.AddScoped<IGuestService, GuestService>();
builder.Services.AddScoped<IPropertyService, PropertyService>();
builder.Services.AddScoped<IBookingService, BookingService>();
builder.Services.AddScoped<ITouristTaxService, TouristTaxService>();
builder.Services.AddScoped<IOtaManager, OtaManager>();
builder.Services.AddScoped<IEmailService, ResendEmailService>();
builder.Services.AddScoped<IImageStorageService, LocalImageStorageService>();
builder.Services.AddScoped<IGuestDocumentStorage, LocalGuestDocumentStorageService>();
builder.Services.AddScoped<IStripeService, StripeService>();
builder.Services.AddScoped<StripeWebhookHandler>();
builder.Services.AddScoped<IStripeConnectGateway, StripeConnectGateway>();
builder.Services.AddScoped<IConnectOnboardingService, ConnectOnboardingService>();
builder.Services.AddScoped<IAuth0ManagementService, Auth0ManagementService>();
builder.Services.AddScoped<IAdminService, AdminService>();
builder.Services.AddScoped<ITaxCalculationService, TaxCalculationService>();
builder.Services.AddScoped<IGdprService, GdprService>();
builder.Services.AddScoped<IAlloggiatiWebService, AlloggiatiWebService>();
builder.Services.AddScoped<IPublicHolidayService, PublicHolidayService>();
builder.Services.AddScoped<IPricingAdapterService, PricingAdapterService>();
builder.Services.AddScoped<IAiProvider, StubAiProvider>();
// Lease services
builder.Services.AddScoped<ILeaseWorkflowService, LeaseWorkflowService>();
builder.Services.AddScoped<ILeaseTemplateService, LeaseContractTemplateService>();
builder.Services.AddScoped<ILeaseESignService, LeaseESignHttpAdapter>();
builder.Services.AddScoped<ILeaseRegistrationService, OpenapiLeaseRegistrationProvider>();
builder.Services.AddHttpClient("Openapi");

// OTA Integrations with resilience patterns
builder.Services.AddCasazenOtaIntegrations(builder.Configuration);

// Authentication & Authorization
builder.Services.AddCasazenAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddCasazenAuthorization();

// CORS
builder.Services.AddCasazenCors(builder.Configuration);

builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("PublicBookingCreate", limiter =>
    {
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.PermitLimit = builder.Configuration.GetValue("DirectBooking:RateLimitPermitLimit", 10);
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("GuestCheckIn", limiter =>
    {
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.PermitLimit = builder.Configuration.GetValue("CheckIn:RateLimitPermitLimit", 10);
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("PublicTouristTaxCalc", limiter =>
    {
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.PermitLimit = builder.Configuration.GetValue("SeoTouristTax:RateLimitPermitLimit", 30);
        limiter.QueueLimit = 0;
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// Background Jobs
builder.Services.AddScoped<OtaSyncJob>();
builder.Services.AddScoped<BookingPullJob>();
builder.Services.AddScoped<DynamicPricingJob>();
builder.Services.AddScoped<EmailQueueProcessor>();
builder.Services.AddScoped<StripeWebhookJob>();
builder.Services.AddScoped<AlloggiatiWebReportJob>();
builder.Services.AddScoped<AlloggiatiDeadlineAlertJob>();
builder.Services.AddScoped<CinDeadlineAlertJob>();
builder.Services.AddScoped<GdprDataRetentionJob>();
// Lease background jobs
builder.Services.AddScoped<ESignWebhookJob>();
builder.Services.AddScoped<LeaseSignStatusPollingJob>();
builder.Services.AddScoped<LeaseRegistrationStatusPollingJob>();
builder.Services.AddScoped<SeoPageGenerationJob>();
builder.Services.AddScoped<SeoContentRefreshJob>();
builder.Services.Configure<SeoBootstrapOptions>(
    builder.Configuration.GetSection(SeoBootstrapOptions.SectionName));
builder.Services.Configure<Casazen.Core.Options.PublicHostOptions>(
    builder.Configuration.GetSection(Casazen.Core.Options.PublicHostOptions.SectionName));
builder.Services.AddHostedService<SeoBootstrapHostedService>();

// API
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    })
    .ConfigureApiBehaviorOptions(options =>
    {
        // Override default 400 response with RFC 7807 Problem Details for model validation errors
        options.InvalidModelStateResponseFactory = context =>
        {
            var problemDetails = new ValidationProblemDetails(context.ModelState)
            {
                Type = "https://tools.ietf.org/html/rfc7231#section-6.5.1",
                Title = "One or more validation errors occurred.",
                Status = StatusCodes.Status400BadRequest,
                Instance = $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}",
            };
            problemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;

            return new BadRequestObjectResult(problemDetails)
            {
                ContentTypes = { "application/problem+json" },
            };
        };
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "CasaZen API",
        Version = "v1",
        Description = "Vacation rental property management system for Italian market"
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Enter 'Bearer' [space] and then your token in the text input below.",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                },
                Scheme = "oauth2",
                Name = "Bearer",
                In = ParameterLocation.Header
            },
            new List<string>()
        }
    });

    options.EnableAnnotations();

    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    options.IncludeXmlComments(xmlPath);
});

var app = builder.Build();

// Apply pending EF migrations on startup (Railway deploy). Skipped in Testing (in-memory DB).
if (!string.IsNullOrEmpty(connectionString) && !app.Environment.IsEnvironment("Testing"))
{
    using var migrateScope = app.Services.CreateScope();
    var db = migrateScope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

// Swagger (must be before Authentication to allow anonymous access to swagger.json)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();

    // Log Swagger URLs
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("====================================================");
    logger.LogInformation("🔵 Swagger UI available at:");
    logger.LogInformation("   → http://localhost:5000/swagger");
    logger.LogInformation("   → https://localhost:5001/swagger");
    logger.LogInformation("====================================================");
}

// Static files (for serving uploaded images)
app.UseStaticFiles();

// Security headers — early in pipeline
app.UseSecurityHeaders();

// CORS (must be before Authentication)
app.UseCors("AllowFrontend");

// Global error handling — must be early in pipeline to catch all exceptions
app.UseErrorHandling();

// Authentication & Authorization (must be in this order)
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Hangfire Dashboard and recurring jobs (only when Hangfire is configured)
if (!string.IsNullOrEmpty(connectionString))
{
    var hangfireDashboardEnabled = builder.Configuration.GetValue(
        "Hangfire:DashboardEnabled",
        app.Environment.IsDevelopment());

    if (hangfireDashboardEnabled)
    {
        app.UseHangfireDashboard("/hangfire", new DashboardOptions
        {
            Authorization = new[] { new HangfireAuthorizationFilter(app.Configuration) }
        });
    }

    app.Lifetime.ApplicationStarted.Register(() =>
    {
        using var scope = app.Services.CreateScope();
        var recurringJobManager = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
        ConfigureRecurringJobs(recurringJobManager);
    });
}

app.MapControllers();

// Log application URLs on startup
app.Lifetime.ApplicationStarted.Register(() =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("====================================================");
    logger.LogInformation("✅ CasaZen Backend Started Successfully!");
    logger.LogInformation("====================================================");
    logger.LogInformation("📡 API Endpoints:");
    logger.LogInformation("   → http://localhost:5000/api/");
    logger.LogInformation("   → https://localhost:5001/api/");
    logger.LogInformation("");
    if (app.Environment.IsDevelopment())
    {
        logger.LogInformation("📖 Swagger Documentation:");
        logger.LogInformation("   → http://localhost:5000/swagger");
        logger.LogInformation("   → https://localhost:5001/swagger");
        logger.LogInformation("");
    }
    var hangfireDashboardEnabled = app.Configuration.GetValue(
        "Hangfire:DashboardEnabled",
        app.Environment.IsDevelopment());
    if (!string.IsNullOrEmpty(connectionString) && hangfireDashboardEnabled)
    {
        logger.LogInformation("📊 Hangfire Dashboard:");
        logger.LogInformation("   → http://localhost:5000/hangfire");
        logger.LogInformation("   → https://localhost:5001/hangfire");
    }
    logger.LogInformation("====================================================");
});

app.Run();

void ConfigureRecurringJobs(IRecurringJobManager recurringJobManager)
{
    recurringJobManager.AddOrUpdate<OtaSyncJob>(
        "ota-sync-all",
        job => job.ExecuteAsync(Guid.Empty),
        Cron.Hourly,
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    recurringJobManager.AddOrUpdate<BookingPullJob>(
        "booking-pull-all",
        job => job.ExecuteAsync(Guid.Empty),
        "*/15 * * * *",
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    recurringJobManager.AddOrUpdate<DynamicPricingJob>(
        "dynamic-pricing-adaptation",
        job => job.ExecuteAsync(),
        "0 2 * * *",
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    recurringJobManager.AddOrUpdate<GdprDataRetentionJob>(
        "gdpr-data-retention",
        job => job.ExecuteAsync(),
        "0 3 * * *",
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    recurringJobManager.AddOrUpdate<AlloggiatiDeadlineAlertJob>(
        "alloggiati-deadline-alert",
        job => job.ExecuteAsync(),
        Cron.Hourly,
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    recurringJobManager.AddOrUpdate<CinDeadlineAlertJob>(
        "cin-deadline-alert",
        job => job.ExecuteAsync(),
        "0 8 * * *",
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    recurringJobManager.AddOrUpdate<LeaseSignStatusPollingJob>(
        "lease-sign-status-poll",
        job => job.ExecuteAsync(),
        "*/10 * * * *",
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    recurringJobManager.AddOrUpdate<LeaseRegistrationStatusPollingJob>(
        "lease-registration-status-poll",
        job => job.ExecuteAsync(),
        "*/5 * * * *",
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    recurringJobManager.AddOrUpdate<SeoContentRefreshJob>(
        "seo-content-refresh",
        job => job.ExecuteAsync(),
        "0 4 1 * *",
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    recurringJobManager.AddOrUpdate<DirectBookingChargeJob>(
        "direct-booking-charge",
        job => job.ExecuteAsync(),
        "0 6 * * *",
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
}

public partial class Program { }
