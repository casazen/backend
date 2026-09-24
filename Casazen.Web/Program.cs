using System.Reflection;
using System.Text.Json.Serialization;
using Casazen.Core.Features;
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
using Casazen.Web.Resources;
using Hangfire;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.OpenApi.Models;

using Stripe;

var builder = WebApplication.CreateBuilder(args);

// Data Protection keys persisted in the database (not the ephemeral container disk): FD-07 / A9-04.
builder.Services.AddCasazenDataProtection(builder.Configuration);

// Database
builder.Services.AddCasazenDatabase(builder.Configuration);
var connectionString = NpgsqlConnectionStringNormalizer.Normalize(
    builder.Configuration.GetConnectionString("DefaultConnection"));
// Outside Development/Testing a missing connection string stops the startup instead of running in memory (FD-12).
RequiredConfiguration.EnsureDatabaseConnection(connectionString, builder.Environment);

// Hangfire (skipped when no connection string, e.g. in CI/test), in a schema dedicated to this environment:
// test and production share the database, never the queue (FD-11, docs/runbooks/hangfire.md).
var hangfireStorage = builder.Services.AddCasazenHangfire(builder.Configuration, builder.Environment, connectionString);

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
builder.Services.AddScoped<ILeaseEventRepository, LeaseEventRepository>();

// External Services
builder.Services.AddHttpClient<PublicHolidayService>();
builder.Services.AddMemoryCache();

// Dates: UTC normalization of JSON/query/route DateTime values + clock for "today" in Europe/Rome (FD-06)
builder.Services.AddCasazenUtcDateTimeHandling();

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
builder.Services.AddCasazenEmail(builder.Configuration, builder.Environment);
// Object storage (Supabase Storage via S3; filesystem only in Development/Testing): FD-07.
builder.Services.AddCasazenFileStorage(builder.Configuration);
builder.Services.AddScoped<IStripeService, StripeService>();
builder.Services.AddScoped<StripeWebhookHandler>();
builder.Services.AddScoped<IStripeConnectGateway, StripeConnectGateway>();
builder.Services.AddScoped<IConnectOnboardingService, ConnectOnboardingService>();
builder.Services.AddCasazenAuth0Management();
builder.Services.AddScoped<IAdminService, AdminService>();
builder.Services.AddScoped<ITouristTaxQuoteService, TouristTaxQuoteService>();
builder.Services.AddScoped<IGdprService, GdprService>();
builder.Services.AddScoped<IAlloggiatiWebService, AlloggiatiWebService>();
builder.Services.AddScoped<IPublicHolidayService, PublicHolidayService>();
builder.Services.AddScoped<IPricingAdapterService, PricingAdapterService>();
builder.Services.AddCasazenAiProvider(builder.Configuration);
// Lease services
builder.Services.AddScoped<ILeaseWorkflowService, LeaseWorkflowService>();
// Contract templates: final PDF only from a complete, lawyer-approved template (LT-03, A7-03)
builder.Services.AddCasazenLeaseContractTemplates(builder.Configuration);
builder.Services.AddScoped<ILeaseESignService, LeaseESignHttpAdapter>();

// OTA partner adapters with resilience patterns: registered only with Features:OtaPartnerApi on (D10, FD-20)
builder.Services.AddCasazenOtaIntegrations(builder.Configuration);

// Localization — Italian (default) and English.
// No ResourcesPath: resource names follow the marker type's namespace, so IStringLocalizer<SharedResources>
// (Casazen.Web.Resources.SharedResources) reads Resources/SharedResources.resx and SharedResources.en.resx.
// With ResourcesPath = "Resources" it looked for Casazen.Web.Resources.Resources.SharedResources and every
// lookup returned its raw key.
builder.Services.AddLocalization();

builder.Services.AddRequestLocalization(options =>
{
    // Neutral cultures too, so "Accept-Language: en" (or en-GB) resolves to English instead of the default.
    var supportedCultures = new[] { "it-IT", "it", "en-US", "en" };
    options.SetDefaultCulture(supportedCultures[0])
           .AddSupportedCultures(supportedCultures)
           .AddSupportedUICultures(supportedCultures);
    options.ApplyCurrentCultureToResponseHeaders = true;
});

