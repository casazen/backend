using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// DB-08h (BK-02, BK-07): the public cancellation link answers 200 with the booking, its status and the refund; a link
/// without a token is a 400 and never reaches the service.
/// </summary>
public class PublicGuestBookingCancellationControllerTests
{
    private readonly Mock<IGuestBookingCancellationService> _service = new();

    [Fact]
    public async Task Cancel_ValidToken_Returns200WithBookingStatusAndRefund()
    {
        var bookingId = Guid.NewGuid();
        _service
            .Setup(s => s.CancelByGuestLinkAsync(bookingId, "the-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuestCancellationResult(
                new Booking { Id = bookingId, Status = BookingStatus.Cancelled },
                [new PaymentRefund { Amount = 200m }],
                200m));

        var result = await Controller().Cancel(bookingId, "the-token");

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = JsonSerializer.SerializeToElement(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(bookingId, body.GetProperty("bookingId").GetGuid());
        Assert.Equal("Cancelled", body.GetProperty("status").GetString());
        Assert.Equal(200m, body.GetProperty("refundAmount").GetDecimal());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Cancel_MissingToken_Returns400WithoutCallingTheService(string? token)
    {
        var result = await Controller().Cancel(Guid.NewGuid(), token);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Equal("guest_cancel_token_missing", Assert.IsAssignableFrom<ProblemDetails>(problem.Value).Extensions["code"]);
        _service.Verify(
            s => s.CancelByGuestLinkAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private PublicGuestBookingCancellationController Controller()
    {
        var controller = new PublicGuestBookingCancellationController(
            _service.Object,
            NullLogger<PublicGuestBookingCancellationController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
            },
        };
        return controller;
    }
}
