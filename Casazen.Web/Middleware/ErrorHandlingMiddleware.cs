using System.Text.Json;
using Casazen.Core.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Middleware;

/// <summary>
/// Global exception handler that converts exceptions to RFC 7807 Problem Details responses.
/// </summary>
public class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);

            // Handle auth responses that bypass exception flow
            if (!context.Response.HasStarted)
            {
                await HandleStatusCodeAsync(context);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
            await HandleExceptionAsync(context, ex);
        }
    }

    private static Task HandleStatusCodeAsync(HttpContext context)
    {
        return context.Response.StatusCode switch
        {
            StatusCodes.Status401Unauthorized when !context.Response.ContentLength.HasValue =>
                WriteProblemAsync(context, new ProblemDetails
                {
                    Type = "https://tools.ietf.org/html/rfc7235#section-3.1",
                    Title = "Unauthorized",
                    Status = StatusCodes.Status401Unauthorized,
                    Detail = "Authentication is required. Provide a valid Bearer token in the Authorization header.",
                }),
            StatusCodes.Status403Forbidden when !context.Response.ContentLength.HasValue =>
                WriteProblemAsync(context, new ProblemDetails
                {
                    Type = "https://tools.ietf.org/html/rfc7231#section-6.5.3",
                    Title = "Forbidden",
                    Status = StatusCodes.Status403Forbidden,
                    Detail = "You do not have permission to access this resource.",
                }),
            _ => Task.CompletedTask,
        };
    }

    private Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var problem = exception switch
        {
            ValidationException validationEx => BuildValidationProblem(validationEx),
            NotFoundException notFoundEx => new ProblemDetails
            {
                Type = "https://tools.ietf.org/html/rfc7231#section-6.5.4",
                Title = "Not Found",
                Status = StatusCodes.Status404NotFound,
                Detail = notFoundEx.Message,
            },
            PaymentProcessingException paymentEx => new ProblemDetails
            {
                Type = "https://casazen.app/errors/payment-processing-error",
                Title = "Payment Processing Error",
                Status = StatusCodes.Status503ServiceUnavailable,
                Detail = paymentEx.Message,
            },
            UnauthorizedAccessException => new ProblemDetails
            {
                Type = "https://tools.ietf.org/html/rfc7235#section-3.1",
                Title = "Unauthorized",
                Status = StatusCodes.Status401Unauthorized,
                Detail = "Authentication is required to access this resource.",
            },
            _ => new ProblemDetails
            {
                Type = "https://tools.ietf.org/html/rfc7231#section-6.6.1",
                Title = "Internal Server Error",
                Status = StatusCodes.Status500InternalServerError,
                Detail = "An unexpected error occurred. Please try again later.",
            },
        };

        problem.Instance = $"{context.Request.Method} {context.Request.Path}";
        problem.Extensions["traceId"] = context.TraceIdentifier;

        return WriteProblemAsync(context, problem);
    }

    private static ValidationProblemDetails BuildValidationProblem(ValidationException ex)
    {
        var problem = new ValidationProblemDetails(ex.Errors)
        {
            Type = "https://tools.ietf.org/html/rfc7231#section-6.5.1",
            Title = "One or more validation errors occurred.",
            Status = StatusCodes.Status422UnprocessableEntity,
            Detail = ex.Message,
        };
        return problem;
    }

    private static Task WriteProblemAsync(HttpContext context, ProblemDetails problem)
    {
        context.Response.ContentType = "application/problem+json";
        context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        return context.Response.WriteAsync(JsonSerializer.Serialize(problem, JsonOptions));
    }
}

public static class ErrorHandlingMiddlewareExtensions
{
    public static IApplicationBuilder UseErrorHandling(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<ErrorHandlingMiddleware>();
    }
}
