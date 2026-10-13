using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Web.Configuration;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Extensions;

/// <summary>
/// Lease signature (LT-02, A7-02, A7-20): offline signature by default; the provider path only with
/// <c>Features:ESignProvider</c> on and a configured provider (none is written yet: <see cref="UnconfiguredLeaseESignService"/>).
/// The <c>ESign</c> options are validated at startup (webhook secret required with the flag on). Runbook:
/// docs/runbooks/rli.md § Contract signature (LT-02).
/// </summary>
public static class LeaseSigningExtensions
{
    public static IServiceCollection AddCasazenLeaseSigning(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ESignOptions>()
            .Bind(configuration.GetSection(ESignOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ESignOptions>, ESignOptionsValidator>();
        services.AddSingleton<ILeaseESignService, UnconfiguredLeaseESignService>();
        services.AddScoped<ILeaseSigningService, LeaseSigningService>();
        return services;
    }
}
