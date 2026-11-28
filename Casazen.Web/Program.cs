using System.Reflection;
using System.Text.Json.Serialization;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Configuration;
using Casazen.Web.Extensions;
using Casazen.Web.HostedServices;
using Casazen.Web.Infrastructure;
using Casazen.Web.Middleware;
using Casazen.Web.Resources;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.OpenApi.Models;

using Stripe;

if (args is ["--ensure-agency-test-meter", .. var modules])
{
    foreach (var module in modules.Length == 0 ? [AgencyUsagePricing.Base] : modules)
        await AgencyTestMeter.EnsureAsync(module);
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCasazenDataProtection(builder.Configuration, builder.Environment);

builder.Services.AddCasazenDatabase(builder.Configuration);
var connectionString = NpgsqlConnectionStringNormalizer.Normalize(
    builder.Configuration.GetConnectionString("DefaultConnection"));
// Outside Development/Testing a missing connection string stops the startup instead of running in memory (FD-12).
RequiredConfiguration.EnsureDatabaseConnection(connectionString, builder.Environment);
// Session-pooler limits and the transaction pooler (port 6543, breaks advisory locks and Hangfire) are refused outside
// Development/Testing; elsewhere they are reported by the readiness check (HOSTING).
if (!string.IsNullOrEmpty(connectionString) && RequiredConfiguration.IsEnforced(builder.Environment))
{
    DatabaseConnectionSettings.Resolve(builder.Configuration, connectionString, hangfireRegistered: true).EnsureUsable();
}

// Hangfire (skipped when no connection string, e.g. in CI/test), in a schema dedicated to this environment:
// test and production share the database, never the queue (FD-11, docs/runbooks/hangfire.md).
var hangfireStorage = builder.Services.AddCasazenHangfire(builder.Configuration, builder.Environment, connectionString);

builder.Services.AddCasazenRepositories();
builder.Services.AddScoped<IGuestRepository, GuestRepository>();
builder.Services.AddScoped<IPropertyRepository, PropertyRepository>();
builder.Services.AddScoped<IPaymentRepository, PaymentRepository>();
builder.Services.AddScoped<IBookingRepository, BookingRepository>();
builder.Services.AddScoped<ITouristTaxRateRepository, TouristTaxRateRepository>();
builder.Services.AddScoped<IPricingAdapterConfigRepository, PricingAdapterConfigRepository>();
builder.Services.AddScoped<IPricingHistoryRepository, PricingHistoryRepository>();
builder.Services.AddScoped<ILeaseContractRepository, LeaseContractRepository>();
builder.Services.AddScoped<ILeaseEventRepository, LeaseEventRepository>();

builder.Services.AddMemoryCache();

builder.Services.AddCasazenUtcDateTimeHandling();

var stripeSecretKey = builder.Configuration["Stripe:SecretKey"];
if (!string.IsNullOrEmpty(stripeSecretKey))
{
    StripeConfiguration.ApiKey = stripeSecretKey;
}

builder.Services.AddCasazenBillingConfiguration(builder.Configuration, builder.Environment);

builder.Services.AddCasazenOpenAccessConfiguration(builder.Configuration);

builder.Services.AddCasazenServices();
builder.Services.AddDirectAlloggiati(builder.Environment.IsProduction() && builder.Configuration.GetValue<bool>("Alloggiati:DirectEnabled"));
builder.Services.AddRoss1000Direct();
builder.Services.AddScoped<IGuestService, GuestService>();
builder.Services.AddScoped<IPropertyService, PropertyService>();
builder.Services.AddScoped<IBookingService, BookingService>();
builder.Services.AddScoped<ITouristTaxService, TouristTaxService>();
builder.Services.AddScoped<IStatementService, StatementService>();
builder.Services.AddScoped<IAgencyInvoiceService, AgencyInvoiceService>();
builder.Services.AddScoped<IWireTransferService, WireTransferService>();
builder.Services.AddScoped<IWithholdingService, WithholdingService>();
builder.Services.AddScoped<IOtaManager, OtaManager>();
builder.Services.AddCasazenEmail(builder.Configuration, builder.Environment);
builder.Services.AddCasazenFiscalDocuments(builder.Configuration);
builder.Services.AddCasazenOwnerPortalOperations();
builder.Services.AddCasazenPush(builder.Configuration);
builder.Services.AddCasazenFileStorage(builder.Configuration);
builder.Services.AddScoped<IStripeService, StripeService>();
builder.Services.AddScoped<StripeWebhookHandler>();
builder.Services.AddScoped<ConcordatoFacilePaymentHandler>();
builder.Services.AddScoped<IStripeConnectGateway, StripeConnectGateway>();
builder.Services.AddScoped<IConnectOnboardingService, ConnectOnboardingService>();
builder.Services.AddScoped<ISupplierPaymentsAccountService, SupplierPaymentsAccountService>();
builder.Services.AddCasazenAuth0Management();
builder.Services.AddScoped<IAdminService, AdminService>();
builder.Services.AddScoped<ITouristTaxQuoteService, TouristTaxQuoteService>();
builder.Services.AddScoped<IGdprService, GdprService>();
builder.Services.AddScoped<IAlloggiatiWebService, AlloggiatiWebService>();
builder.Services.AddScoped<IPricingAdapterService, PricingAdapterService>();
builder.Services.AddScoped<IListingPriceSuggestionService, ListingPriceSuggestionService>();
builder.Services.AddCasazenAiProvider(builder.Configuration);
builder.Services.AddScoped<ITrustedSupplierService, TrustedSupplierService>();
builder.Services.AddScoped<ILeaseWorkflowService, LeaseWorkflowService>();
builder.Services.AddScoped<ILeaseDocumentService, LeaseDocumentService>();
builder.Services.AddScoped<ILeaseDocumentExtractionService, LeaseDocumentExtractionService>();
builder.Services.AddScoped<ILeaseDocumentReadingService, LeaseDocumentReadingService>();
builder.Services.AddSingleton<IContractTextReader, ContractTextReader>();
builder.Services.AddCasazenLeaseContractTemplates(builder.Configuration);
builder.Services.AddCasazenLeaseSigning(builder.Configuration);
builder.Services.AddCasazenLongRentLeaseDrafts();
builder.Services.AddCasazenLongRentLeaseTerm();
builder.Services.AddCasazenLongRentLeaseOperations(builder.Configuration);

builder.Services.AddCasazenOtaIntegrations(builder.Configuration);

builder.Services.AddCasazenBookingOperations();

builder.Services.AddCasazenTouristTaxCollection();

builder.Services.AddCasazenOctorate(builder.Configuration);

builder.Services.AddCasazenOctorateGuestAutoSend();

builder.Services.AddMessageSync();

builder.Services.AddMessagesHub();

builder.Services.AddCheckInLinkDelivery();

builder.Services.AddScheduledMessages();

builder.Services.AddNotificationPreferences();

builder.Services.AddApprovalQueueActions();

builder.Services.AddMessageAssistant();

builder.Services.AddOtaReviews();

builder.Services.AddCasazenMarketplace(builder.Configuration);

builder.Services.AddCrmMarketing(builder.Configuration);

builder.Services.AddCasazenOnboarding(builder.Configuration);

// The guest portal (epics E50-E60, casazen/backend#987): the stay link of a booking and the portal behind it, all registered in AddGuestPortal and
// behind Features:GuestPortal where they are used (the endpoints). Its sections, next steps and policies are found by scanning Core, Infrastructure and
// Web (AddGuestPortalRegistries): a later epic adds a class, never a line here.
builder.Services.AddGuestPortal();
builder.Services.AddGuestPortalRegistries(
    typeof(Casazen.Core.GuestPortal.PortalContext).Assembly,
    typeof(Casazen.Infrastructure.GuestPortal.PortalContextFactory).Assembly,
    typeof(Program).Assembly);

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

builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
        ApiProblemDetails.Complete(context.ProblemDetails, context.HttpContext));

