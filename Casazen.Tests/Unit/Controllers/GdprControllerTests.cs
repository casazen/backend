using Casazen.Core.Services;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
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
    private readonly GdprController _controller;
    private static readonly Guid OrgId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    public GdprControllerTests()
    {
        _mockGdprService = new Mock<IGdprService>();
        _mockOrgContextResolver = new Mock<IOrgContextResolver>();
        _mockGuestAccessService = new Mock<IGuestAccessService>();
        _controller = new GdprController(
            _mockGdprService.Object,
            _mockOrgContextResolver.Object,
            _mockGuestAccessService.Object,
            new Mock<ILogger<GdprController>>().Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        _mockOrgContextResolver
            .Setup(x => x.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(OrgId);
    }

    [Fact]
    public async Task ExportGuestData_GuestNotInOrg_ReturnsNotFound()
    {
        var guestId = Guid.NewGuid();
        _mockGuestAccessService
            .Setup(x => x.IsGuestAccessibleAsync(guestId, OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _controller.ExportGuestData(guestId);

        Assert.IsType<NotFoundResult>(result);
        _mockGdprService.Verify(x => x.ExportGuestDataAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task ExportGuestData_GuestInOrg_ReturnsOk()
    {
        var guestId = Guid.NewGuid();
        _mockGuestAccessService
            .Setup(x => x.IsGuestAccessibleAsync(guestId, OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _mockGdprService
            .Setup(x => x.ExportGuestDataAsync(guestId))
            .ReturnsAsync(new Dictionary<string, object> { ["guestId"] = guestId });

        var result = await _controller.ExportGuestData(guestId);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task DeleteGuestData_GuestNotInOrg_ReturnsNotFound()
    {
        var guestId = Guid.NewGuid();
        _mockGuestAccessService
            .Setup(x => x.IsGuestAccessibleAsync(guestId, OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _controller.DeleteGuestData(guestId);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task AnonymizeGuestData_GuestInOrg_ReturnsNoContent()
    {
        var guestId = Guid.NewGuid();
        _mockGuestAccessService
            .Setup(x => x.IsGuestAccessibleAsync(guestId, OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _controller.AnonymizeGuestData(guestId);

        Assert.IsType<NoContentResult>(result);
        _mockGdprService.Verify(x => x.AnonymizeGuestDataAsync(guestId), Times.Once);
    }
}
