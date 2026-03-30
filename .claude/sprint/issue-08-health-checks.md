## User Story

As a **DevOps engineer**, I want **health check endpoints**, so that **Kubernetes/monitoring systems can verify the application is healthy and restart it if needed**.

## Context

Production deployment requires health checks for:
- Load balancers (route traffic only to healthy instances)
- Kubernetes liveness/readiness probes
- Monitoring dashboards (uptime tracking)

## Technical Details

### Health Check Endpoints

1. **GET /health** - Overall health (liveness probe)
2. **GET /health/ready** - Readiness check (can accept traffic)
3. **GET /health/live** - Liveness check (application is running)

### Files to Create

1. **Install NuGet Package**
```bash
dotnet add Casazen.Web package AspNetCore.HealthChecks.SqlServer
dotnet add Casazen.Web package AspNetCore.HealthChecks.UI
```

2. **Configure in Program.cs**
```csharp
// Add health checks
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database", tags: new[] { "ready" })
    .AddSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        name: "sql-server",
        tags: new[] { "ready" })
    .AddCheck<StripeHealthCheck>("stripe", tags: new[] { "ready" })
    .AddCheck<Auth0HealthCheck>("auth0", tags: new[] { "ready" })
    .AddCheck<OtaHealthCheck>("ota-platforms", tags: new[] { "ready" });

// Add health checks UI (optional dashboard)
builder.Services.AddHealthChecksUI(setup =>
{
    setup.AddHealthCheckEndpoint("CasaZen API", "/health");
}).AddInMemoryStorage();

// Map endpoints
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false, // No checks, just returns 200 if app is running
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});

// Optional: Health checks UI dashboard
app.MapHealthChecksUI(options => options.UIPath = "/health-ui");
```

3. **Custom Health Checks**

**Stripe Health Check:**
```csharp
// Casazen.Infrastructure/HealthChecks/StripeHealthCheck.cs
public class StripeHealthCheck : IHealthCheck
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<StripeHealthCheck> _logger;

    public StripeHealthCheck(IConfiguration configuration, ILogger<StripeHealthCheck> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Simple check: verify Stripe API key is configured
            var apiKey = _configuration["Stripe:SecretKey"];
            if (string.IsNullOrEmpty(apiKey))
            {
                return HealthCheckResult.Unhealthy("Stripe API key not configured");
            }

            // Optional: ping Stripe API
            StripeConfiguration.ApiKey = apiKey;
            var balanceService = new BalanceService();
            await balanceService.GetAsync(cancellationToken: cancellationToken);

            return HealthCheckResult.Healthy("Stripe API is accessible");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stripe health check failed");
            return HealthCheckResult.Unhealthy("Stripe API is not accessible", ex);
        }
    }
}
```

**Auth0 Health Check:**
```csharp
// Casazen.Infrastructure/HealthChecks/Auth0HealthCheck.cs
public class Auth0HealthCheck : IHealthCheck
{
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;

    public Auth0HealthCheck(IConfiguration configuration, HttpClient httpClient)
    {
        _configuration = configuration;
        _httpClient = httpClient;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var domain = _configuration["Auth0:Domain"];
            if (string.IsNullOrEmpty(domain))
            {
                return HealthCheckResult.Unhealthy("Auth0 domain not configured");
            }

            var response = await _httpClient.GetAsync(
                $"https://{domain}/.well-known/openid-configuration",
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Healthy("Auth0 is accessible");
            }

            return HealthCheckResult.Degraded($"Auth0 returned {response.StatusCode}");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Auth0 is not accessible", ex);
        }
    }
}
```

**OTA Platforms Health Check:**
```csharp
// Casazen.Infrastructure/HealthChecks/OtaHealthCheck.cs
public class OtaHealthCheck : IHealthCheck
{
    private readonly AppDbContext _context;

    public OtaHealthCheck(AppDbContext context)
    {
        _context = context;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Check if OTA integrations exist
            var activeIntegrations = await _context.OtaIntegrations
                .Where(o => o.IsActive && o.SyncEnabled)
                .CountAsync(cancellationToken);

            var data = new Dictionary<string, object>
            {
                { "activeIntegrations", activeIntegrations },
                { "lastSyncCheck", DateTime.UtcNow }
            };

            if (activeIntegrations == 0)
            {
                return HealthCheckResult.Degraded("No active OTA integrations", data: data);
            }

            return HealthCheckResult.Healthy($"{activeIntegrations} active OTA integrations", data);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("OTA health check failed", ex);
        }
    }
}
```

4. **Register Health Checks in Program.cs**
```csharp
builder.Services.AddHttpClient<Auth0HealthCheck>();
builder.Services.AddScoped<StripeHealthCheck>();
builder.Services.AddScoped<Auth0HealthCheck>();
builder.Services.AddScoped<OtaHealthCheck>();
```

## Acceptance Criteria

- [ ] GET /health returns 200 OK when all checks pass
- [ ] GET /health returns 503 Service Unavailable when any check fails
- [ ] GET /health/ready returns 200 OK only when database and external services are accessible
- [ ] GET /health/live returns 200 OK if application is running (no dependency checks)
- [ ] Database health check verifies SQL Server connectivity
- [ ] Stripe health check verifies API key configuration
- [ ] Auth0 health check verifies domain accessibility
- [ ] OTA health check reports active integrations
- [ ] Health check responses include status, duration, and details for each check
- [ ] Integration test: health endpoints return expected responses

## Example Health Check Response

```json
{
  "status": "Healthy",
  "totalDuration": "00:00:00.1234567",
  "entries": {
    "database": {
      "status": "Healthy",
      "duration": "00:00:00.0123456",
      "description": "Database connection successful"
    },
    "sql-server": {
      "status": "Healthy",
      "duration": "00:00:00.0098765"
    },
    "stripe": {
      "status": "Healthy",
      "duration": "00:00:00.0567890",
      "description": "Stripe API is accessible"
    },
    "auth0": {
      "status": "Healthy",
      "duration": "00:00:00.0345678",
      "description": "Auth0 is accessible"
    },
    "ota-platforms": {
      "status": "Healthy",
      "duration": "00:00:00.0012345",
      "description": "5 active OTA integrations",
      "data": {
        "activeIntegrations": 5,
        "lastSyncCheck": "2026-03-30T10:30:00Z"
      }
    }
  }
}
```

## Kubernetes Configuration Example

```yaml
apiVersion: v1
kind: Pod
metadata:
  name: casazen-api
spec:
  containers:
  - name: api
    image: casazen/backend:latest
    livenessProbe:
      httpGet:
        path: /health/live
        port: 8080
      initialDelaySeconds: 10
      periodSeconds: 10
    readinessProbe:
      httpGet:
        path: /health/ready
        port: 8080
      initialDelaySeconds: 5
      periodSeconds: 5
```

## Definition of Done

- [ ] Health check endpoints configured
- [ ] Custom health checks implemented (Stripe, Auth0, OTA)
- [ ] All health checks return correct status
- [ ] Integration tests for all health endpoints
- [ ] README updated with health check documentation
- [ ] Kubernetes deployment YAML example added

## Estimated Effort

**1 day**

## Priority

📊 **MEDIUM** - Required for production deployment

## Dependencies

None