builder.Services.AddCasazenAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddCasazenAuthorization();

builder.Services.AddCasazenHealthChecks();

builder.Services.AddCasazenCors();

builder.Services.AddCasazenForwardedHeaders();
builder.Services.AddCasazenRateLimiting();

builder.Services.AddScoped<OtaSyncJob>();
builder.Services.AddScoped<BookingPullJob>();
builder.Services.AddScoped<DynamicPricingJob>();
builder.Services.AddScoped<StripeWebhookJob>();
builder.Services.AddScoped<AlloggiatiWebReportJob>();
builder.Services.AddScoped<StayAlertsJob>();
builder.Services.AddScoped<CinDeadlineAlertJob>();
builder.Services.AddScoped<PropertyModeChangeJob>();
builder.Services.AddScoped<GdprDataRetentionJob>();
builder.Services.AddScoped<ESignWebhookJob>();
builder.Services.AddScoped<LeaseSignStatusPollingJob>();
builder.Services.AddScoped<LeaseRegistrationStatusPollingJob>();
builder.Services.AddScoped<RliDeadlineReminderJob>();
builder.Services.AddScoped<SeoPageGenerationJob>();
builder.Services.AddScoped<SeoContentRefreshJob>();
builder.Services.AddScoped<SeoEventRetentionJob>();
builder.Services.AddScoped<GuestCheckInSendJob>();
builder.Services.AddScoped<BookingFlowAutomationJob>();
builder.Services.AddScoped<CheckoutHoldExpiryJob>();
builder.Services.AddScoped<ServiceRequestAutoCancelJob>();
builder.Services.AddScoped<ServiceRequestGroupJob>();
builder.Services.AddScoped<ServiceRequestExpiryJob>();
builder.Services.AddScoped<ServiceRequestReminderJob>();
builder.Services.AddScoped<ServicePaymentSyncJob>();
builder.Services.AddScoped<ServicePaymentRemindersJob>();
builder.Services.AddScoped<SendPendingPaymentRequestsJob>();
builder.Services.AddScoped<DomainRecheckJob>();
builder.Services.AddScoped<PropertyComplianceCheckJob>();
builder.Services.AddScoped<IAlloggiatiReportScheduler, AlloggiatiReportScheduler>();
builder.Services.Configure<SeoBootstrapOptions>(
    builder.Configuration.GetSection(SeoBootstrapOptions.SectionName));
