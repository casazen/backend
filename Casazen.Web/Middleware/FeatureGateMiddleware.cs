using Casazen.Core.Features;
using Casazen.Web.Infrastructure;

namespace Casazen.Web.Middleware;

/// <summary>
/// Answers 404 for an endpoint marked <see cref="FeatureGateAttribute"/> whose flag is off. It runs after routing and
/// before authentication, so a disabled endpoint is indistinguishable from a missing route (anonymous callers included);
/// the empty 404 gets the standard problem body from <see cref="ErrorHandlingMiddleware"/>.
/// </summary>
public sealed class FeatureGateMiddleware(RequestDelegate next, IFeatureFlags featureFlags, ILogger<FeatureGateMiddleware> logger)
{
    public Task InvokeAsync(HttpContext context)
    {
        var gates = context.GetEndpoint()?.Metadata.GetOrderedMetadata<FeatureGateAttribute>();
        var disabled = gates?.FirstOrDefault(gate => !featureFlags.IsEnabled(gate.Flag));
        if (disabled is null)
            return next(context);

        logger.LogDebug(
            "{Method} {Route} is behind the disabled feature flag {Flag}: 404",
            context.Request.Method,
            (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText,
            disabled.Flag);
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }
}

public static class FeatureGateMiddlewareExtensions
{
    /// <summary>After <c>UseErrorHandling</c> (standard 404 body) and before <c>UseAuthentication</c>.</summary>
    public static IApplicationBuilder UseFeatureGates(this IApplicationBuilder app) =>
        app.UseMiddleware<FeatureGateMiddleware>();
}
