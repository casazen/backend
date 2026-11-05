using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The proof of the mapping endpoints (E02/F01/US02/T04, ADR-023-R10, AM-03): confirming a listing to a property needs the row-level check
/// of the property, with the production authorization handler, and the one of the property the listing already belongs to. A member who
/// does not reach the property, anyone naming a property of another organization, and a member who reaches only the new property of a
/// listing confirmed to another one, get 404 and the mapping is never touched.
/// </summary>
public class OctorateMappingEndpointsTests
{
    private static readonly Guid OrgA = Guid.Parse("6d5c4b3a-2918-4f7e-8d6c-5b4a39281701");
    private static readonly Guid PropertyOfOrgA = Guid.Parse("1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c01");
    private static readonly Guid PropertyOfOrgB = Guid.Parse("5e6f7a8b-9c0d-4e1f-a2b3-c4d5e6f7a802");
    private static readonly Guid PropertyOfColleague = Guid.Parse("3c4d5e6f-7a8b-4c9d-8e0f-1a2b3c4d5e03");
    private const string Owner = "auth0|owner-a";
    private const string Colleague = "auth0|colleague-a";

    [Fact]
    public async Task OctorateMappingEndpoints_RowLevelAuthorization_Test()
    {
        var mappings = new Mock<IChannelMappingService>();
        var properties = new Mock<IPropertyService>();
        properties.Setup(p => p.GetPropertyRecordAsync(PropertyOfOrgA))
            .ReturnsAsync(new Property { Id = PropertyOfOrgA, OrgId = OrgA, OwnerId = Owner, Name = "Casa del Lago" });
        // The tenant filter hides another org's property: the record of the caller's org is not found.
        properties.Setup(p => p.GetPropertyRecordAsync(PropertyOfOrgB)).ReturnsAsync((Property?)null);
        // A colleague's property in the same org, which the owner (an account in no org team) does not reach.
        properties.Setup(p => p.GetPropertyRecordAsync(PropertyOfColleague))
            .ReturnsAsync(new Property { Id = PropertyOfColleague, OrgId = OrgA, OwnerId = Colleague, Name = "Casa del Bosco" });

        // The listings of the org: 1001 is proposed to the owner's property, 2002 is already confirmed to the colleague's.
        IReadOnlyList<ChannelMappingRow> rows =
        [
            Row("1001", OctorateMappingStatus.Proposed, PropertyOfOrgA),
            Row("2002", OctorateMappingStatus.Confirmed, PropertyOfColleague),
        ];
        mappings.Setup(m => m.ListAsync(OrgA, It.IsAny<CancellationToken>())).ReturnsAsync(rows);

        // A member with no org-wide role and no grant: it reaches only the properties it created, not this one.
        var outsider = Controller(mappings, properties, HostAuthorizationTestHarness.User("auth0|staff-a", "Staff"));
        var refused = await outsider.Confirm("1001", new ConfirmMappingRequest { PropertyId = PropertyOfOrgA }, CancellationToken.None);
        AssertNotFound(refused, "octorate_property_not_found");

        // Nobody names a property of another organization, an org-wide manager neither.
        var manager = Controller(mappings, properties, HostAuthorizationTestHarness.User("auth0|manager-a", "PropertyManager"));
        var foreign = await manager.Confirm("1001", new ConfirmMappingRequest { PropertyId = PropertyOfOrgB }, CancellationToken.None);
        AssertNotFound(foreign, "octorate_property_not_found");

        // The owner reaches the property it names, but listing 2002 is confirmed to a colleague's property it does not reach: the listing
        // is hidden from it, so reaching the new property alone does not take the listing over.
        var owner = Controller(mappings, properties, HostAuthorizationTestHarness.User(Owner, "PropertyOwner"));
        var takeover = await owner.Confirm("2002", new ConfirmMappingRequest { PropertyId = PropertyOfOrgA }, CancellationToken.None);
        AssertNotFound(takeover, "octorate_mapping_not_found");

        mappings.Verify(
            m => m.ConfirmAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // The person who reaches the property of the listing (its creator, in an account with no org team) confirms it.
        Assert.IsType<NoContentResult>(
            await owner.Confirm("1001", new ConfirmMappingRequest { PropertyId = PropertyOfOrgA }, CancellationToken.None));
        mappings.Verify(m => m.ConfirmAsync(OrgA, "1001", PropertyOfOrgA, null, Owner, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>A listing of the org with the property it is proposed or confirmed to.</summary>
    private static ChannelMappingRow Row(string accommodationId, OctorateMappingStatus status, Guid propertyId) =>
        new(
            Guid.NewGuid(),
            accommodationId,
            "Listing " + accommodationId,
            null,
            null,
            null,
            [],
            null,
            status,
            0,
            new ChannelMappingProperty(propertyId, "Casa " + accommodationId, "Via Roma 1", "Como"),
            [],
            [],
            null);

    private static void AssertNotFound(IActionResult result, string code)
    {
        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        Assert.Equal(code, Assert.IsAssignableFrom<ProblemDetails>(problem.Value).Extensions["code"]);
    }

    private static OctorateMappingController Controller(
        Mock<IChannelMappingService> mappings,
        Mock<IPropertyService> properties,
        System.Security.Claims.ClaimsPrincipal user)
    {
        return new OctorateMappingController(
            mappings.Object,
            properties.Object,
            new FixedOrg(OrgA),
            HostAuthorizationTestHarness.ScopeResolver(),
            HostAuthorizationTestHarness.Create(OrgA))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = user,
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };
    }

    private sealed class FixedOrg(Guid orgId) : IOrgContextResolver
    {
        public Task<Guid?> GetOrProvisionOrgIdAsync(CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(orgId);
    }
}
