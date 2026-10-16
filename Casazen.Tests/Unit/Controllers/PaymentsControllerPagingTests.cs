using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Payments;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// SR-03b: <c>GET /api/payments</c> a page at a time, and still the array it always was without <c>page</c> and <c>pageSize</c>
/// (the web app reads that today). The pages are the payment list service's; here what the endpoint decides.
/// </summary>
public class PaymentsControllerPagingTests
{
    private const string OwnerId = "auth0|owner_123";
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly Guid OrgId = Guid.NewGuid();

    private readonly Mock<IPaymentListService> _paymentList = new();
    private readonly Mock<IOrgContextResolver> _orgResolver = new();
    private readonly Mock<IHostResourceLookup> _hostResources = new();

    public PaymentsControllerPagingTests()
    {
        _orgResolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        _hostResources.Setup(h => h.ForPropertyAsync(PropertyId, It.IsAny<CancellationToken>())).ReturnsAsync(new HostResource(OrgId, OwnerId));
    }

    private PaymentsController CreateController() => new(
        Mock.Of<IPaymentService>(),
        _paymentList.Object,
        Mock.Of<IBookingService>(),
        _hostResources.Object,
        HostAuthorizationTestHarness.Create(OrgId),
        _orgResolver.Object,
        HostAuthorizationTestHarness.ScopeResolver(),
        Mock.Of<IFiscalRegimeService>(),
        Mock.Of<IPaymentRefundService>(),
        Mock.Of<ILogger<PaymentsController>>())
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = HostAuthorizationTestHarness.User(OwnerId),
                RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
            },
        },
    };

    private static PaymentListItem Item(decimal amount) => new(
        Guid.NewGuid(), Guid.NewGuid(), "ABCDEFGHJK", PropertyId, "Trullo", "Anna Verdi", amount, 0m, PaymentStatus.Completed,
        PaymentMethod.CreditCard, "tx_1", string.Empty, null, null, 0m, false, 0m, WithholdingSource.None,
        new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task WithoutAPage_TheAnswerIsTheArrayOfAlways_AllThePayments_AndThePagedListIsNeverAsked()
    {
        _paymentList
            .Setup(p => p.ListAsync(It.IsAny<HostScope>(), It.IsAny<PaymentListCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Item(10m), Item(20m)]);

        var result = await CreateController().GetAll(null);

        var rows = Assert.IsAssignableFrom<IEnumerable<PaymentListItemDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal([10m, 20m], rows.Select(r => r.Amount));
        _paymentList.Verify(
            p => p.ListPageAsync(It.IsAny<HostScope>(), It.IsAny<PaymentListCriteria>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(2, 25, 2, 25)]
    [InlineData(null, 5, 1, 5)]
    [InlineData(3, null, 3, PaymentListPage.DefaultPageSize)]
    public async Task WithAPageOrASize_TheAnswerIsOnePage_LikeTheBookings(int? page, int? pageSize, int expectedPage, int expectedSize)
    {
        _paymentList
            .Setup(p => p.ListPageAsync(It.IsAny<HostScope>(), It.IsAny<PaymentListCriteria>(), expectedPage, expectedSize, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentListPage([Item(10m), Item(20m)], 52, expectedPage, expectedSize));

        var result = await CreateController().GetAll(null, page: page, pageSize: pageSize);

        var paged = Assert.IsType<PagedResultDto<PaymentListItemDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal((52, expectedPage, expectedSize), (paged.TotalCount, paged.Page, paged.PageSize));
        Assert.Equal([10m, 20m], paged.Items.Select(r => r.Amount));
        _paymentList.Verify(
            p => p.ListAsync(It.IsAny<HostScope>(), It.IsAny<PaymentListCriteria>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task APage_KeepsTheCriteriaAndTheScopeOfTheCaller()
    {
        _paymentList
            .Setup(p => p.ListPageAsync(It.IsAny<HostScope>(), It.IsAny<PaymentListCriteria>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentListPage([], 0, 1, 20));

        await CreateController().GetAll(PropertyId, null, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), page: 1);

        _paymentList.Verify(
            p => p.ListPageAsync(
                new HostScope(OrgId, OwnerId),
                new PaymentListCriteria(PropertyId, null, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)),
                1,
                PaymentListPage.DefaultPageSize,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task APageOfAnotherOrgsProperty_IsNotFound_AndAPageWithABadRangeIs400_BeforeAnythingIsListed()
    {
        _hostResources.Setup(h => h.ForPropertyAsync(PropertyId, It.IsAny<CancellationToken>())).ReturnsAsync((HostResource?)null);

        var notFound = await CreateController().GetAll(PropertyId, page: 1);
        var badRange = await CreateController().GetAll(null, null, new DateOnly(2026, 11, 1), new DateOnly(2026, 10, 1), page: 1);

        Assert.IsType<NotFoundResult>(notFound.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<ObjectResult>(badRange.Result).StatusCode);
        _paymentList.Verify(
            p => p.ListPageAsync(It.IsAny<HostScope>(), It.IsAny<PaymentListCriteria>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
