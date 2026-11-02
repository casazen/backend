using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Services.LeaseLetters;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Extensions;

/// <summary>
/// The operations of a lease in force (LR-OPS-1): the letters CasaZen prepares for the landlord (options validated at startup: a fake approval
/// stops Production, exactly like the contract templates), the catalog of their files and the service behind
/// <c>GET api/leases/{id}/operations</c> and <c>POST api/leases/{id}/letters/…</c>. The endpoints are always on, there is no flag. Runbook:
/// <c>docs/runbooks/lease-operations.md</c>.
/// </summary>
public static class LongRentLeaseOperationsExtensions
{
    public static IServiceCollection AddCasazenLongRentLeaseOperations(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LeaseLetterOptions>()
            .Bind(configuration.GetSection(LeaseLetterOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<LeaseLetterOptions>, LeaseLetterOptionsValidator>();
        services.AddSingleton<ILeaseLetterCatalog, LeaseLetterCatalog>();
        services.AddScoped<ILeaseOperationsService, LeaseOperationsService>();
        return services;
    }
}
