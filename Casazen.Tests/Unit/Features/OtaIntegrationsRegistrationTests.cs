using Casazen.Infrastructure.OTA;
using Casazen.Infrastructure.OTA.Resilience;
using Casazen.Web.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Unit.Features;

/// <summary>FD-20 / D10: the OTA partner adapters stay out of the container while <c>Features:OtaPartnerApi</c> is off.</summary>
public class OtaIntegrationsRegistrationTests
{
    private static readonly Type[] AdapterTypes =
    [
        typeof(AirbnbAdapter),
        typeof(BookingComAdapter),
        typeof(ExpediaAdapter),
        typeof(VrboAdapter),
        typeof(TripAdvisorAdapter),
        typeof(AgodaAdapter),
        typeof(OtaRateLimiter),
    ];

    [Fact]
    public void AddCasazenOtaIntegrations_FlagOff_RegistersNoAdapter()
    {
        var services = Register(flag: null);

        Assert.All(AdapterTypes, type => Assert.DoesNotContain(services, d => d.ServiceType == type));
        // OtaManager still resolves: the factory has no adapter to hand out.
        Assert.Contains(services, d => d.ServiceType == typeof(IChannelFactory));
    }

    [Fact]
    public void AddCasazenOtaIntegrations_FlagOn_RegistersAdapters()
    {
        var services = Register(flag: "true");

        Assert.All(AdapterTypes, type => Assert.Contains(services, d => d.ServiceType == type));
    }

    private static ServiceCollection Register(string? flag)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Features:OtaPartnerApi"] = flag })
            .Build();
        var services = new ServiceCollection();
        services.AddCasazenOtaIntegrations(configuration);
        return services;
    }
}
