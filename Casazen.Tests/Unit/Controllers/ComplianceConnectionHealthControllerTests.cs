using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Compliance;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// The permission rule of the connection health endpoint (E03/F02/US01/T02, TN-3): the row of the property is checked with the production
/// authorization handler. A collaborator who does not reach the property cannot read its health (403), another organization's property is
/// not found (404, the same answer as a property that does not exist), and in both cases the service is never called.
/// </summary>
public class ComplianceConnectionHealthControllerTests
{
    private static readonly Guid OrgA = Guid.Parse("6d5c4b3a-2918-4f7e-8d6c-5b4a39281702");
    private static readonly Guid PropertyOfOrgA = Guid.Parse("1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c11");
    private static readonly Guid PropertyOfOrgB = Guid.Parse("5e6f7a8b-9c0d-4e1f-a2b3-c4d5e6f7a812");
    private const string Owner = "auth0|owner-a";

    [Fact]
    public async Task ConnectionHealth_RowLevelAuthorization_Test()
    {
        var health = new Mock<IComplianceConnectionHealthService>();
        health
            .Setup(h => h.GetAsync(PropertyOfOrgA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Health(PropertyOfOrgA));

        // The property of the org A belongs to its owner; the property of the org B is hidden by the tenant filter.
        var lookup = new Mock<IHostResourceLookup>();
        lookup
            .Setup(l => l.ForPropertyAsync(PropertyOfOrgA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(HostResource.ForProperty(new Property { Id = PropertyOfOrgA, OrgId = OrgA, OwnerId = Owner, Name = "Casa del Lago" }));
        lookup
            .Setup(l => l.ForPropertyAsync(PropertyOfOrgB, It.IsAny<CancellationToken>()))
            .ReturnsAsync((HostResource?)null);

        // A member with no org-wide role and no grant reaches only the properties it created: nothing of this one, no call.
        var outsider = Controller(health, lookup, HostAuthorizationTestHarness.User("auth0|staff-a", "Staff"));
        Assert.IsType<ForbidResult>((await outsider.Get(PropertyOfOrgA, CancellationToken.None)).Result);

        // Another organization's property is not found, even for a manager of the whole org A.
        var manager = Controller(health, lookup, HostAuthorizationTestHarness.User("auth0|manager-a", "PropertyManager"));
        var notFound = Assert.IsType<ObjectResult>((await manager.Get(PropertyOfOrgB, CancellationToken.None)).Result);
        Assert.Equal(StatusCodes.Status404NotFound, notFound.StatusCode);
        Assert.Equal("property_not_found", Assert.IsAssignableFrom<ProblemDetails>(notFound.Value).Extensions["code"]);

        Assert.Empty(health.Invocations);

        // Whoever reaches the property reads its health, and the answer is not kept by any cache.
        var owner = Controller(health, lookup, HostAuthorizationTestHarness.User(Owner, "PropertyOwner"));
        var read = Assert.IsType<OkObjectResult>((await owner.Get(PropertyOfOrgA, CancellationToken.None)).Result);
        var dto = Assert.IsType<ComplianceConnectionHealthDto>(read.Value);
        Assert.Equal(PropertyOfOrgA, dto.PropertyId);
        Assert.Equal(ConnectionHealthState.Unknown, dto.Alloggiati.State);
        Assert.Equal(ConnectionHealthState.Unknown, dto.Istat.State);
        Assert.False(dto.CredentialsUsable);
        Assert.Equal("private, no-store", owner.Response.Headers.CacheControl.ToString());
        health.Verify(h => h.GetAsync(PropertyOfOrgA, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void ConnectionHealthController_RequiresThePropertyReadPolicy_Test()
    {
        var authorize = typeof(ComplianceConnectionHealthController)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
            .Single();

        Assert.Equal(Casazen.Web.Authorization.CasazenPolicies.PropertyRead, authorize.Policy);
    }

    private static ComplianceConnectionHealth Health(Guid propertyId)
    {
        var line = new ConnectionHealthLine(ComplianceSenderType.ChannelManager, ConnectionHealthState.Unknown, null, null, null, SenderReasonCodes.PoliceNotExposed, null);
        return new ComplianceConnectionHealth(propertyId, true, line, line, true, false, DateTime.UtcNow);
    }

    private static ComplianceConnectionHealthController Controller(
        Mock<IComplianceConnectionHealthService> health,
        Mock<IHostResourceLookup> lookup,
        ClaimsPrincipal user)
    {
        var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        return new ComplianceConnectionHealthController(
            health.Object,
            lookup.Object,
            HostAuthorizationTestHarness.Create(OrgA),
            NullLogger<ComplianceConnectionHealthController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user, RequestServices = services },
            },
        };
    }
}
