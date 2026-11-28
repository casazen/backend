using Casazen.Core.Features;
using Casazen.Web.Infrastructure;

namespace Casazen.Web.Middleware;

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
    public static IApplicationBuilder UseFeatureGates(this IApplicationBuilder app) =>
        app.UseMiddleware<FeatureGateMiddleware>();
}