builder.Services.Configure<BookingsOptions>(
    builder.Configuration.GetSection(BookingsOptions.SectionName));
builder.Services.Configure<SeoEventOptions>(
    builder.Configuration.GetSection(SeoEventOptions.SectionName));
builder.Services.Configure<Casazen.Web.Controllers.IndexNowOptions>(
    builder.Configuration.GetSection("IndexNow"));
builder.Services.Configure<Casazen.Core.Options.PublicHostOptions>(
    builder.Configuration.GetSection(Casazen.Core.Options.PublicHostOptions.SectionName));
builder.Services.Configure<Casazen.Core.Options.VercelDomainsOptions>(
    builder.Configuration.GetSection(Casazen.Core.Options.VercelDomainsOptions.SectionName));
builder.Services.Configure<Casazen.Core.Options.ComplianceOptions>(
    builder.Configuration.GetSection(Casazen.Core.Options.ComplianceOptions.SectionName));
builder.Services.Configure<Casazen.Core.Options.StayAlertOptions>(
    builder.Configuration.GetSection(Casazen.Core.Options.StayAlertOptions.SectionName));
builder.Services.Configure<Casazen.Core.Options.RoutineOptions>(
    builder.Configuration.GetSection(Casazen.Core.Options.RoutineOptions.SectionName));
builder.Services.Configure<Casazen.Core.Options.EventBusOptions>(
    builder.Configuration.GetSection(Casazen.Core.Options.EventBusOptions.SectionName));
builder.Services.Configure<Casazen.Core.Options.GuestCheckInOptions>(
    builder.Configuration.GetSection(Casazen.Core.Options.GuestCheckInOptions.SectionName));
builder.Services.Configure<Casazen.Core.Options.RliOptions>(
    builder.Configuration.GetSection(Casazen.Core.Options.RliOptions.SectionName));
builder.Services.AddOptions<Casazen.Core.Options.ShortStayFiscalOptions>()
    .Bind(builder.Configuration.GetSection(Casazen.Core.Options.ShortStayFiscalOptions.SectionName))
    .Validate(o => o.IsValid(), "ShortStayFiscal: threshold, nights and rates must be positive (rates below 1) and every source set.")
    .ValidateOnStart();
