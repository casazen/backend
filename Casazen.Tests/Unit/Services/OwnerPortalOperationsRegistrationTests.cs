using Casazen.Core.Services;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;
using Casazen.Web.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// What <c>AddCasazenOwnerPortalOperations</c> registers (feature E07/F07, NF-111): the one place the owner portal operations are wired, so the exact list is
/// pinned here. The proofs build the services by hand: without this test a registration taken out would fail nothing, and the portal would answer 500.
/// </summary>
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
            // What a caller of the portal reaches (E07/F07/US01).
            (typeof(IOwnerPortalAccessResolver), typeof(OwnerPortalAccessResolver), ServiceLifetime.Scoped),

            // The owner calendar, the owner stays and their background work (E07/F07/US01/T01).
            (typeof(IOwnerStayService), typeof(OwnerStayService), ServiceLifetime.Scoped),
            (typeof(OwnerPortalOperationsJob), typeof(OwnerPortalOperationsJob), ServiceLifetime.Scoped),

            // The shared documents, the delegated contacts and the inbox (E07/F07/US01/T02).
            (typeof(IOwnerFileScanner), typeof(OwnerFileContentScanner), ServiceLifetime.Singleton),
            (typeof(IOwnerCollaborationService), typeof(OwnerCollaborationService), ServiceLifetime.Scoped),

            // The keyed index of the delegated contacts (the address is encrypted at rest; the options are validated at startup).
            (typeof(IOwnerContactIndex), typeof(OwnerContactIndex), ServiceLifetime.Singleton),
        };

        Assert.Equal(expected, ours);
    }
}
