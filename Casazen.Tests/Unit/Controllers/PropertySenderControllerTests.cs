using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// The permission rule of the sender endpoints (E03/F01/US02/T01, ADR-029-R6, TN-3): every action checks the row of the property with the
/// production authorization handler. A member of the org who does not reach the property cannot read or change its sender (403), another
/// organization's property is not found (404, the same answer as a property that does not exist), and in both cases the sender service is
/// never called.
/// </summary>
public class PropertySenderControllerTests
{
    private static readonly Guid OrgA = Guid.Parse("6d5c4b3a-2918-4f7e-8d6c-5b4a39281702");
    private static readonly Guid PropertyOfOrgA = Guid.Parse("1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c11");
    private static readonly Guid PropertyOfOrgB = Guid.Parse("5e6f7a8b-9c0d-4e1f-a2b3-c4d5e6f7a812");
    private const string Owner = "auth0|owner-a";

    [Fact]
    public async Task PropertySender_RowLevelAuthorization_Test()
    {
        var senders = new Mock<IPropertySenderService>();
        senders
            .Setup(s => s.GetAsync(PropertyOfOrgA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(View(PropertyOfOrgA));
        senders
            .Setup(s => s.SwitchAsync(
                PropertyOfOrgA,
                It.IsAny<ComplianceSenderType>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<ComplianceSenderType?>()))
            .ReturnsAsync(View(PropertyOfOrgA));
        senders
            .Setup(s => s.TestConnectionAsync(PropertyOfOrgA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SenderConnectionTestResult(
                ComplianceSenderType.ChannelManager,
                SenderConnectionTestOutcome.Unverifiable,
                SenderReasonCodes.PoliceNotExposed,
                null,
                null,
                null,
                DateTime.UtcNow));
        senders
            .Setup(s => s.DeclareActivatedAsync(PropertyOfOrgA, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(View(PropertyOfOrgA));

        // The property of the org A, which belongs to its owner; the property of the org B is hidden by the tenant filter.
        var lookup = new Mock<IHostResourceLookup>();
        lookup
            .Setup(l => l.ForPropertyAsync(PropertyOfOrgA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(HostResource.ForProperty(new Property { Id = PropertyOfOrgA, OrgId = OrgA, OwnerId = Owner, Name = "Casa del Lago" }));
        lookup
            .Setup(l => l.ForPropertyAsync(PropertyOfOrgB, It.IsAny<CancellationToken>()))
            .ReturnsAsync((HostResource?)null);

        var change = new SwitchSenderRequest { Sender = ComplianceSenderType.Manual, Confirmed = true };

        // A member of the org with no org-wide role and no grant reaches only the properties it created: nothing of this one, no call.
        var outsider = Controller(senders, lookup, HostAuthorizationTestHarness.User("auth0|staff-a", "Staff"));
        Assert.IsType<ForbidResult>((await outsider.Get(PropertyOfOrgA, CancellationToken.None)).Result);
        Assert.IsType<ForbidResult>((await outsider.Switch(PropertyOfOrgA, change, CancellationToken.None)).Result);
        Assert.IsType<ForbidResult>((await outsider.TestConnection(PropertyOfOrgA, CancellationToken.None)).Result);
        Assert.IsType<ForbidResult>((await outsider.DeclareActivated(PropertyOfOrgA, CancellationToken.None)).Result);

        // Another organization's property is not found, even for a manager of the whole org A: the same 404 as a property that does not exist.
        var manager = Controller(senders, lookup, HostAuthorizationTestHarness.User("auth0|manager-a", "PropertyManager"));
        AssertNotFound((await manager.Get(PropertyOfOrgB, CancellationToken.None)).Result);
        AssertNotFound((await manager.Switch(PropertyOfOrgB, change, CancellationToken.None)).Result);
        AssertNotFound((await manager.TestConnection(PropertyOfOrgB, CancellationToken.None)).Result);
        AssertNotFound((await manager.DeclareActivated(PropertyOfOrgB, CancellationToken.None)).Result);

        Assert.Empty(senders.Invocations);

        // Whoever reaches the property reads and changes its sender, and the change is made in the name of the caller.
        var owner = Controller(senders, lookup, HostAuthorizationTestHarness.User(Owner, "PropertyOwner"));
        var read = Assert.IsType<OkObjectResult>((await owner.Get(PropertyOfOrgA, CancellationToken.None)).Result);
        Assert.Equal(PropertyOfOrgA, Assert.IsType<PropertySenderDto>(read.Value).PropertyId);
        Assert.IsType<OkObjectResult>((await owner.Switch(PropertyOfOrgA, change, CancellationToken.None)).Result);
        Assert.IsType<OkObjectResult>((await owner.TestConnection(PropertyOfOrgA, CancellationToken.None)).Result);
        Assert.IsType<OkObjectResult>((await owner.DeclareActivated(PropertyOfOrgA, CancellationToken.None)).Result);
        senders.Verify(
            s => s.SwitchAsync(
                PropertyOfOrgA,
                ComplianceSenderType.Manual,
                true,
                false,
                Owner,
                It.IsAny<CancellationToken>(),
                It.IsAny<ComplianceSenderType?>()),
            Times.Once);
        senders.Verify(s => s.DeclareActivatedAsync(PropertyOfOrgA, Owner, It.IsAny<CancellationToken>()), Times.Once);

        // The test says why it could not verify, in words, and never returns a credential: the answer carries the code and the guidance only.
        var test = Assert.IsType<OkObjectResult>((await owner.TestConnection(PropertyOfOrgA, CancellationToken.None)).Result);
        var dto = Assert.IsType<SenderConnectionTestDto>(test.Value);
        Assert.Equal((SenderConnectionTestOutcome.Unverifiable, SenderReasonCodes.PoliceNotExposed), (dto.Outcome, dto.Reason));
        Assert.False(string.IsNullOrWhiteSpace(dto.Guidance));
        Assert.NotEqual("SenderGuidancePoliceNotExposed", dto.Guidance);
    }

    private static PropertySenderView View(Guid propertyId) =>
        new(
            propertyId,
            ComplianceSenderType.Manual,
            ComplianceSenderType.Manual,
            SenderSource.Stored,
            true,
            SenderState.NotConfigured,
            SenderVerification.NotApplicable,
            null,
            null,
            null,
            null,
            false,
            true,
            [ComplianceSenderType.ChannelManager, ComplianceSenderType.Manual],
            true,
            AlloggiatiDelegationTexts.CurrentVersion,
            null,
            DateTime.UtcNow);

    private static void AssertNotFound(IActionResult? result)
    {
        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        Assert.Equal("property_not_found", Assert.IsAssignableFrom<ProblemDetails>(problem.Value).Extensions["code"]);
    }

    private static PropertySenderController Controller(
        Mock<IPropertySenderService> senders,
        Mock<IHostResourceLookup> lookup,
        ClaimsPrincipal user)
    {
        var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        return new PropertySenderController(
            senders.Object,
            lookup.Object,
            HostAuthorizationTestHarness.Create(OrgA),
            services.GetRequiredService<IStringLocalizer<SharedResources>>(),
            NullLogger<PropertySenderController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user, RequestServices = services },
            },
        };
    }
}
