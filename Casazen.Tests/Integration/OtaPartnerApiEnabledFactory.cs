using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Casazen.Tests.Integration;

public sealed class OtaPartnerApiEnabledFactory : CasazenWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:OtaPartnerApi"] = "true" }));
    }
}
