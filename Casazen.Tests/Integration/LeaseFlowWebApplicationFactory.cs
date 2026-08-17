using Casazen.Core.Services;
using Casazen.Tests.Integration.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Casazen.Tests.Integration;

/// <summary>
/// Lease full-flow factory: no-op APE gate + confirming RLI stub so poll can reach Registered.
/// </summary>
public class LeaseFlowWebApplicationFactory : CasazenWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            RemoveService<IApeComplianceService>(services);
            var ape = new Mock<IApeComplianceService>();
            ape.Setup(s => s.EnsurePropertyHasValidApeAsync(It.IsAny<Guid>()))
                .Returns(Task.CompletedTask);
            services.AddSingleton(ape.Object);

            RemoveService<ILeaseRegistrationService>(services);
            services.AddSingleton<ILeaseRegistrationService, ConfirmingLeaseRegistrationService>();
        });
    }
}
