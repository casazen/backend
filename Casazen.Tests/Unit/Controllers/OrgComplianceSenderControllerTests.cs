using System.Security.Claims;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

public class OrgComplianceSenderControllerTests
{
    [Fact]
    public async Task RequiresConfirmationAndCurrentDelegation_AndRejectsStaleUpdates()
    {
        await using var db = OrgTeamTestData.NewDb();
        var org = OrgTeamTestData.AddOrg(db);
        var otherOrg = OrgTeamTestData.AddOrg(db);
        otherOrg.DefaultComplianceSender = ComplianceSenderType.Manual;
        await db.SaveChangesAsync();
        var resolver = new Mock<IOrgContextResolver>();
        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(org.Id);
        var delegated = new Mock<IAlloggiatiDelegationService>();
        delegated.Setup(d => d.GetAsync(org.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new AlloggiatiDelegationInfo(false, null, null, null, null));
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var controller = new OrgComplianceSenderController(db, resolver.Object, delegated.Object, TimeProvider.System, NullLogger<OrgComplianceSenderController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services, User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "auth0|host")], "test")) } },
        };
        var request = new OrgComplianceSenderRequest { Sender = ComplianceSenderType.ChannelManager };
        Assert.Equal(409, Assert.IsType<ObjectResult>((await controller.Put(request, CancellationToken.None)).Result).StatusCode);
        Assert.Null(org.DefaultComplianceSender);
        request.Confirmed = true;
        Assert.Equal(403, Assert.IsType<ObjectResult>((await controller.Put(request, CancellationToken.None)).Result).StatusCode);
        Assert.Null(org.DefaultComplianceSender);
        request.Sender = ComplianceSenderType.Manual;
        Assert.IsType<OkObjectResult>((await controller.Put(request, CancellationToken.None)).Result);
        Assert.Equal(ComplianceSenderType.Manual, org.DefaultComplianceSender);
        Assert.NotNull(org.DefaultComplianceSenderChangedAt);
        Assert.Equal(ComplianceSenderType.Manual, otherOrg.DefaultComplianceSender);
        Assert.Equal(409, Assert.IsType<ObjectResult>((await controller.Put(request, CancellationToken.None)).Result).StatusCode);
        delegated.Verify(d => d.GrantAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void RestrictedToOrganizationAdministrators()
    {
        var policy = Assert.Single(typeof(OrgComplianceSenderController).GetCustomAttributes(typeof(AuthorizeAttribute), false).Cast<AuthorizeAttribute>());
        Assert.Equal(CasazenPolicies.OrgBillingAdmin, policy.Policy);
    }
}