// The official ISTAT comuni list is loaded before the SEO bootstrap, which generates the pages of its pilot comuni (SU-04).
builder.Services.Configure<ComuniOptions>(builder.Configuration.GetSection(ComuniOptions.SectionName));
builder.Services.AddHostedService<ComuneSeedHostedService>();
builder.Services.AddHostedService<AlloggiatiCodeTableSeedHostedService>();
builder.Services.AddHostedService<SwissPlacesSeedHostedService>();
builder.Services.AddHostedService<SeoBootstrapHostedService>();
builder.Services.AddHostedService<LegalDocumentsStartupCheck>();
builder.Services.AddHostedService<OwnerWithholdingDraftSeedHostedService>();
builder.Services.AddHostedService<AiSettingsWarmupHostedService>();
// E09/F01/US01/T02: with the connector on and the cache of the portal catalog still empty (a new environment), one run of the daily refresh is queued
// at start, so the portal list does not wait for 03:30 UTC. Only where Hangfire is (it queues a job), and it never holds or stops the start.
if (hangfireStorage is not null)
    builder.Services.AddHostedService<MarketplacePortalBootstrapHostedService>();

builder.Services.AddControllers(options => options.Filters.Add<ProblemDetailsResultFilter>())
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
    })
    .AddDataAnnotationsLocalization(options =>
        options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResources)))
    .ConfigureApiBehaviorOptions(options =>
    {
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
builder.Services.AddOptions<JsonOptions>()
    .Configure<IStringLocalizerFactory>((o, localizerFactory) =>
        o.JsonSerializerOptions.Converters.Add(
            new DefinedEnumJsonConverterFactory(localizerFactory.Create(typeof(SharedResources)))));
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
app.LogDatabaseConnectionPlan();

app.UseForwardedHeaders();

if (!string.IsNullOrEmpty(connectionString) && !app.Environment.IsEnvironment("Testing"))
{
    using var migrateScope = app.Services.CreateScope();
    var db = migrateScope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();

    // Values stored in clear before their column was encrypted (iCal import URLs PC-11, guest documents CO-14) are
    // rewritten encrypted; a no-op once done. Stops the startup when Data Protection is missing
    // (docs/runbooks/encryption.md).
    await EncryptedColumns.EncryptLegacyPlaintextAsync(
        db, migrateScope.ServiceProvider.GetRequiredService<ILogger<Program>>());
}

if (StorageExtensions.IsLegacyFileMigrationCommand(args))
{
    Environment.ExitCode = await app.RunLegacyFileMigrationAsync(args);
    return;
}

if (PropertyComplianceCheckJob.IsRecalculateCommand(args))
{
    Environment.ExitCode = await PropertyComplianceCheckJob.RunRecalculateCommandAsync(app, args);
    return;
}

app.LogDataProtectionKeyProtection();
if (args.Contains(DemoOtaInboxCommand.Command, StringComparer.Ordinal))
{
    Environment.ExitCode = await DemoOtaInboxCommand.RunAsync(app);
    return;
}

app.LogOpenAccess();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();

    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("====================================================");
    logger.LogInformation("🔵 Swagger UI available at:");
    logger.LogInformation("   → http://localhost:5000/swagger");
    logger.LogInformation("   → https://localhost:5001/swagger");
    logger.LogInformation("====================================================");
}

app.UseSecurityHeaders();

app.UseCasazenStaticFiles();

app.UseCors(CasazenCorsPolicyProvider.PolicyName);

app.UseRequestLocalization();

app.UseErrorHandling();

app.UseFeatureGates();

app.UseAuthentication();
app.UseTenantResolution();
app.UseInactiveAccountBlock();
app.UseMiddleware<Casazen.Web.Middleware.CommercialAccessMiddleware>();
app.UseAuthorization();
app.UseRateLimiter();

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
        DirectAlloggiatiExtensions.ConfigureJobs(recurringJobManager,
            app.Services.GetRequiredService<Casazen.Infrastructure.External.AlloggiatiTransportEnvironment>().Live);
        RecurringJobsRegistration.Configure(
            recurringJobManager,
            scope.ServiceProvider.GetRequiredService<IFeatureFlags>(),
            scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<RoutineOptions>>().Value);
    });
}

app.MapControllers();
app.MapHub<Casazen.Web.Hubs.NotificationsHub>(Casazen.Web.Hubs.NotificationsHub.Path);
app.MapCasazenHealthChecks();

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
