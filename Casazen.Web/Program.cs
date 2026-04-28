using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.OTA;
using Casazen.Infrastructure.OTA.Resilience;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using SendGrid.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (!string.IsNullOrEmpty(connectionString))
        options.UseSqlServer(connectionString);
    else
        options.UseInMemoryDatabase("CasazenTest");
});

// Hangfire Configuration (skipped when no connection string, e.g. in CI/test)
if (!string.IsNullOrEmpty(connectionString))
{
    builder.Services.AddHangfire(configuration => configuration
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UseSqlServerStorage(connectionString, new SqlServerStorageOptions
        {
            CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
            SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
            QueuePollInterval = TimeSpan.Zero,
            UseRecommendedIsolationLevel = true,
            DisableGlobalLocks = true
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

// External Services
builder.Services.AddSendGrid(options =>
{
    options.ApiKey = builder.Configuration["Email:SendGridApiKey"] ?? string.Empty;
});

// Services
builder.Services.AddCasazenServices();
builder.Services.AddScoped<IGuestService, GuestService>();
builder.Services.AddScoped<IPropertyService, PropertyService>();
builder.Services.AddScoped<IBookingService, BookingService>();
builder.Services.AddScoped<ITouristTaxService, TouristTaxService>();
builder.Services.AddScoped<IOtaManager, OtaManager>();
builder.Services.AddScoped<ISendGridService, SendGridService>();
builder.Services.AddScoped<IImageStorageService, LocalImageStorageService>();
builder.Services.AddScoped<StripeWebhookHandler>();
builder.Services.AddScoped<ITaxCalculationService, TaxCalculationService>();
builder.Services.AddScoped<IGdprService, GdprService>();
builder.Services.AddScoped<IAlloggiatiWebService, AlloggiatiWebService>();

// OTA Integrations with resilience patterns
builder.Services.AddCasazenOtaIntegrations(builder.Configuration);

// Authentication & Authorization
builder.Services.AddCasazenAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddCasazenAuthorization();

// CORS
builder.Services.AddCasazenCors(builder.Configuration);

// Background Jobs
builder.Services.AddScoped<OtaSyncJob>();
builder.Services.AddScoped<BookingPullJob>();
builder.Services.AddScoped<EmailQueueProcessor>();
builder.Services.AddScoped<StripeWebhookJob>();
builder.Services.AddScoped<AlloggiatiWebReportJob>();
builder.Services.AddScoped<GdprDataRetentionJob>();

// API
builder.Services.AddControllers();
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
});

var app = builder.Build();

// Swagger (must be before Authentication to allow anonymous access to swagger.json)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Static files (for serving uploaded images)
app.UseStaticFiles();

// CORS (must be before Authentication)
app.UseCors("AllowFrontend");

// Authentication & Authorization (must be in this order)
app.UseAuthentication();
app.UseAuthorization();

// Hangfire Dashboard and recurring jobs (only when Hangfire is configured)
if (!string.IsNullOrEmpty(connectionString))
{
    app.UseHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = new[] { new HangfireAuthorizationFilter(app.Environment.IsDevelopment()) }
    });

    ConfigureRecurringJobs();
}

app.MapControllers();

app.Run();

void ConfigureRecurringJobs()
{
    RecurringJob.AddOrUpdate<OtaSyncJob>(
        "ota-sync-all",
        job => job.ExecuteAsync(Guid.Empty),
        Cron.Hourly,
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    RecurringJob.AddOrUpdate<BookingPullJob>(
        "booking-pull-all",
        job => job.ExecuteAsync(Guid.Empty),
        "*/15 * * * *",
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
}

public partial class Program { }
