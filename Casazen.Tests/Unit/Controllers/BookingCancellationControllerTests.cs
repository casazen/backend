using System.Globalization;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Casazen.Web.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// BK-02 + TN-3: a booking is cancelled only by who may write it; when the cancellation moves money (refund, intent to
/// cancel) only the owning host or an org member with <c>payment.write</c>. A booking that came from a channel takes the other path, Octorate
/// first (E08/F02/US02/T02): refused where the channel is closed, 503 when Octorate does not answer, 200 only when Octorate confirmed, never
/// through the local cancellation and its money.
/// </summary>
public class BookingCancellationControllerTests
{
    private const string OwnerId = "auth0|owner_1";
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();

    private readonly Mock<IBookingService> _bookings = new();
    private readonly Mock<IBookingCancellationService> _cancellations = new();
    private readonly Mock<IOtaBookingCancellationService> _otaCancellations = new();
    private readonly Mock<IHostResourceLookup> _hostResources = new();
    private readonly Booking _booking = new()
    {
        Id = Guid.NewGuid(),
        PropertyId = PropertyId,
        OrgId = OrgId,
        Status = BookingStatus.Confirmed,
    };

    /// <summary>A confirmed booking of an Airbnb guest that has its reservation on Octorate: a booking of a channel.</summary>
    private readonly Booking _channelBooking = new()
    {
        Id = Guid.NewGuid(),
        PropertyId = PropertyId,
        OrgId = OrgId,
        Status = BookingStatus.Confirmed,
        Source = BookingSource.Airbnb,
        OctorateReservationId = "7001",
    };
    private Func<string, string, bool>? _permissions;