// Single error contract: every ProblemDetails (framework-generated too) gets code, traceId and localized texts.
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
        ApiProblemDetails.Complete(context.ProblemDetails, context.HttpContext));

// Authentication & Authorization
builder.Services.AddCasazenAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddCasazenAuthorization();

// Health checks: /api/health/live, /api/health/ready (database, Hangfire, configuration), /api/health (FD-12)
builder.Services.AddCasazenHealthChecks();

// CORS: configured origins only, no credentials (FD-17, docs/runbooks/cors-security-headers.md)
builder.Services.AddCasazenCors();

// Client IP behind the Railway proxy (UseForwardedHeaders, first middleware) and per-IP rate limiting (FD-10, #273).
builder.Services.AddCasazenForwardedHeaders();
builder.Services.AddCasazenRateLimiting();

// Background Jobs
builder.Services.AddScoped<OtaSyncJob>();
builder.Services.AddScoped<BookingPullJob>();
builder.Services.AddScoped<DynamicPricingJob>();
builder.Services.AddScoped<StripeWebhookJob>();
builder.Services.AddScoped<AlloggiatiWebReportJob>();
builder.Services.AddScoped<AlloggiatiDeadlineAlertJob>();
builder.Services.AddScoped<CinDeadlineAlertJob>();
builder.Services.AddScoped<GdprDataRetentionJob>();
// Lease background jobs
builder.Services.AddScoped<ESignWebhookJob>();
builder.Services.AddScoped<LeaseSignStatusPollingJob>();
builder.Services.AddScoped<LeaseRegistrationStatusPollingJob>();
builder.Services.AddScoped<RliDeadlineReminderJob>();
builder.Services.AddScoped<SeoPageGenerationJob>();
builder.Services.AddScoped<SeoContentRefreshJob>();
builder.Services.AddScoped<GuestCheckInSendJob>();
builder.Services.AddScoped<GuestCheckInReminderJob>();
builder.Services.AddScoped<CheckoutReminderJob>();
builder.Services.AddScoped<CheckoutHoldExpiryJob>();
builder.Services.AddScoped<ICheckoutReminderScheduler, CheckoutReminderScheduler>();
builder.Services.AddScoped<IAlloggiatiReportScheduler, AlloggiatiReportScheduler>();
builder.Services.Configure<SeoBootstrapOptions>(
    builder.Configuration.GetSection(SeoBootstrapOptions.SectionName));
builder.Services.Configure<Casazen.Core.Options.PublicHostOptions>(
    builder.Configuration.GetSection(Casazen.Core.Options.PublicHostOptions.SectionName));
builder.Services.Configure<Casazen.Core.Options.ComplianceOptions>(
    builder.Configuration.GetSection(Casazen.Core.Options.ComplianceOptions.SectionName));
builder.Services.Configure<Casazen.Core.Options.RliOptions>(
    builder.Configuration.GetSection(Casazen.Core.Options.RliOptions.SectionName));
builder.Services.Configure<Casazen.Core.Options.CedolareAdvisoryOptions>(
    builder.Configuration.GetSection(Casazen.Core.Options.CedolareAdvisoryOptions.SectionName));
builder.Services.AddHostedService<SeoBootstrapHostedService>();

