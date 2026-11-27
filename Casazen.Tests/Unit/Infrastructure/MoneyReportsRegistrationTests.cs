using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Web.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// NF-97, NF-98 (E07/F03/US02): what <c>AddCasazenServices</c> registers for the money reports: the two reports and the provider of the portal settings, each once and
/// scoped (they read the org's database for one request), and the container can build all three with what the application has, so a report is never a 500 because a
/// dependency is missing.
/// </summary>
public class MoneyReportsRegistrationTests
{
    [Fact]
    public void AddCasazenServices_RegistersTheTwoMoneyReportsAndTheSettingsProvider_OnceAndScoped()
    {
        var services = new ServiceCollection();

        services.AddCasazenServices();

        foreach (var (service, implementation) in new[]
                 {
                     (typeof(IHostCollectionsReportService), typeof(HostCollectionsReportService)),
                     (typeof(IChannelCostsReportService), typeof(ChannelCostsReportService)),
                     (typeof(IChannelCommissionSettingsProvider), typeof(ChannelCommissionSettingsProvider)),
                 })
        {
            var registered = Assert.Single(services, d => d.ServiceType == service);
            Assert.Equal(implementation, registered.ImplementationType);
            Assert.Equal(ServiceLifetime.Scoped, registered.Lifetime);
        }
    }

    [Fact]
    public void TheContainer_BuildsTheMoneyReports_WithWhatTheApplicationHas()
    {
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(h => h.EnvironmentName).Returns("Staging");
        var services = new ServiceCollection();
        services.AddLogging();
        // The tenant of the context is the request's (AddCasazenServices registers it), which reads the HTTP context.
        services.AddHttpContextAccessor();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(host.Object);
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase($"money-reports-{Guid.NewGuid()}"));
        // The mapper of the booking import is registered by the Octorate module, which this test does not need whole.
        services.AddSingleton<BookingStateMapper>();
        services.AddCasazenServices();
        using var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();

        Assert.IsType<HostCollectionsReportService>(scope.ServiceProvider.GetRequiredService<IHostCollectionsReportService>());
        Assert.IsType<ChannelCostsReportService>(scope.ServiceProvider.GetRequiredService<IChannelCostsReportService>());
        Assert.IsType<ChannelCommissionSettingsProvider>(scope.ServiceProvider.GetRequiredService<IChannelCommissionSettingsProvider>());
    }
}
