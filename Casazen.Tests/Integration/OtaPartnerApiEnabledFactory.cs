using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Casazen.Tests.Integration;

/// <summary>
/// The default integration factory with <c>Features:OtaPartnerApi=true</c> (FD-20): the OTA partner endpoints, in
/// freeze and 404 by default (D10), exist again, so their behaviour (tenant isolation, removed endpoints) stays tested.
/// </summary>
public sealed class OtaPartnerApiEnabledFactory : CasazenWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:OtaPartnerApi"] = "true" }));
    }
}
