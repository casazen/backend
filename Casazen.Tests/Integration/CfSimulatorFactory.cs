using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Casazen.Tests.Integration;

public sealed class CfSimulatorFactory : CasazenWebApplicationFactory
{
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
