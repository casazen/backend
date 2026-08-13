using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

public class PaymentsControllerTests
{
    private const string OwnerId = "auth0|owner_123";
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly Guid OrgId = Guid.NewGuid();

    private readonly Mock<IPaymentService> _paymentService = new();
    private readonly Mock<IBookingService> _bookingService = new();
    private readonly Mock<IPropertyAuthorizationService> _authz = new();
    private readonly Mock<IFiscalRegimeService> _fiscal = new();
    private readonly PaymentsController _controller;

    public PaymentsControllerTests()
    {
        _fiscal.Setup(f => f.ApplyWithholdingOnCreateAsync(
                It.IsAny<Payment>(), It.IsAny<Booking>(), It.IsAny<bool?>(), It.IsAny<decimal?>()))
            .Returns(Task.CompletedTask);
        _controller = new PaymentsController(
            _paymentService.Object,
            _bookingService.Object,
            _authz.Object,
            _fiscal.Object,
            Mock.Of<ILogger<PaymentsController>>());

        SetUser(OwnerId);
    }

    [Fact]
    public async Task GetAll_WithPropertyId_WhenUnauthorized_ReturnsNotFound()
    {
        _authz.Setup(a => a.CanAccessPropertyAsync(OwnerId, PropertyId, It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(false);

        var result = await _controller.GetAll(PropertyId);

        Assert.IsType<NotFoundResult>(result.Result);
        _paymentService.Verify(p => p.GetPropertyPaymentsAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task GetAll_WithoutPropertyId_FiltersOutUnauthorizedPayments()
    {
        var visiblePayment = MakePayment(PropertyId);
        var hiddenPropertyId = Guid.NewGuid();
        var hiddenPayment = MakePayment(hiddenPropertyId);

        _paymentService.Setup(p => p.GetAllPaymentsAsync())
            .ReturnsAsync([visiblePayment, hiddenPayment]);
        _authz.Setup(a => a.CanAccessPropertyAsync(OwnerId, PropertyId, It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(true);
        _authz.Setup(a => a.CanAccessPropertyAsync(OwnerId, hiddenPropertyId, It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(false);

        var result = await _controller.GetAll(null);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payments = Assert.IsAssignableFrom<IEnumerable<Payment>>(ok.Value);
        var payment = Assert.Single(payments);
        Assert.Equal(visiblePayment.Id, payment.Id);
    }

    [Fact]
    public async Task Refund_WhenPaymentBelongsToUnauthorizedProperty_ReturnsNotFound()
    {
        var payment = MakePayment(PropertyId);
        _paymentService.Setup(p => p.GetPaymentAsync(payment.Id)).ReturnsAsync(payment);
        _authz.Setup(a => a.CanAccessPropertyAsync(OwnerId, PropertyId, It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(false);

        var result = await _controller.Refund(payment.Id);

        Assert.IsType<NotFoundResult>(result);
        _paymentService.Verify(p => p.RefundPaymentAsync(It.IsAny<Guid>(), It.IsAny<decimal?>()), Times.Never);
    }

    [Fact]
    public async Task Create_WhenAuthorized_DerivesOrgIdFromBooking()
    {
        var booking = MakeBooking(PropertyId);
        _bookingService.Setup(b => b.GetBookingAsync(booking.Id)).ReturnsAsync(booking);
        _authz.Setup(a => a.CanAccessPropertyAsync(OwnerId, PropertyId, It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(true);
        _paymentService.Setup(p => p.CreatePaymentAsync(It.IsAny<Payment>()))
            .ReturnsAsync((Payment p) => p);

        var result = await _controller.Create(new CreatePaymentRequest(
            booking.Id, 100m, PaymentMethod.CreditCard, null, null, null));

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var createdPayment = Assert.IsType<Payment>(created.Value);
        Assert.Equal(OrgId, createdPayment.OrgId);
    }

    private void SetUser(string userId)
    {
        var identity = new ClaimsIdentity([new Claim("sub", userId)], "TestAuth");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    private static Booking MakeBooking(Guid propertyId) => new()
    {
        Id = Guid.NewGuid(),
        PropertyId = propertyId,
        OrgId = OrgId,
        CheckInDate = DateTime.UtcNow.Date,
        CheckOutDate = DateTime.UtcNow.Date.AddDays(1),
        NumberOfGuests = 2,
        Status = BookingStatus.Confirmed,
        Source = BookingSource.Direct,
    };

    private static Payment MakePayment(Guid propertyId)
    {
        var booking = MakeBooking(propertyId);
        return new Payment
        {
            Id = Guid.NewGuid(),
            BookingId = booking.Id,
            Booking = booking,
            OrgId = booking.OrgId,
            Amount = 100m,
            Status = PaymentStatus.Completed,
        };
    }
}
