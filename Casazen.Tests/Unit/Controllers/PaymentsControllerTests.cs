using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Controllers;
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
/// Payments are authorized as host resources (TN-3, A3-38): org, <c>payment.*</c> permission and property ownership,
/// with the org-wide list filtered in SQL through a <see cref="HostScope"/>.
/// </summary>
public class PaymentsControllerTests
{
    private const string OwnerId = "auth0|owner_123";
    private const string OtherOwnerId = "auth0|other_owner";
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly Guid OrgId = Guid.NewGuid();

    private readonly Mock<IPaymentService> _paymentService = new();
    private readonly Mock<IPaymentListService> _paymentList = new();
    private readonly Mock<IBookingService> _bookingService = new();
    private readonly Mock<IHostResourceLookup> _hostResources = new();
    private readonly Mock<IOrgContextResolver> _orgResolver = new();
    private readonly Mock<IFiscalRegimeService> _fiscal = new();
    private readonly Mock<IPaymentRefundService> _refundService = new();
    private Func<string, string, bool>? _permissions;

    public PaymentsControllerTests()
    {
        _fiscal.Setup(f => f.ApplyWithholdingOnCreateAsync(
                It.IsAny<Payment>(), It.IsAny<Booking>(), It.IsAny<bool?>(), It.IsAny<decimal?>()))
            .Returns(Task.CompletedTask);
        _orgResolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        _hostResources.Setup(h => h.ForPropertyAsync(PropertyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostResource(OrgId, OwnerId));
    }

    [Fact]
    public async Task GetAll_WithPropertyIdOfAnotherOwner_ReturnsNotFound()
    {
        _hostResources.Setup(h => h.ForPropertyAsync(PropertyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostResource(OrgId, OtherOwnerId));

        var result = await CreateController().GetAll(PropertyId);

        Assert.IsType<NotFoundResult>(result.Result);
        _paymentList.Verify(
            p => p.ListAsync(It.IsAny<HostScope>(), It.IsAny<PaymentListCriteria>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetAll_WithPropertyIdNotVisible_ReturnsNotFound()
    {
        var hidden = Guid.NewGuid();
        _hostResources.Setup(h => h.ForPropertyAsync(hidden, It.IsAny<CancellationToken>()))
            .ReturnsAsync((HostResource?)null);

        var result = await CreateController().GetAll(hidden);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetAll_WithoutPropertyId_QueriesOwnerScopeInSql()
    {
        var visible = MakeListItem();
        _paymentList
            .Setup(p => p.ListAsync(new HostScope(OrgId, OwnerId), It.IsAny<PaymentListCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([visible]);

        var result = await CreateController().GetAll(null);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(visible.Id, Assert.Single(Assert.IsAssignableFrom<IEnumerable<PaymentListItemDto>>(ok.Value)).Id);
        _paymentList.Verify(
            p => p.ListAsync(It.Is<HostScope>(s => s != new HostScope(OrgId, OwnerId)), It.IsAny<PaymentListCriteria>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _hostResources.Verify(h => h.ForPropertyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetAll_WithoutPropertyId_AsPropertyManager_QueriesWholeOrg()
    {
        _paymentList
            .Setup(p => p.ListAsync(new HostScope(OrgId, null), It.IsAny<PaymentListCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await CreateController(OwnerId, "PropertyManager").GetAll(null);

        Assert.IsType<OkObjectResult>(result.Result);
        _paymentList.Verify(
            p => p.ListAsync(new HostScope(OrgId, null), It.IsAny<PaymentListCriteria>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // SR-03: a booking the caller may not read the payments of answers 404, like a missing one, and nothing is listed.
    [Fact]
    public async Task GetAll_WithBookingIdOfAnotherOwner_ReturnsNotFound()
    {
        var bookingId = Guid.NewGuid();
        _hostResources.Setup(h => h.ForBookingAsync(bookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostResource(OrgId, OtherOwnerId, PropertyId));

        var result = await CreateController().GetAll(null, bookingId);

        Assert.IsType<NotFoundResult>(result.Result);
        _paymentList.Verify(
            p => p.ListAsync(It.IsAny<HostScope>(), It.IsAny<PaymentListCriteria>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetAll_WithBookingIdNotVisible_ReturnsNotFound()
    {
        var bookingId = Guid.NewGuid();
        _hostResources.Setup(h => h.ForBookingAsync(bookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((HostResource?)null);

        var result = await CreateController().GetAll(null, bookingId);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetAll_WithBookingIdAndPeriod_PassesTheCriteriaAndTheScopeToTheList()
    {
        var bookingId = Guid.NewGuid();
        _hostResources.Setup(h => h.ForBookingAsync(bookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostResource(OrgId, OwnerId, PropertyId));
        var item = MakeListItem();
        _paymentList
            .Setup(p => p.ListAsync(It.IsAny<HostScope>(), It.IsAny<PaymentListCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);

        var result = await CreateController().GetAll(
            null, bookingId, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));

        var dto = Assert.Single(Assert.IsAssignableFrom<IEnumerable<PaymentListItemDto>>(Assert.IsType<OkObjectResult>(result.Result).Value));
        Assert.Equal(item.Id, dto.Id);
        Assert.Equal("Anna Verdi", dto.GuestName);
        Assert.Equal("Trullo", dto.PropertyName);
        Assert.Equal("ABCDE-FGHJK", dto.BookingCode);
        Assert.Equal(10_000, dto.AmountCents);
        _paymentList.Verify(
            p => p.ListAsync(
                new HostScope(OrgId, OwnerId),
                new PaymentListCriteria(null, bookingId, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetAll_FromAfterTo_Returns400WithAStableCode_AndListsNothing()
    {
        var result = await CreateController().GetAll(null, null, new DateOnly(2026, 11, 1), new DateOnly(2026, 10, 1));

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Equal(PaymentsController.InvalidRangeCode, Assert.IsAssignableFrom<ProblemDetails>(problem.Value).Extensions["code"]);
        _paymentList.Verify(
            p => p.ListAsync(It.IsAny<HostScope>(), It.IsAny<PaymentListCriteria>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetById_PaymentOfAnotherOrg_ReturnsNotFound()
    {
        var payment = MakePayment(PropertyId, orgId: Guid.NewGuid());
        _paymentService.Setup(p => p.GetPaymentAsync(payment.Id)).ReturnsAsync(payment);

        var result = await CreateController().GetById(payment.Id);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Refund_WhenPaymentBelongsToAnotherOwnersProperty_ReturnsNotFound()
    {
        var payment = MakePayment(PropertyId);
        _paymentService.Setup(p => p.GetPaymentAsync(payment.Id)).ReturnsAsync(payment);
        _hostResources.Setup(h => h.ForPropertyAsync(PropertyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostResource(OrgId, OtherOwnerId));

        var result = await CreateController().Refund(payment.Id, new RefundPaymentRequest { Amount = 10m });

        Assert.IsType<NotFoundResult>(result.Result);
        _refundService.Verify(
            r => r.RefundAsync(It.IsAny<PaymentRefundRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Refund_WhenCallerLacksPaymentWrite_DoesNotRefund()
    {
        var payment = MakePayment(PropertyId);
        _paymentService.Setup(p => p.GetPaymentAsync(payment.Id)).ReturnsAsync(payment);
        _permissions = (_, permission) => permission != "payment.write";

        var result = await CreateController().Refund(payment.Id);

        Assert.IsType<NotFoundResult>(result.Result);
        _refundService.Verify(
            r => r.RefundAsync(It.IsAny<PaymentRefundRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Refund_WhenAuthorized_ReturnsRefundAsStripeLeftIt()
    {
        var payment = MakePayment(PropertyId);
        _paymentService.Setup(p => p.GetPaymentAsync(payment.Id)).ReturnsAsync(payment);
        var pending = new PaymentRefund
        {
            PaymentId = payment.Id,
            OrgId = OrgId,
            Amount = 40m,
            Status = PaymentRefundStatus.Pending,
        };
        _refundService
            .Setup(r => r.RefundAsync(It.IsAny<PaymentRefundRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pending);

        var result = await CreateController().Refund(payment.Id, new RefundPaymentRequest { Amount = 40m, Reason = "Guasto" });

        var dto = Assert.IsType<PaymentRefundDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(PaymentRefundStatus.Pending, dto.Status);
        _refundService.Verify(r => r.RefundAsync(
            It.Is<PaymentRefundRequest>(q => q.PaymentId == payment.Id && q.Amount == 40m && q.Reason == "Guasto" && q.RequestedByUserId == OwnerId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Controller_HasNoProcessPaymentAction()
    {
        // A9-15: "process" marked a payment Completed without Stripe; payments are collected only through Stripe.
        Assert.Null(typeof(PaymentsController).GetMethod("Process"));
    }

    [Fact]
    public async Task Create_WhenAuthorized_DerivesOrgIdFromBooking()
    {
        var booking = MakeBooking(PropertyId);
        _bookingService.Setup(b => b.GetBookingAsync(booking.Id)).ReturnsAsync(booking);
        _paymentService.Setup(p => p.CreatePaymentAsync(It.IsAny<Payment>()))
            .ReturnsAsync((Payment p) => p);

        var result = await CreateController().Create(new CreatePaymentRequest(
            booking.Id, 100m, PaymentMethod.CreditCard, null, null, null));

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var createdPayment = Assert.IsType<Payment>(created.Value);
        Assert.Equal(OrgId, createdPayment.OrgId);
    }

    private PaymentsController CreateController(string userId = OwnerId, params string[] roles)
    {
        var controller = new PaymentsController(
            _paymentService.Object,
            _paymentList.Object,
            _bookingService.Object,
            _hostResources.Object,
            HostAuthorizationTestHarness.Create(OrgId, (c, p) => _permissions?.Invoke(c, p) ?? true),
            _orgResolver.Object,
            HostAuthorizationTestHarness.ScopeResolver(),
            _fiscal.Object,
            _refundService.Object,
            Mock.Of<ILogger<PaymentsController>>());

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = HostAuthorizationTestHarness.User(userId, roles),
                // ApiProblem (FD-05 contract) localizes the detail through SharedResources.
                RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
            },
        };
        return controller;
    }

    private static PaymentListItem MakeListItem() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "ABCDEFGHJK",
        PropertyId,
        "Trullo",
        "Anna Verdi",
        100m,
        0m,
        PaymentStatus.Completed,
        PaymentMethod.CreditCard,
        "tx_1",
        string.Empty,
        null,
        null,
        0m,
        false,
        0m,
        WithholdingSource.None,
        new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc));

    private static Booking MakeBooking(Guid propertyId, Guid? orgId = null) => new()
    {
        Id = Guid.NewGuid(),
        PropertyId = propertyId,
        OrgId = orgId ?? OrgId,
        CheckInDate = TimeProvider.System.TodayInRome(),
        CheckOutDate = TimeProvider.System.TodayInRome().AddDays(1),
        NumberOfGuests = 2,
        Status = BookingStatus.Confirmed,
        Source = BookingSource.Direct,
    };

    private static Payment MakePayment(Guid propertyId, Guid? orgId = null)
    {
        var booking = MakeBooking(propertyId, orgId);
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
