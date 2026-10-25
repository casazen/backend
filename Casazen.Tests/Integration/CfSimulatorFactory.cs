using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Casazen.Tests.Integration;

/// <summary>
/// The web host with the simulated module of ConcordatoFacile on (CF-SIM): <c>ConcordatoFacile:FormMode=Simulated</c>, the address of the
/// web app and a link secret. The Testing environment accepts it as a test environment does; Production does not (see
/// <c>ConcordatoFacileFormModeTests</c>).
/// </summary>
public sealed class CfSimulatorFactory : CasazenWebApplicationFactory
{
    /// <summary>The secret that signs and verifies the links of the module (at least 32 characters).</summary>
    public const string LinkSecret = "cf-simulator-test-link-secret-0123456789abcdef";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConcordatoFacile:FormMode"] = "Simulated",
                ["ConcordatoFacile:BaseUrl"] = "https://casazen-test-app.example",
                ["ConcordatoFacile:LinkSecret"] = LinkSecret,
            }));
    }
}
