using Casazen.Core.Services;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;
using Casazen.Web.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class OwnerPortalOperationsRegistrationTests
{
    [Fact]
    public void AddCasazenOwnerPortalOperations_RegistersTheServicesAndTheJob_AndNothingElse()
    {
        var services = new ServiceCollection();

        services.AddCasazenOwnerPortalOperations();

        var ours = services
            .Where(d => d.ServiceType.Namespace?.StartsWith("Casazen", StringComparison.Ordinal) == true)
            .Select(d => (d.ServiceType, d.ImplementationType, d.Lifetime))
            .ToList();
        var expected = new List<(Type ServiceType, Type? ImplementationType, ServiceLifetime Lifetime)>
        {
            (typeof(IOwnerPortalAccessResolver), typeof(OwnerPortalAccessResolver), ServiceLifetime.Scoped),

            (typeof(IOwnerStayService), typeof(OwnerStayService), ServiceLifetime.Scoped),
            (typeof(OwnerPortalOperationsJob), typeof(OwnerPortalOperationsJob), ServiceLifetime.Scoped),

            (typeof(IOwnerFileScanner), typeof(OwnerFileContentScanner), ServiceLifetime.Singleton),
            (typeof(IOwnerCollaborationService), typeof(OwnerCollaborationService), ServiceLifetime.Scoped),

            (typeof(IOwnerContactIndex), typeof(OwnerContactIndex), ServiceLifetime.Singleton),
        };

        Assert.Equal(expected, ours);
    }
}
