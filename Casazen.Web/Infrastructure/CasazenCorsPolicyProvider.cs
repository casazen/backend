using Casazen.Web.Configuration;
using Microsoft.AspNetCore.Cors.Infrastructure;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Extension point for origins that are not in the configuration, such as the custom domains of the hosts' booking
/// sites (task BK-16). Implementations are registered in DI (scoped is fine) and are asked only about origins the
/// configuration rejects. None is registered today.
/// </summary>
public interface ICorsOriginSource
{
    ValueTask<bool> IsOriginAllowedAsync(string origin, CancellationToken cancellationToken);
}

/// <summary>
/// CORS policy of the web app (FD-17, A3-29 / A9-28): only the configured origins (<see cref="CorsOriginAllowList"/>)
/// plus whatever an <see cref="ICorsOriginSource"/> accepts. No credentials: the API authenticates with a Bearer token
/// and uses no cookie, so a browser never has to send cookies cross-origin.
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
            return BuildPolicy(allowList.IsOriginAllowed);

        foreach (var source in originSources)
        {
            if (await source.IsOriginAllowedAsync(origin, context.RequestAborted))
                return BuildPolicy(candidate => string.Equals(candidate, origin, StringComparison.OrdinalIgnoreCase));
        }

        // Rejected origin: the policy is still evaluated, it simply emits no Access-Control-Allow-Origin.
        return BuildPolicy(allowList.IsOriginAllowed);
    }

    private static CorsPolicy BuildPolicy(Func<string, bool> isOriginAllowed) =>
        new CorsPolicyBuilder()
            .SetIsOriginAllowed(isOriginAllowed)
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
            .WithHeaders("Authorization", "Content-Type", "Accept", "X-Requested-With")
            .Build();
}
