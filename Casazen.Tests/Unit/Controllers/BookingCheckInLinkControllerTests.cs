using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Casazen.Tests.Unit.Authorization;
using Casazen.Tests.Unit.Email;
using Casazen.Web.Controllers;
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

    private Booking SetupBooking(BookingStatus status)
    {
        var booking = new Booking { Id = Guid.NewGuid(), PropertyId = Guid.NewGuid(), OrgId = OrgId, Status = status };
        _bookings.Setup(b => b.GetBookingAsync(booking.Id)).ReturnsAsync(booking);
        _resources
            .Setup(r => r.ForPropertyAsync(booking.PropertyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostResource(OrgId, OwnerId));
        return booking;
    }

    private BookingCheckInLinkController CreateController(PublicSiteLinks links) =>
        new(
            _bookings.Object,
            _resources.Object,
            HostAuthorizationTestHarness.Create(OrgId),
            _checkIn.Object,
            _linkEmails.Object,
            links,
            NullLogger<BookingCheckInLinkController>.Instance)
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