// API
builder.Services.AddControllers(options => options.Filters.Add<ProblemDetailsResultFilter>())
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    })
    // Validation attributes may use a SharedResources key as ErrorMessage (a literal message is kept as is).
    .AddDataAnnotationsLocalization(options =>
        options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResources)))
    .ConfigureApiBehaviorOptions(options =>
    {
        // Model binding/validation errors: 400 ValidationProblemDetails (code "validation_error", field errors).
        options.InvalidModelStateResponseFactory = context =>
        {
            var problemDetailsFactory = context.HttpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>();
            var problemDetails = problemDetailsFactory.CreateValidationProblemDetails(
                context.HttpContext,
                context.ModelState,
                StatusCodes.Status400BadRequest);

            return new BadRequestObjectResult(problemDetails)
            {
                ContentTypes = { ApiProblemDetails.ContentType },
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

// First middleware: resolves the client IP and scheme from the trusted proxy's X-Forwarded-For / X-Forwarded-Proto.
// Everything after it (rate limiting, consent evidence) reads HttpContext.Connection.RemoteIpAddress, never the header.
app.UseForwardedHeaders();

// Apply pending EF migrations on startup (Railway deploy). Skipped in Testing (in-memory DB).
if (!string.IsNullOrEmpty(connectionString) && !app.Environment.IsEnvironment("Testing"))
{
    using var migrateScope = app.Services.CreateScope();
    var db = migrateScope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

// One-off command: `dotnet Casazen.Web.dll storage:migrate-legacy [--dry-run]` (docs/runbooks/storage.md).
if (StorageExtensions.IsLegacyFileMigrationCommand(args))
{
    Environment.ExitCode = await app.RunLegacyFileMigrationAsync(args);
    return;
}

app.LogDataProtectionKeyProtection();

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

// Security headers on every response, static files included (FD-17): before anything that can short-circuit.
app.UseSecurityHeaders();

// Static files (wwwroot test feeds; never the legacy /uploads folder). Uploads live in object storage.
app.UseCasazenStaticFiles();

// CORS (must be before Authentication)
app.UseCors(CasazenCorsPolicyProvider.PolicyName);

// Localization middleware — reads Accept-Language header, sets culture for downstream components
app.UseRequestLocalization();

// Global error handling — must be early in pipeline to catch all exceptions
app.UseErrorHandling();

// Endpoints behind a disabled feature flag answer 404 before authentication, like a missing route (FD-20)
app.UseFeatureGates();

// Authentication & Authorization (must be in this order)
app.UseAuthentication();
// Loads the caller's OrgId asynchronously once per request, before policies and the EF tenant filter read it (A1-20).
app.UseTenantResolution();
app.UseAuthorization();
app.UseRateLimiter();

// Hangfire Dashboard and recurring jobs (only when Hangfire is configured)
if (hangfireStorage is not null)
{
    var hangfireDashboardEnabled = builder.Configuration.GetValue(
        "Hangfire:DashboardEnabled",
        app.Environment.IsDevelopment());

    if (hangfireDashboardEnabled)
    {
        app.UseHangfireDashboard("/hangfire", new DashboardOptions
        {
            DashboardTitle = $"CasaZen jobs ({hangfireStorage.Schema})",
            Authorization = new[] { new HangfireAuthorizationFilter(app.Configuration) }
        });
    }

    app.Lifetime.ApplicationStarted.Register(() =>
    {
        app.Services.GetRequiredService<ILogger<Program>>()
            .LogInformation("Hangfire storage schema: {HangfireSchema}", hangfireStorage.Schema);
        using var scope = app.Services.CreateScope();
        var recurringJobManager = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
        RecurringJobsRegistration.Configure(recurringJobManager, scope.ServiceProvider.GetRequiredService<IFeatureFlags>());
    });
}

app.MapControllers();
app.MapCasazenHealthChecks();

// Log application URLs on startup
app.Lifetime.ApplicationStarted.Register(() =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("====================================================");
    logger.LogInformation("✅ CasaZen Backend Started Successfully!");
    logger.LogInformation("Commit: {CommitSha}", app.Services.GetRequiredService<BuildInfo>().CommitSha ?? "unknown");
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
    if (hangfireStorage is not null && hangfireDashboardEnabled)
    {
        logger.LogInformation("📊 Hangfire Dashboard:");
        logger.LogInformation("   → http://localhost:5000/hangfire");
        logger.LogInformation("   → https://localhost:5001/hangfire");
    }
    logger.LogInformation("====================================================");
});

app.Run();

public partial class Program { }
