using Casazen.Core.Authorization;
using Casazen.Core.Entities;
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

public class BookingLifecycleControllerEditTests
{
    private const string OwnerId = "auth0|owner_1";
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();

    private readonly Mock<IBookingService> _bookings = new();
    private readonly Mock<IHostBookingService> _hostBookings = new();
    private readonly Mock<IHostResourceLookup> _hostResources = new();
    private readonly Booking _booking = new()
    {
        Id = Guid.NewGuid(),
        PropertyId = PropertyId,
        OrgId = OrgId,
        Status = BookingStatus.Confirmed,
        Source = BookingSource.Direct,
    };
    private Func<string, string, bool>? _permissions;

    public BookingLifecycleControllerEditTests()
    {
        _bookings.Setup(b => b.GetBookingAsync(_booking.Id)).ReturnsAsync(_booking);
        _hostResources.Setup(h => h.ForPropertyAsync(PropertyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostResource(OrgId, OwnerId));
        _hostBookings
            .Setup(h => h.UpdateAsync(It.IsAny<HostBookingUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HostBookingUpdateResult(_booking, BookingEditSettlement.NotNeeded("EUR")));
    }

    [Fact]
    public async Task Update_ThatMovesMoney_WithoutPaymentWrite_Is403_AndNothingIsDone_Test()
    {
        GivenPlan(BookingSettlementAction.Refund);
        _permissions = (_, permission) => permission != "payment.write";

        var result = await Controller().Update(_booking.Id, Request(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        _hostBookings.Verify(h => h.UpdateAsync(It.IsAny<HostBookingUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(BookingSettlementAction.ChargeSavedCard)]
    [InlineData(BookingSettlementAction.PaymentLink)]
    public async Task Update_ThatChargesOrAsksForMoney_WithoutPaymentWrite_Is403(BookingSettlementAction action)
    {
        GivenPlan(action);
        _permissions = (_, permission) => permission != "payment.write";

        var result = await Controller().Update(_booking.Id, Request(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        _hostBookings.Verify(h => h.UpdateAsync(It.IsAny<HostBookingUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_ThatMovesNoMoney_WithoutPaymentWrite_GoesOn_WithoutTheRightToMoveMoney()
    {
        GivenPlan(BookingSettlementAction.None);
        _permissions = (_, permission) => permission != "payment.write";

        var result = await Controller().Update(_booking.Id, Request(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);

        _hostBookings.Verify(
            h => h.UpdateAsync(It.Is<HostBookingUpdate>(u => u.BookingId == _booking.Id && !u.CanMoveMoney && u.RequestedByUserId == OwnerId), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Update_WithPaymentWrite_PassesTheRightToMoveMoney_AndAsksNoQuote()
    {
        var result = await Controller().Update(_booking.Id, Request(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        _hostBookings.Verify(
            h => h.UpdateAsync(It.Is<HostBookingUpdate>(u => u.CanMoveMoney), It.IsAny<CancellationToken>()),
            Times.Once);
        _hostBookings.Verify(h => h.QuoteEditAsync(It.IsAny<HostBookingUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task QuoteEdit_WithoutPaymentRead_Is403_AndNeverQuotes_Test()
    {
        GivenPlan(BookingSettlementAction.Refund);
        _permissions = (_, permission) => permission != "payment.read";

        var result = await Controller().QuoteEdit(_booking.Id, Request(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        _hostBookings.Verify(h => h.QuoteEditAsync(It.IsAny<HostBookingUpdate>(), It.IsAny<CancellationToken>()), Times.Never);

        _permissions = null;
        var allowed = await Controller().QuoteEdit(_booking.Id, Request(), CancellationToken.None);
        var body = Assert.IsType<BookingEditQuoteDto>(Assert.IsType<OkObjectResult>(allowed.Result).Value);
        Assert.Equal(("refund", 100m), (body.Action, body.Amount));
    }

    [Fact]
    public async Task QuoteEdit_OfABookingOutOfTheCallersReach_IsNotFound_BeforeAnyPermissionOfMoney()
    {
        _permissions = (_, permission) => permission != "booking.write";

        var result = await Controller().QuoteEdit(_booking.Id, Request(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        _hostBookings.Verify(h => h.QuoteEditAsync(It.IsAny<HostBookingUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static UpdateBookingRequest Request() => new()
    {
        CheckInDate = new DateTime(2026, 12, 10, 0, 0, 0, DateTimeKind.Utc),
        CheckOutDate = new DateTime(2026, 12, 13, 0, 0, 0, DateTimeKind.Utc),
        NumberOfGuests = 1,
        NumberOfChildren = 0,
    };

    private void GivenPlan(BookingSettlementAction action) =>
        _hostBookings
            .Setup(h => h.QuoteEditAsync(It.IsAny<HostBookingUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingEditQuote(
                "EUR",
                400m,
                300m,
                400m,
                -100m,
                action,
                action == BookingSettlementAction.None ? 0m : 100m,
                0m));

    private BookingLifecycleController Controller()
    {
        var controller = new BookingLifecycleController(
            _bookings.Object,
            _hostBookings.Object,
            Mock.Of<IStayLifecycleService>(),
            Mock.Of<IComplianceWizardService>(),
            Mock.Of<IPropertyService>(),
            _hostResources.Object,
            HostAuthorizationTestHarness.Create(OrgId, (c, p) => _permissions?.Invoke(c, p) ?? true),
            Mock.Of<IStringLocalizer<SharedResources>>(),
            NullLogger<BookingLifecycleController>.Instance,
            OtaChannelActionPolicy.Closed);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = HostAuthorizationTestHarness.User(OwnerId),
                RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
            },
        };
        return controller;
    }
}
