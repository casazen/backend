using System.Reflection;
using System.Text.Json;
using Casazen.Core.Exceptions;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Stripe;

namespace Casazen.Web.Middleware;

/// <summary>
/// Turns exceptions and empty error responses into the API's single error shape (<see cref="ApiProblemDetails"/>).
/// </summary>
/// <remarks>
/// Exception → response:
/// <list type="bullet">
/// <item><see cref="DomainConflictException"/> → 409, <see cref="DomainException"/> → 422: its code and localized message;</item>
/// <item><see cref="NotFoundException"/> → 404: its code/message, or the generic <c>not_found</c>;</item>
/// <item><see cref="Casazen.Core.Exceptions.ValidationException"/> → 422 with <c>errors</c>;</item>
/// <item><see cref="UnauthorizedAccessException"/> → 403 <c>forbidden</c> (the caller is authenticated but may not act
/// on the resource; 401 is reserved for a missing or invalid token);</item>
/// <item><see cref="PaymentProcessingException"/>, <see cref="StripeException"/> → 503 <c>payment_provider_error</c>;</item>
/// <item>anything else, <see cref="InvalidOperationException"/> included → 500 <c>internal_error</c>.</item>
/// </list>
/// The client never receives an exception message: the details go to the log with the trace id, the route template
/// (not the concrete path, which may contain e-mails or tokens) and the exception type.
/// </remarks>
public class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation(
                "Request aborted by the client: {Method} {Route}. TraceId={TraceId}",
                context.Request.Method,
                GetRouteTemplate(context),
                ApiProblemDetails.GetTraceId(context));
            if (!context.Response.HasStarted)
                context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            return;
        }
        catch (Exception ex)
        {
            if (context.Response.HasStarted)
            {
                LogUnhandled(context, ex, "after the response started");
                throw;
            }

            await HandleExceptionAsync(context, ex);
            return;
        }

        // Empty error responses (auth challenge/forbid, rate limiter, unmatched route...) get the same shape.
        if (IsEmptyErrorResponse(context))
            await WriteProblemAsync(context, ApiProblemDetails.Create(context, context.Response.StatusCode));
    }

    private Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        exception = Unwrap(exception);

        ProblemDetails problem;
        switch (exception)
        {
            case DomainException domain:
                var status = domain is DomainConflictException
                    ? StatusCodes.Status409Conflict
                    : StatusCodes.Status422UnprocessableEntity;
                problem = ApiProblemDetails.Create(context, status, domain.Code, domain.MessageKey, domain.MessageArgs);
                LogRejected(context, exception, status, domain.Code);
                break;

            case NotFoundException notFound:
                problem = ApiProblemDetails.Create(
                    context,
                    StatusCodes.Status404NotFound,
                    notFound.Code ?? ProblemCodes.NotFound,
                    notFound.MessageKey);
                LogRejected(context, exception, StatusCodes.Status404NotFound, notFound.Code ?? ProblemCodes.NotFound);
                break;

            case Casazen.Core.Exceptions.ValidationException validation:
                problem = new ValidationProblemDetails(validation.Errors)
                {
                    Status = StatusCodes.Status422UnprocessableEntity,
                };
                ApiProblemDetails.Complete(problem, context);
                LogRejected(context, exception, StatusCodes.Status422UnprocessableEntity, ProblemCodes.ValidationError);
                break;

            case UnauthorizedAccessException:
                problem = ApiProblemDetails.Create(context, StatusCodes.Status403Forbidden, ProblemCodes.Forbidden);
                logger.LogWarning(
                    "Access denied on {Method} {Route}. TraceId={TraceId}",
                    context.Request.Method,
                    GetRouteTemplate(context),
                    ApiProblemDetails.GetTraceId(context));
                break;

            case BadHttpRequestException badRequest:
                problem = ApiProblemDetails.Create(context, badRequest.StatusCode);
                LogRejected(context, exception, badRequest.StatusCode, ProblemCodes.ForStatus(badRequest.StatusCode));
                break;

            case PaymentProcessingException or StripeException:
                problem = ApiProblemDetails.Create(
                    context,
                    StatusCodes.Status503ServiceUnavailable,
                    ProblemCodes.PaymentProviderError,
                    "PaymentProviderUnavailableDetail");
                LogUnhandled(context, exception, "from the payment provider");
                break;

            default:
                problem = ApiProblemDetails.Create(context, StatusCodes.Status500InternalServerError, ProblemCodes.InternalError);
                LogUnhandled(context, exception, "unhandled");
                break;
        }

        return WriteProblemAsync(context, problem);
    }

    /// <summary>
    /// Only unwraps containers that add no information (reflection, single-task aggregates). Other inner exceptions
    /// are causes (e.g. a PostgresException inside a DbUpdateException) and must not change the response.
    /// </summary>
    private static Exception Unwrap(Exception exception)
    {
        while (true)
        {
            switch (exception)
            {
                case TargetInvocationException { InnerException: { } inner }:
                    exception = inner;
                    continue;
                case AggregateException aggregate when aggregate.InnerExceptions.Count == 1:
                    exception = aggregate.InnerExceptions[0];
                    continue;
                default:
                    return exception;
            }
        }
    }

    private void LogRejected(HttpContext context, Exception exception, int statusCode, string code)
    {
        // 4xx outcome of a business rule: no stack trace, no exception message (it may echo user input).
        logger.LogInformation(
            "Request rejected with {StatusCode} {ErrorCode} ({ExceptionType}) on {Method} {Route}. TraceId={TraceId}",
            statusCode,
            code,
            exception.GetType().Name,
            context.Request.Method,
            GetRouteTemplate(context),
            ApiProblemDetails.GetTraceId(context));
    }

    private void LogUnhandled(HttpContext context, Exception exception, string kind)
    {
        logger.LogError(
            exception,
            "Exception {ExceptionType} ({Kind}) on {Method} {Route}. TraceId={TraceId}",
            exception.GetType().Name,
            kind,
            context.Request.Method,
            GetRouteTemplate(context),
            ApiProblemDetails.GetTraceId(context));
    }

    private static string GetRouteTemplate(HttpContext context) =>
        (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)";

    private static bool IsEmptyErrorResponse(HttpContext context) =>
        context.Response.StatusCode >= StatusCodes.Status400BadRequest
        && !context.Response.HasStarted
        && context.Response.ContentLength is null
        && string.IsNullOrEmpty(context.Response.ContentType)
        && !HttpMethods.IsHead(context.Request.Method);

    private static Task WriteProblemAsync(HttpContext context, ProblemDetails problem)
    {
        context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        // Serialize with the runtime type: a ValidationProblemDetails must keep its "errors".
        return context.Response.WriteAsJsonAsync(problem, problem.GetType(), JsonOptions, ApiProblemDetails.ContentType);
    }
}

public static class ErrorHandlingMiddlewareExtensions
{
    public static IApplicationBuilder UseErrorHandling(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<ErrorHandlingMiddleware>();
    }
}