    public BookingCancellationControllerTests()
    {
        _bookings.Setup(b => b.GetBookingAsync(_booking.Id)).ReturnsAsync(_booking);
        _bookings.Setup(b => b.GetBookingAsync(_channelBooking.Id)).ReturnsAsync(_channelBooking);
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
    public async Task Cancel_OwnerOfPaidBooking_RefundsTheFullRefundableAmount()
    {
        GivenQuote(refundable: 400m);

        var result = await Controller().Cancel(_booking.Id, new CancelBookingRequest { RefundAmount = 250m, Reason = "Guasto" });

        Assert.IsType<OkObjectResult>(result.Result);
        _cancellations.Verify(c => c.CancelAsync(
            It.Is<BookingCancellationRequest>(r => r.RefundAmount == 400m && r.Reason == "Guasto"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Cancel_ByTheHost_IsARequestOfTheHostSoTheGuestGetsEverythingRefundable()
    {
        // DB-08h: the request carries who cancels; the host's endpoint must never ask for the guest's (policy) minimum.
        GivenQuote(refundable: 400m);

        await Controller().Cancel(_booking.Id, new CancelBookingRequest { RefundAmount = 100m });

        _cancellations.Verify(c => c.CancelAsync(
            It.Is<BookingCancellationRequest>(r => r.Initiator == BookingCancellationInitiator.Host && r.RefundAmount == 400m),
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

    [Fact]
    public async Task Cancel_ChannelBookingOfAClosedChannel_Is422WithTheSentenceAndTheMembersOfTheChannel_AndNothingIsCancelledLocally()
    {
        _otaCancellations
            .Setup(s => s.CancelAsync(_channelBooking.Id, "Ospite non arrivato", It.IsAny<CancellationToken>()))
            .ThrowsAsync(OtaChannelActionPolicy.Closed.CancelNotAllowed(BookingSource.Airbnb));

        var problem = await ProblemOfAsync(
            _channelBooking.Id, controller => controller.Cancel(_channelBooking.Id, new CancelBookingRequest { Reason = "Ospite non arrivato" }));

        // 422 with the sentence of the channel in the language of the request, and the members the screens read.
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, problem.Status);
        Assert.Equal("ota_booking_cancel_not_allowed", problem.Code);
        Assert.Equal("Per cancellare questa prenotazione di Airbnb chiedi all'ospite di annullarla su Airbnb.", problem.Detail);
        Assert.Equal("OtaChannelCancelGuidanceAirbnb", problem.GuidanceKey);
        Assert.Equal("Airbnb", problem.Channel);

        // The booking of a channel is never cancelled through the local cancellation: no quote, no refund, no Stripe.
        VerifyNotCancelled();
        _cancellations.Verify(c => c.GetQuoteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Cancel_ChannelBookingWhenOctorateDoesNotAnswer_Is503WithTheMembersOfTheChannel_AndNothingIsCancelledLocally()
    {
        _otaCancellations
            .Setup(s => s.CancelAsync(_channelBooking.Id, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OtaCancellationUnavailableException(BookingSource.Airbnb, "OtaChannelCancelGuidanceAirbnb"));

        var result = await InItalianAsync(() => Controller().Cancel(_channelBooking.Id));

        var response = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.StatusCode);
        Assert.Contains("application/problem+json", response.ContentTypes);
        var problem = Assert.IsAssignableFrom<ProblemDetails>(response.Value);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.Status);
        Assert.Equal("octorate_unavailable", problem.Extensions["code"]);
        Assert.Equal("OtaChannelCancelGuidanceAirbnb", problem.Extensions["guidanceKey"]);
        Assert.Equal("Airbnb", problem.Extensions["channel"]);
        Assert.Equal("Per cancellare questa prenotazione di Airbnb chiedi all'ospite di annullarla su Airbnb.", problem.Detail);
        VerifyNotCancelled();
        _cancellations.Verify(c => c.GetQuoteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Cancel_ChannelBookingConfirmedByOctorate_Is200WithNoRefunds_NeedsNoPaymentWrite_AndTheLocalCancellationIsNeverUsed()
    {
        // The channel collects its own bookings: no money moves here, so booking.write is all it takes.
        _permissions = (_, permission) => !permission.StartsWith("payment.", StringComparison.Ordinal);
        _otaCancellations
            .Setup(s => s.CancelAsync(_channelBooking.Id, "Ospite non arrivato", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Booking { Id = _channelBooking.Id, Status = BookingStatus.Cancelled });

        var result = await Controller().Cancel(
            _channelBooking.Id, new CancelBookingRequest { RefundAmount = 250m, Reason = "Ospite non arrivato" });

        var body = Assert.IsType<CancelBookingResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(_channelBooking.Id, body.BookingId);
        Assert.Equal(BookingStatus.Cancelled, body.Status);
        Assert.Empty(body.Refunds);
        Assert.Equal(0, body.CanceledIntents);
        _otaCancellations.Verify(
            s => s.CancelAsync(_channelBooking.Id, "Ospite non arrivato", It.IsAny<CancellationToken>()), Times.Once);

        // Not the local cancellation: the refund the body asked for is never made, and no quote is read.
        VerifyNotCancelled();
        _cancellations.Verify(c => c.GetQuoteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Cancel_ChannelBookingOutOfTheReachOfTheCaller_Is404_AndOctorateIsNeverAsked()
    {
        // The property of another owner, then the same property with no booking.write: both are a booking the caller does not see.
        _hostResources.Setup(h => h.ForPropertyAsync(PropertyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostResource(OrgId, "auth0|someone_else"));
        var otherOwner = await Controller().Cancel(_channelBooking.Id, new CancelBookingRequest());

        _hostResources.Setup(h => h.ForPropertyAsync(PropertyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostResource(OrgId, OwnerId));
        _permissions = (_, permission) => permission != "booking.write";
        var withoutWrite = await Controller().Cancel(_channelBooking.Id, new CancelBookingRequest());

        Assert.IsType<NotFoundResult>(otherOwner.Result);
        Assert.IsType<NotFoundResult>(withoutWrite.Result);
        _otaCancellations.Verify(
            s => s.CancelAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyNotCancelled();
    }

    /// <summary>What the API answers when the action throws: the exception goes through the real error middleware, in Italian.</summary>
    private async Task<ProblemResponse> ProblemOfAsync(Guid bookingId, Func<BookingCancellationController, Task> action)
    {
        var controller = Controller();
        var context = (DefaultHttpContext)controller.HttpContext;
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = $"/api/bookings/{bookingId}/cancel";
        context.Response.Body = new MemoryStream();
        var middleware = new ErrorHandlingMiddleware(_ => action(controller), NullLogger<ErrorHandlingMiddleware>.Instance);

        await InItalianAsync(async () =>
        {
            await middleware.InvokeAsync(context);
            return 0;
        });

        context.Response.Body.Position = 0;
        using var json = JsonDocument.Parse(await new StreamReader(context.Response.Body).ReadToEndAsync());
        var root = json.RootElement;
        return new ProblemResponse(
            context.Response.StatusCode,
            TextOf(root, "code"),
            TextOf(root, "detail"),
            TextOf(root, "guidanceKey"),
            TextOf(root, "channel"));
    }

    private static string? TextOf(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static async Task<T> InItalianAsync<T>(Func<Task<T>> action)
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("it-IT");
        try
        {
            return await action();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    private sealed record ProblemResponse(int Status, string? Code, string? Detail, string? GuidanceKey, string? Channel);

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
            new Mock<IGuestBookingCancellationService>().Object,
            _hostResources.Object,
            HostAuthorizationTestHarness.Create(OrgId, (c, p) => _permissions?.Invoke(c, p) ?? true),
            NullLogger<BookingCancellationController>.Instance,
            _otaCancellations.Object);
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
