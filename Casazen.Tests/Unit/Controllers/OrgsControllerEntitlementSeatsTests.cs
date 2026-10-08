using System.Security.Claims;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>AM-02: the entitlement of the org also says how many people the plan allows and how many it has.</summary>
public class OrgsControllerEntitlementSeatsTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");

    private static OrgsController Build(OrgSeatUsage seats)
    {
        var orgResolver = new Mock<IOrgContextResolver>();
        orgResolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        var entitlement = new Mock<IEntitlementService>();
        entitlement.Setup(e => e.GetEntitlementAsync(OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntitlementResult(OrgId, "Pro", 50, 4, true));
        entitlement.Setup(e => e.CanUseCustomDomainAsync(OrgId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var seatService = new Mock<IOrgSeatService>();
        seatService.Setup(s => s.GetUsageAsync(OrgId, It.IsAny<CancellationToken>())).ReturnsAsync(seats);

        var controller = new OrgsController(
            orgResolver.Object,
            entitlement.Object,
            seatService.Object,
            Mock.Of<IOrgService>(),
            Mock.Of<IPublicHostResolver>(),
            Options.Create(new PublicHostOptions()));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "auth0|owner")], "TestAuth")),
                RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
            },
        };
        return controller;
    }

    [Fact]
    public async Task GetMyEntitlement_SaysTheSeatsOfThePlanAndTheOnesInUse()
    {
        var controller = Build(new OrgSeatUsage(Max: 10, ActiveMembers: 4, PendingInvitations: 1));

        var result = await controller.GetMyEntitlement(CancellationToken.None);

        var dto = Assert.IsType<EntitlementDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal((50, 10), (dto.Limits.MaxProperties, dto.Limits.MaxSeats));
        Assert.Equal((4, 5), (dto.Usage.Properties, dto.Usage.Seats));
        Assert.True(dto.CanInviteMember);
        Assert.True(dto.CanAddProperty);
        Assert.True(dto.CanUseCustomDomain);
    }

    [Fact]
    public async Task GetMyEntitlement_WithTheSeatsFull_CannotInviteAnyoneElse()
    {
        var controller = Build(new OrgSeatUsage(Max: 2, ActiveMembers: 2, PendingInvitations: 0));

        var result = await controller.GetMyEntitlement(CancellationToken.None);

        var dto = Assert.IsType<EntitlementDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(dto.CanInviteMember);
        Assert.Equal((2, 2), (dto.Limits.MaxSeats, dto.Usage.Seats));
    }

    [Fact]
    public async Task GetMyEntitlement_AnUnlimitedPlan_ReportsTheIntMaxLikeTheProperties()
    {
        var controller = Build(new OrgSeatUsage(Max: int.MaxValue, ActiveMembers: 30, PendingInvitations: 2));

        var result = await controller.GetMyEntitlement(CancellationToken.None);

        var dto = Assert.IsType<EntitlementDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(int.MaxValue, dto.Limits.MaxSeats);
        Assert.True(dto.CanInviteMember);
    }
}
