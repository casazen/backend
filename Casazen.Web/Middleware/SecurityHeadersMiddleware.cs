using Casazen.Web.Configuration;

namespace Casazen.Web.Middleware;

/// <summary>
/// Adds baseline security headers to every response (FD-17, A9-29). The API answers JSON and is never meant to be
/// framed: <c>frame-ancestors 'none'</c> (plus <c>X-Frame-Options</c> for older browsers). HSTS is sent outside
/// Development and Testing, i.e. on both Railway environments (TLS terminates at the Railway edge, the browser only
/// ever sees https). The headers are set when the response starts, so an error response written later keeps them.
/// The web app's own headers (CSP for the SPA) are in the frontend <c>vercel.json</c>.
/// </summary>
public class SecurityHeadersMiddleware(RequestDelegate next, IHostEnvironment environment)
{
    /// <summary>One year, subdomains of the API host included; no <c>preload</c> (it cannot be undone quickly).</summary>
    public const string StrictTransportSecurity = "max-age=31536000; includeSubDomains";

    public const string ContentSecurityPolicy = "frame-ancestors 'none'";

    private readonly bool _sendHsts = RequiredConfiguration.IsEnforced(environment);

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var (response, sendHsts) = ((HttpResponse, bool))state;
            var headers = response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers.XXSSProtection = "0";
            headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
            if (sendHsts)
                headers.StrictTransportSecurity = StrictTransportSecurity;
            return Task.CompletedTask;
        }, (context.Response, _sendHsts));

        return next(context);
    }
}

public static class SecurityHeadersMiddlewareExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.UseMiddleware<SecurityHeadersMiddleware>();
}
