## User Story

As a **frontend developer**, I want **standardized error responses following RFC 7807 Problem Details**, so that **I can handle errors consistently across all API endpoints**.

## Context

Current error handling middleware returns basic error responses. Need to implement RFC 7807 Problem Details for better API error communication.

## Technical Details

### RFC 7807 Problem Details Format

```json
{
  "type": "https://casazen.app/errors/validation-error",
  "title": "Validation Failed",
  "status": 400,
  "detail": "One or more validation errors occurred",
  "instance": "/api/properties",
  "traceId": "00-abc123...",
  "timestamp": "2026-03-30T10:15:30Z",
  "errors": {
    "Name": ["The Name field is required"],
    "NightlyRate": ["Must be greater than 0"]
  }
}
```

### Files to Create/Modify

1. **Casazen.Web/Middleware/ProblemDetailsMiddleware.cs** (enhance existing)

```csharp
public class ProblemDetailsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ProblemDetailsMiddleware> _logger;

    public ProblemDetailsMiddleware(
        RequestDelegate next,
        IWebHostEnvironment env,
        ILogger<ProblemDetailsMiddleware> logger)
    {
        _next = next;
        _env = env;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        _logger.LogError(exception, "Unhandled exception occurred");

        var problemDetails = exception switch
        {
            KeyNotFoundException notFound => CreateProblemDetails(
                context,
                StatusCodes.Status404NotFound,
                "Resource Not Found",
                notFound.Message,
                "https://casazen.app/errors/not-found"
            ),

            InvalidOperationException invalid => CreateProblemDetails(
                context,
                StatusCodes.Status400BadRequest,
                "Invalid Operation",
                invalid.Message,
                "https://casazen.app/errors/invalid-operation"
            ),

            UnauthorizedAccessException unauthorized => CreateProblemDetails(
                context,
                StatusCodes.Status403Forbidden,
                "Access Denied",
                "You do not have permission to perform this action",
                "https://casazen.app/errors/forbidden"
            ),

            _ => CreateProblemDetails(
                context,
                StatusCodes.Status500InternalServerError,
                "An error occurred",
                _env.IsDevelopment() ? exception.Message : "Please contact support",
                "https://casazen.app/errors/internal-server-error"
            )
        };

        // Add stack trace in development
        if (_env.IsDevelopment())
        {
            problemDetails.Extensions["stackTrace"] = exception.StackTrace;
        }

        context.Response.StatusCode = problemDetails.Status ?? 500;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsJsonAsync(problemDetails);
    }

    private ProblemDetails CreateProblemDetails(
        HttpContext context,
        int statusCode,
        string title,
        string detail,
        string type)
    {
        return new ProblemDetails
        {
            Type = type,
            Title = title,
            Status = statusCode,
            Detail = detail,
            Instance = context.Request.Path,
            Extensions =
            {
                ["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier,
                ["timestamp"] = DateTime.UtcNow
            }
        };
    }
}
```

2. **Configure in Program.cs**
```csharp
// Replace existing error handling middleware
app.UseMiddleware<ProblemDetailsMiddleware>();

// Configure built-in ProblemDetails
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions.Add("traceId",
            Activity.Current?.Id ?? context.HttpContext.TraceIdentifier);
        context.ProblemDetails.Extensions.Add("timestamp", DateTime.UtcNow);
    };
});
```

3. **Validation Error Handler** (for model validation)
```csharp
// Casazen.Web/Filters/ValidationProblemDetailsFilter.cs
public class ValidationProblemDetailsFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid)
        {
            var errors = context.ModelState
                .Where(e => e.Value.Errors.Count > 0)
                .ToDictionary(
                    e => e.Key,
                    e => e.Value.Errors.Select(er => er.ErrorMessage).ToArray()
                );

            var problemDetails = new ValidationProblemDetails(context.ModelState)
            {
                Type = "https://casazen.app/errors/validation-error",
                Title = "Validation Failed",
                Status = StatusCodes.Status400BadRequest,
                Detail = "One or more validation errors occurred",
                Instance = context.HttpContext.Request.Path
            };

            problemDetails.Extensions["traceId"] =
                Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
            problemDetails.Extensions["timestamp"] = DateTime.UtcNow;

            context.Result = new BadRequestObjectResult(problemDetails);
        }
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
```

4. **Register filter in Program.cs**
```csharp
builder.Services.AddControllers(options =>
{
    options.Filters.Add<ValidationProblemDetailsFilter>();
});
```

5. **Custom exception types** (optional, for clarity)
```csharp
// Casazen.Core/Exceptions/BusinessException.cs
public class BusinessException : Exception
{
    public int StatusCode { get; }
    public string Type { get; }

    public BusinessException(string message, int statusCode = 400, string type = null)
        : base(message)
    {
        StatusCode = statusCode;
        Type = type ?? "https://casazen.app/errors/business-error";
    }
}

// Usage in services:
throw new BusinessException("Property already has an active booking for these dates", 409);
```

## Acceptance Criteria

- [ ] ProblemDetailsMiddleware catches all unhandled exceptions
- [ ] ValidationProblemDetailsFilter returns validation errors in Problem Details format
- [ ] All error responses include: type, title, status, detail, instance, traceId, timestamp
- [ ] Development environment includes stack traces
- [ ] Production environment hides sensitive error details
- [ ] HTTP status codes match problem type (404, 400, 500, etc.)
- [ ] Integration test: POST invalid data returns 400 with Problem Details
- [ ] Integration test: GET non-existent resource returns 404 with Problem Details
- [ ] Integration test: Unhandled exception returns 500 with Problem Details

## Example Error Responses

### Validation Error (400)
```json
{
  "type": "https://casazen.app/errors/validation-error",
  "title": "Validation Failed",
  "status": 400,
  "detail": "One or more validation errors occurred",
  "instance": "/api/properties",
  "errors": {
    "Name": ["The Name field is required"],
    "NightlyRate": ["Must be greater than 0"]
  },
  "traceId": "00-abc123...",
  "timestamp": "2026-03-30T10:15:30Z"
}
```

### Not Found (404)
```json
{
  "type": "https://casazen.app/errors/not-found",
  "title": "Resource Not Found",
  "status": 404,
  "detail": "Property with ID 123 not found",
  "instance": "/api/properties/123",
  "traceId": "00-def456...",
  "timestamp": "2026-03-30T10:20:00Z"
}
```

### Internal Server Error (500)
```json
{
  "type": "https://casazen.app/errors/internal-server-error",
  "title": "An error occurred",
  "status": 500,
  "detail": "Please contact support",
  "instance": "/api/payments/process",
  "traceId": "00-ghi789...",
  "timestamp": "2026-03-30T10:25:00Z"
}
```

## Definition of Done

- [ ] ProblemDetailsMiddleware implemented
- [ ] ValidationProblemDetailsFilter implemented
- [ ] Configured in Program.cs
- [ ] All controllers return Problem Details on errors
- [ ] Integration tests for all error types
- [ ] Swagger documentation updated
- [ ] README updated with error handling documentation

## Estimated Effort

**1 day**

## Priority

📋 **MEDIUM** - Improves API consistency

## Dependencies

None
