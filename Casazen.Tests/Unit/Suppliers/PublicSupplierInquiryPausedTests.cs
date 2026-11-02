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

/// <summary>
/// SP-10b-a x SP-13a: a showcase the supplier paused takes no new request on measure (422 <c>supplier_booking_offline</c>, the answer of
/// its slots and its estimate) and nothing is stored or sent; a showcase that is not paused takes it as always.
/// </summary>
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

        // Not paused: the request is taken as always.
        showcase.Setup(s => s.IsPausedAsync(supplier, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var created = await controller.Create("fornitore", new CreatePublicSupplierInquiryRequest(), CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<ObjectResult>(created.Result).StatusCode);
        inquiries.Verify(
            i => i.CreateAsync(supplier, It.IsAny<ShowcaseInquiryInput>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
