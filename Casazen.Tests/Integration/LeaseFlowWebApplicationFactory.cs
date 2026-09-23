using Casazen.Core.Services;
using Casazen.Tests.Integration.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Casazen.Tests.Integration;

/// <summary>
/// Lease full-flow factory: confirming RLI stub so poll can reach Registered.
/// APE is stubbed on the base factory.
/// </summary>
public class LeaseFlowWebApplicationFactory : CasazenWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Rli:FilingEnabled"] = "true",
            });
        });
        builder.ConfigureTestServices(services =>
        {
            RemoveAllOf<ILeaseRegistrationService>(services);
            services.AddSingleton<ILeaseRegistrationService, ConfirmingLeaseRegistrationService>();
        });
    }
}
