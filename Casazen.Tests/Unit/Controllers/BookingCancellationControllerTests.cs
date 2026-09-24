using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// BK-02 + TN-3: a booking is cancelled only by who may write it; when the cancellation moves money (refund, intent to
/// cancel) only the owning host or an org member with <c>payment.write</c>.
/// </summary>
public class BookingCancellationControllerTests
{
    private const string OwnerId = "auth0|owner_1";
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();

    private readonly Mock<IBookingService> _bookings = new();
    private readonly Mock<IBookingCancellationService> _cancellations = new();
    private readonly Mock<IHostResourceLookup> _hostResources = new();
    private readonly Booking _booking = new()
    {
        Id = Guid.NewGuid(),
        PropertyId = PropertyId,
        OrgId = OrgId,
        Status = BookingStatus.Confirmed,
    };
    private Func<string, string, bool>? _permissions;

    public BookingCancellationControllerTests()
    {
        _bookings.Setup(b => b.GetBookingAsync(_booking.Id)).ReturnsAsync(_booking);
        _hostResources.Setup(h => h.ForPropertyAsync(PropertyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostResource(OrgId, OwnerId));
        _cancellations
            .Setup(c => c.CancelAsync(It.IsAny<BookingCancellationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new BookingCancellationResult(
                new Booking { Id = _booking.Id, Status = BookingStatus.Cancelled }, [], 0));
    }

    [Fact]
    public async Task Cancel_BookingOfAnotherOwnersProperty_ReturnsNotFoundWithoutCancelling()
    {
        _hostResources.Setup(h => h.ForPropertyAsync(PropertyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostResource(OrgId, "auth0|someone_else"));
        GivenQuote(refundable: 0m);

        var result = await Controller().Cancel(_booking.Id, new CancelBookingRequest());

        Assert.IsType<NotFoundResult>(result.Result);
        VerifyNotCancelled();
    }

    [Fact]
    public async Task Cancel_PaidBookingWithoutPaymentWrite_Returns403WithoutCancelling()
    {
        GivenQuote(refundable: 400m);
        _permissions = (_, permission) => permission != "payment.write";

        var result = await Controller().Cancel(_booking.Id, new CancelBookingRequest { RefundAmount = 400m });

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, problem.StatusCode);
        VerifyNotCancelled();
    }

    [Fact]
    public async Task Cancel_UncollectedIntentWithoutPaymentWrite_Returns403WithoutCancelling()
    {
        GivenQuote(refundable: 0m, uncollectedIntent: true);
        _permissions = (_, permission) => permission != "payment.write";

        var result = await Controller().Cancel(_booking.Id);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        VerifyNotCancelled();
    }

    [Fact]
    public async Task Cancel_UnpaidBookingWithBookingWriteOnly_Cancels()
    {
        GivenQuote(refundable: 0m);
        _permissions = (_, permission) => !permission.StartsWith("payment.", StringComparison.Ordinal);

        var result = await Controller().Cancel(_booking.Id);

        var body = Assert.IsType<CancelBookingResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(BookingStatus.Cancelled, body.Status);
        _cancellations.Verify(c => c.CancelAsync(
            It.Is<BookingCancellationRequest>(r => r.BookingId == _booking.Id && r.RefundAmount == null && r.RequestedByUserId == OwnerId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Cancel_OwnerOfPaidBooking_PassesTheChosenRefund()
    {
        GivenQuote(refundable: 400m);

        var result = await Controller().Cancel(_booking.Id, new CancelBookingRequest { RefundAmount = 250m, Reason = "Guasto" });

        Assert.IsType<OkObjectResult>(result.Result);
        _cancellations.Verify(c => c.CancelAsync(
            It.Is<BookingCancellationRequest>(r => r.RefundAmount == 250m && r.Reason == "Guasto"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetQuote_WithoutPaymentRead_Returns403()
    {
        GivenQuote(refundable: 400m);
        _permissions = (_, permission) => permission != "payment.read";

        var result = await Controller().GetQuote(_booking.Id);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        _cancellations.Verify(c => c.GetQuoteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetQuote_MissingBooking_ReturnsNotFound()
    {
        var result = await Controller().GetQuote(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result.Result);
    }

    private void GivenQuote(decimal refundable, bool uncollectedIntent = false) =>
        _cancellations
            .Setup(c => c.GetQuoteAsync(_booking.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingCancellationQuote(
                _booking.Id, _booking.Status, true, "EUR", refundable, 0m, 0m, refundable, 0m,
                CancellationRefundRule.None, null, null, 0m, uncollectedIntent));

    private void VerifyNotCancelled()
    {
        _cancellations.Verify(c => c.CancelAsync(It.IsAny<BookingCancellationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private BookingCancellationController Controller()
    {
        var controller = new BookingCancellationController(
            _bookings.Object,
            _cancellations.Object,
            _hostResources.Object,
            HostAuthorizationTestHarness.Create(OrgId, (c, p) => _permissions?.Invoke(c, p) ?? true),
            NullLogger<BookingCancellationController>.Instance);
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
