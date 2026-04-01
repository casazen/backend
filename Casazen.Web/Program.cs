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
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")!));

// Hangfire Configuration
builder.Services.AddHangfire(configuration => configuration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("DefaultConnection")!, new SqlServerStorageOptions
    {
        CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
        SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
        QueuePollInterval = TimeSpan.Zero,
        UseRecommendedIsolationLevel = true,
        DisableGlobalLocks = true
    }));

// Add Hangfire server
builder.Services.AddHangfireServer();

// Repositories
builder.Services.AddScoped<IGuestRepository, GuestRepository>();
builder.Services.AddScoped<IPropertyRepository, PropertyRepository>();
builder.Services.AddScoped<IPaymentRepository, PaymentRepository>();

// External Services
builder.Services.AddSendGrid(options =>
{
    options.ApiKey = builder.Configuration["Email:SendGridApiKey"] ?? string.Empty;
});

// Services
builder.Services.AddScoped<IGuestService, GuestService>();
builder.Services.AddScoped<IPropertyService, PropertyService>();
builder.Services.AddScoped<IOtaManager, OtaManager>();
builder.Services.AddScoped<ISendGridService, SendGridService>();
builder.Services.AddScoped<StripeWebhookHandler>();

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

    // JWT Bearer Authentication for Swagger UI
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

// CORS (must be before Authentication)
app.UseCors("AllowFrontend");

// Authentication & Authorization (must be in this order)
app.UseAuthentication();
app.UseAuthorization();

// Hangfire Dashboard (Development only for security)
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new HangfireAuthorizationFilter(app.Environment.IsDevelopment()) }
});

app.MapControllers();

// Configure Recurring Jobs
ConfigureRecurringJobs();

app.Run();

void ConfigureRecurringJobs()
{
    // OTA Sync - Run every hour for all active properties
    // In production, this would query for all properties and queue individual jobs
    RecurringJob.AddOrUpdate<OtaSyncJob>(
        "ota-sync-all",
        job => job.ExecuteAsync(Guid.Empty), // Placeholder - replace with actual property iteration logic
        Cron.Hourly,
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc
        });

    // Booking Pull - Run every 15 minutes
    RecurringJob.AddOrUpdate<BookingPullJob>(
        "booking-pull-all",
        job => job.ExecuteAsync(Guid.Empty), // Placeholder - replace with actual property iteration logic
        "*/15 * * * *", // Every 15 minutes
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc
        });
}