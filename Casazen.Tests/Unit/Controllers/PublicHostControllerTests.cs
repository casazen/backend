using Casazen.Core.DTOs;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

public class PublicHostControllerTests
{
    private readonly Mock<IPublicHostResolver> _resolver = new();
    private readonly PublicHostController _controller;

    public PublicHostControllerTests()
    {
        _controller = new PublicHostController(_resolver.Object);
    }

    [Fact]
    public async Task ResolveHost_WhenFound_Returns200()
    {
        var dto = new ResolveHostResponseDto
        {
            OrgId = Guid.NewGuid(),
            Slug = "villa-mare",
            PublicHostMode = PublicHostMode.CasazenSubdomain,
            Branding = new PublicOrgDto { Slug = "villa-mare", DisplayName = "Villa Mare" },
        };
        _resolver.Setup(r => r.ResolveAsync("villa-mare.casazen.it", It.IsAny<CancellationToken>()))
            .ReturnsAsync(dto);

        var result = await _controller.ResolveHost("villa-mare.casazen.it", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ResolveHostResponseDto>(ok.Value);
        Assert.Equal("villa-mare", body.Slug);
    }

    [Fact]
    public async Task ResolveHost_WhenUnknown_Returns404()
    {
        _resolver.Setup(r => r.ResolveAsync("missing.casazen.it", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolveHostResponseDto?)null);

        var result = await _controller.ResolveHost("missing.casazen.it", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }
}
