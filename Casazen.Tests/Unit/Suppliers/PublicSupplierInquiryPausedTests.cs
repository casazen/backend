using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

public class PublicSupplierInquiryPausedTests
{
    [Fact]
    public async Task APausedShowcase_TakesNoRequestOnMeasure_AndStoresNothing_AnotherDoes()
    {
        var supplier = new SupplierProfile { OrgId = Guid.NewGuid(), Status = SupplierStatus.Active, ShowcaseSlug = "fornitore" };
        var showcase = new Mock<IPublicSupplierShowcaseService>();
        showcase.Setup(s => s.FindActiveSupplierAsync("fornitore", It.IsAny<CancellationToken>())).ReturnsAsync(supplier);
        showcase.Setup(s => s.IsPausedAsync(supplier, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var inquiries = new Mock<IShowcaseInquiryService>();
        inquiries
            .Setup(i => i.CreateAsync(supplier, It.IsAny<ShowcaseInquiryInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShowcaseInquiryResult(Guid.NewGuid(), DateTime.UtcNow.AddMinutes(30)));
        using var limiter = new SupplierBookingEmailRateLimiter(new ConfigurationBuilder().Build());
        var controller = new PublicSupplierInquiryController(
            showcase.Object, inquiries.Object, limiter, TimeProvider.System, Options.Create(new ShowcaseBookingOptions()))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var refused = await Assert.ThrowsAsync<DomainRuleException>(
            () => controller.Create("fornitore", new CreatePublicSupplierInquiryRequest(), CancellationToken.None));

        Assert.Equal(ShowcaseBookingErrors.Offline, refused.Code);
        inquiries.Verify(
            i => i.CreateAsync(It.IsAny<SupplierProfile>(), It.IsAny<ShowcaseInquiryInput>(), It.IsAny<CancellationToken>()), Times.Never);

        showcase.Setup(s => s.IsPausedAsync(supplier, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var created = await controller.Create("fornitore", new CreatePublicSupplierInquiryRequest(), CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<ObjectResult>(created.Result).StatusCode);
        inquiries.Verify(
            i => i.CreateAsync(supplier, It.IsAny<ShowcaseInquiryInput>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
