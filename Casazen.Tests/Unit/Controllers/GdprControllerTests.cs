using System.Security.Claims;
using Casazen.Core.Services;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

public class GdprControllerTests
{
    private readonly Mock<IGdprService> _mockGdprService;
    private readonly Mock<IOrgContextResolver> _mockOrgContextResolver;
    private readonly Mock<IGuestAccessService> _mockGuestAccessService;
    private readonly Mock<IAuthorizationService> _mockAuthorization;
    private readonly GdprController _controller;
    private static readonly Guid OrgId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    public GdprControllerTests()
    {
        _mockGdprService = new Mock<IGdprService>();
        _mockOrgContextResolver = new Mock<IOrgContextResolver>();
        _mockGuestAccessService = new Mock<IGuestAccessService>();
        _mockAuthorization = new Mock<IAuthorizationService>();
        _controller = new GdprController(
            _mockGdprService.Object,
            _mockOrgContextResolver.Object,
            _mockGuestAccessService.Object,
            _mockAuthorization.Object,
            new Mock<ILogger<GdprController>>().Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        _mockOrgContextResolver
            .Setup(x => x.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(OrgId);
        Authorize(true);
    }

    [Fact]
    public async Task ExportGuestData_GuestNotInOrg_ReturnsNotFound()
    {
        var guestId = GuestAccessible(false);

        var result = await _controller.ExportGuestData(guestId);

        Assert.IsType<NotFoundResult>(result);
        _mockGdprService.Verify(
            x => x.ExportGuestDataAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExportGuestData_WithoutGuestReadOnTheOrg_ReturnsForbidAndExportsNothing()
    {
        var guestId = GuestAccessible(true);
        Authorize(false);

        var result = await _controller.ExportGuestData(guestId);

        Assert.IsType<ForbidResult>(result);
        _mockGdprService.Verify(
            x => x.ExportGuestDataAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExportGuestData_GuestInOrg_ReturnsOkNeverCached()
    {
        var guestId = GuestAccessible(true);

        var result = await _controller.ExportGuestData(guestId);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("private, no-store", _controller.Response.Headers.CacheControl.ToString());
        _mockGdprService.Verify(
            x => x.ExportGuestDataAsync(OrgId, guestId, It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DeleteGuestData_GuestNotInOrg_ReturnsNotFound()
    {
        var guestId = GuestAccessible(false);

        var result = await _controller.DeleteGuestData(guestId);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task AnonymizeGuestData_GuestInOrg_ReturnsNoContent()
    {
        var guestId = GuestAccessible(true);

        var result = await _controller.AnonymizeGuestData(guestId);

        Assert.IsType<NoContentResult>(result);
        _mockGdprService.Verify(
            x => x.AnonymizeGuestDataAsync(OrgId, guestId, It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdateConsent_WithdrawalWithNote_ForwardsTheNoteToTheService()
    {
        var guestId = GuestAccessible(true);

        var result = await _controller.UpdateConsent(guestId, new UpdateConsentRequest(false, "Email del 02/09"));

        Assert.IsType<NoContentResult>(result);
        _mockGdprService.Verify(
            x => x.UpdateMarketingConsentAsync(OrgId, guestId, false, "Email del 02/09", It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private Guid GuestAccessible(bool accessible)
    {
        var guestId = Guid.NewGuid();
        _mockGuestAccessService
            .Setup(x => x.IsGuestAccessibleAsync(guestId, OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(accessible);
        return guestId;
    }

    private void Authorize(bool allowed) =>
        _mockAuthorization
            .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<IEnumerable<IAuthorizationRequirement>>()))
            .ReturnsAsync(allowed ? AuthorizationResult.Success() : AuthorizationResult.Failed());
}
