using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Casazen.Tests.Unit.Authorization;
using Casazen.Tests.Unit.Email;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.CheckIn;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>Host check-in link endpoints (CO-09): the checks that come before a link is issued.</summary>
public class BookingCheckInLinkControllerTests
{
    private const string OwnerId = "auth0|owner_link";
    private static readonly Guid OrgId = Guid.NewGuid();

    private readonly Mock<IBookingService> _bookings = new();
    private readonly Mock<IHostResourceLookup> _resources = new();
    private readonly Mock<IGuestCheckInService> _checkIn = new();
    private readonly Mock<IGuestCheckInLinkEmailQueue> _linkEmails = new();

    [Fact]
    public async Task ResendCheckInLink_PublicSiteBaseUrlMissing_ThrowsConfigurationErrorWithoutIssuingLink()
    {
        var booking = SetupBooking(BookingStatus.Confirmed);
        var controller = CreateController(EmailTestHelpers.Links(null));

        await Assert.ThrowsAsync<EmailConfigurationException>(() => controller.ResendCheckInLink(booking.Id));

        _checkIn.Verify(s => s.IssueLinkAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
        _linkEmails.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(BookingStatus.Pending)]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.CheckedOut)]
    public async Task CreateCheckInLink_BookingNotConfirmedOrCheckedIn_Returns409WithoutIssuingLink(BookingStatus status)
    {
        var booking = SetupBooking(status);
        var controller = CreateController(EmailTestHelpers.Links());

        var result = await controller.CreateCheckInLink(booking.Id);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        _checkIn.Verify(s => s.IssueLinkAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task CreateCheckInLink_Confirmed_ReturnsTheLinkWithoutEmail()
    {
        var booking = SetupBooking(BookingStatus.Confirmed);
        var expiresAt = DateTime.UtcNow.AddDays(7);
        _checkIn.Setup(s => s.IssueLinkAsync(booking.Id, OrgId)).ReturnsAsync(new IssuedCheckInLink(Guid.NewGuid(), "tok", expiresAt));
        var controller = CreateController(EmailTestHelpers.Links("https://public.test"));

        var result = await controller.CreateCheckInLink(booking.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<Casazen.Web.DTOs.CheckIn.CheckInLinkResponse>(ok.Value);
        Assert.Equal("https://public.test/checkin/tok", response.CheckInLink);
        Assert.Equal(expiresAt, response.ExpiresAt);
        Assert.Equal(GuestCheckInLinkEmailStatus.NotRequested, response.EmailStatus);
        _linkEmails.VerifyNoOtherCalls();
    }

    /// <summary>
    /// PROOF TEST: permission rule (E23/F01/US01/T01). A booking the caller cannot see (another org's: the tenant filter hides it) is a 404 and the delivery of its link is never
    /// read; a booking whose property the caller does not reach is refused with 403, whatever else it can do.
    /// </summary>
    [Fact]
    public async Task LinkDelivery_OtherOrganizationBooking404_Test()
    {
        var foreign = Guid.NewGuid();
        _bookings.Setup(b => b.GetBookingAsync(foreign)).ReturnsAsync((Booking?)null);
        var controller = CreateController(EmailTestHelpers.Links(), deliveries: _deliveries.Object);

        var notFound = await controller.GetCheckInLinkDelivery(foreign);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ObjectResult>(notFound.Result).StatusCode);

        // A booking of this org whose property the caller does not reach (another owner's): refused.
        var booking = SetupBooking(BookingStatus.Confirmed, propertyOwner: "auth0|somebody_else");
        var outsider = CreateController(EmailTestHelpers.Links(), deliveries: _deliveries.Object);

        var forbidden = await outsider.GetCheckInLinkDelivery(booking.Id);

        Assert.IsType<ForbidResult>(forbidden.Result);
        _deliveries.VerifyNoOtherCalls();
    }

    /// <summary>
    /// PROOF TEST: permission rule (E23/F01/US01/T01). The link to copy is returned only to a caller who can already copy one (<c>booking.write</c> on the property, as for the
    /// endpoint that generates it); a caller who can only read the booking gets the state and the reason, never the link; and nobody gets it for a booking that no longer takes a
    /// check-in.
    /// </summary>
    [Fact]
    public async Task LinkDelivery_LinkHiddenWithoutPermission_Test()
    {
        var booking = SetupBooking(BookingStatus.Confirmed);
        var cancelled = SetupBooking(BookingStatus.Cancelled);
        _deliveries
            .Setup(d => d.GetAsync(OrgId, It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid _, bool includeLink, CancellationToken _) => new CheckInLinkDeliveryView(
                CheckInLinkChannel.PortalChat,
                CheckInLinkDeliveryState.WaitingChatPermission,
                1,
                "chat_not_licensed",
                null,
                null,
                null,
                DateTime.UtcNow.AddDays(7),
                includeLink ? "https://public.test/checkin/tok" : null));
        var writer = CreateController(EmailTestHelpers.Links("https://public.test"), deliveries: _deliveries.Object);
        var reader = CreateController(
            EmailTestHelpers.Links("https://public.test"),
            HostAuthorizationTestHarness.Create(OrgId, (_, permission) => permission != "booking.write"),
            _deliveries.Object);

        var copied = Assert.IsType<CheckInLinkDeliveryResponse>(Assert.IsType<OkObjectResult>((await writer.GetCheckInLinkDelivery(booking.Id)).Result).Value);
        var read = Assert.IsType<CheckInLinkDeliveryResponse>(Assert.IsType<OkObjectResult>((await reader.GetCheckInLinkDelivery(booking.Id)).Result).Value);
        var over = Assert.IsType<CheckInLinkDeliveryResponse>(Assert.IsType<OkObjectResult>((await writer.GetCheckInLinkDelivery(cancelled.Id)).Result).Value);

        Assert.Equal("https://public.test/checkin/tok", copied.CheckInLink);
        Assert.Equal((CheckInLinkChannel.PortalChat, CheckInLinkDeliveryState.WaitingChatPermission, "chat_not_licensed"), (copied.Channel, copied.State, copied.LastErrorCode));
        Assert.Null(read.CheckInLink);
        Assert.Equal((CheckInLinkDeliveryState.WaitingChatPermission, "chat_not_licensed"), (read.State, read.LastErrorCode));
        Assert.Null(over.CheckInLink);
        _deliveries.Verify(d => d.GetAsync(OrgId, booking.Id, true, It.IsAny<CancellationToken>()), Times.Once);
        _deliveries.Verify(d => d.GetAsync(OrgId, booking.Id, false, It.IsAny<CancellationToken>()), Times.Once);
        _deliveries.Verify(d => d.GetAsync(OrgId, cancelled.Id, false, It.IsAny<CancellationToken>()), Times.Once);

        // A booking with no delivery record answers with empty members, not with an error.
        _deliveries
            .Setup(d => d.GetAsync(OrgId, booking.Id, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CheckInLinkDeliveryView?)null);
        var none = Assert.IsType<CheckInLinkDeliveryResponse>(Assert.IsType<OkObjectResult>((await writer.GetCheckInLinkDelivery(booking.Id)).Result).Value);
        Assert.Null(none.State);
        Assert.Null(none.CheckInLink);
    }

    /// <summary>
    /// When the host issues a link itself (it copies a new one or sends it), the delivery of the link the system was trying to take to the guest ends: the previous link stopped working
    /// and the host carries the link from here (E23/F01/US01/T01).
    /// </summary>
    [Fact]
    public async Task CreateCheckInLink_TheHostIssuesALink_EndsTheDeliveryOfThePreviousOne()
    {
        var booking = SetupBooking(BookingStatus.Confirmed);
        _checkIn.Setup(s => s.IssueLinkAsync(booking.Id, OrgId)).ReturnsAsync(new IssuedCheckInLink(Guid.NewGuid(), "tok", DateTime.UtcNow.AddDays(7)));
        var controller = CreateController(EmailTestHelpers.Links("https://public.test"), deliveries: _deliveries.Object);

        await controller.CreateCheckInLink(booking.Id);

        _deliveries.Verify(d => d.EndAsync(OrgId, booking.Id, CheckInLinkEndReasons.HostLink, It.IsAny<CancellationToken>()), Times.Once);
    }

    private readonly Mock<ICheckInLinkDeliveryService> _deliveries = new();

    private Booking SetupBooking(BookingStatus status, string propertyOwner = OwnerId)
    {
        var booking = new Booking { Id = Guid.NewGuid(), PropertyId = Guid.NewGuid(), OrgId = OrgId, Status = status };
        _bookings.Setup(b => b.GetBookingAsync(booking.Id)).ReturnsAsync(booking);
        _resources
            .Setup(r => r.ForPropertyAsync(booking.PropertyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostResource(OrgId, propertyOwner));
        return booking;
    }

    private BookingCheckInLinkController CreateController(
        PublicSiteLinks links,
        IAuthorizationService? authorization = null,
        ICheckInLinkDeliveryService? deliveries = null) =>
        new(
            _bookings.Object,
            _resources.Object,
            authorization ?? HostAuthorizationTestHarness.Create(OrgId),
            _checkIn.Object,
            _linkEmails.Object,
            links,
            NullLogger<BookingCheckInLinkController>.Instance,
            null,
            deliveries)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = HostAuthorizationTestHarness.User(OwnerId, "PropertyOwner"),
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };
}
