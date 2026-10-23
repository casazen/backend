using Casazen.Web.Configuration;
using Microsoft.AspNetCore.Cors.Infrastructure;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Extension point for origins that are not in the configuration, such as the subdomains and custom domains of the hosts'
/// booking sites (<see cref="OrgHostCorsOriginSource"/>, BK-16). Implementations are registered in DI (scoped is fine)
/// and are asked only about origins the configuration rejects.
/// </summary>
public interface ICorsOriginSource
{
    /// <param name="origin">The <c>Origin</c> header of the request.</param>
    /// <param name="requestPath">Path of the request (of the actual one, for a preflight): a source may limit its origins to some endpoints.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    ValueTask<bool> IsOriginAllowedAsync(string origin, PathString requestPath, CancellationToken cancellationToken);
}

/// <summary>
/// CORS policy of the web app (FD-17, A3-29 / A9-28): only the configured origins (<see cref="CorsOriginAllowList"/>)
/// plus whatever an <see cref="ICorsOriginSource"/> accepts. No credentials for the API (it authenticates with a Bearer token
/// and uses no cookie); the SignalR hubs under <c>/hubs</c> allow them, as the SignalR client asks.
/// </summary>
public sealed class CasazenCorsPolicyProvider(
    CorsOriginAllowList allowList,
    IEnumerable<ICorsOriginSource> originSources) : ICorsPolicyProvider
{
    public const string PolicyName = "AllowFrontend";

    public async Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        if (policyName is not null && !string.Equals(policyName, PolicyName, StringComparison.Ordinal))
            return null;

        var origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin) || allowList.IsOriginAllowed(origin))
            return BuildPolicy(allowList.IsOriginAllowed, context.Request.Path);

        foreach (var source in originSources)
        {
            if (await source.IsOriginAllowedAsync(origin, context.Request.Path, context.RequestAborted))
                return BuildPolicy(candidate => string.Equals(candidate, origin, StringComparison.OrdinalIgnoreCase), context.Request.Path);
        }

        // Rejected origin: the policy is still evaluated, it simply emits no Access-Control-Allow-Origin.
        return BuildPolicy(allowList.IsOriginAllowed, context.Request.Path);
    }

    private static CorsPolicy BuildPolicy(Func<string, bool> isOriginAllowed, PathString requestPath)
    {
        var builder = new CorsPolicyBuilder()
            .SetIsOriginAllowed(isOriginAllowed)
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
            // Idempotency-Key: the key of a payment the host records (SR-06b); without it the browser's preflight refuses the call.
            // X-SignalR-User-Agent: sent by the SignalR browser client on its negotiate request (the hubs only use it).
            .WithHeaders("Authorization", "Content-Type", "Accept", "X-Requested-With", "Idempotency-Key", "X-SignalR-User-Agent");

        // The SignalR client negotiates with credentials included by default: the hubs answer Access-Control-Allow-Credentials for the
        // allowed origins (echoed one by one, never "*"). The API itself stays without credentials: it uses no cookie.
        if (requestPath.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
            builder.AllowCredentials();

        return builder.Build();
    }
}
