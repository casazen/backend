using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

public class BookingsControllerTests
{
    private readonly Mock<IBookingService> _mockBookingService;
    private readonly Mock<ITaxCalculationService> _mockTaxService;
    private readonly Mock<IAlloggiatiWebService> _mockAlloggiatiService;
    private readonly Mock<IPropertyService> _mockPropertyService;
    private readonly Mock<IPropertyAuthorizationService> _mockAuthz;
    private readonly Mock<IGuestService> _mockGuestService;
    private readonly Mock<ILogger<BookingsController>> _mockLogger;
    private readonly BookingsController _controller;

    private const string OwnerId = "auth0|owner_123";
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly Guid OrgId = Guid.NewGuid();

    public BookingsControllerTests()
    {
        _mockBookingService = new Mock<IBookingService>();
        _mockTaxService = new Mock<ITaxCalculationService>();
        _mockAlloggiatiService = new Mock<IAlloggiatiWebService>();
        _mockPropertyService = new Mock<IPropertyService>();
        _mockAuthz = new Mock<IPropertyAuthorizationService>();
        _mockGuestService = new Mock<IGuestService>();
        _mockLogger = new Mock<ILogger<BookingsController>>();

        _controller = new BookingsController(
            _mockBookingService.Object,
            _mockTaxService.Object,
            _mockAlloggiatiService.Object,
            _mockPropertyService.Object,
            _mockAuthz.Object,
            _mockGuestService.Object,
            _mockLogger.Object);
    }

    private void SetUser(string userId)
    {
        var identity = new ClaimsIdentity(new[] { new Claim("sub", userId) }, "TestAuth");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    private static Property MakeProperty() => new()
    {
        Id = PropertyId,
        OwnerId = OwnerId,
        OrgId = OrgId,
        Name = "Test Villa",
        NightlyRate = 100m,
        CleaningFee = 50m,
        MaxGuests = 4,
    };

    private static CreateBookingRequest MakeRequest() => new()
    {
        PropertyId = PropertyId,
        CheckInDate = DateTime.UtcNow.AddDays(7),
        CheckOutDate = DateTime.UtcNow.AddDays(11),
        NumberOfGuests = 2,
        Guest = new CreateBookingGuestRequest
        {
            FirstName = "Mario",
            LastName = "Rossi",
            Email = "mario.rossi@example.com",
            Phone = "+393331234567",
            Country = "Italia",
        },
    };

    [Fact]
    public async Task Create_WithValidRequest_ReturnsCreated()
    {
        SetUser(OwnerId);
        var guest = new Guest { Id = Guid.NewGuid(), Email = "mario.rossi@example.com" };

        _mockPropertyService.Setup(s => s.GetPropertyAsync(PropertyId)).ReturnsAsync(MakeProperty());
        _mockAuthz.Setup(a => a.CanAccess(OwnerId, OwnerId, It.IsAny<IEnumerable<string>>())).Returns(true);
        _mockGuestService.Setup(g => g.GetGuestByEmailAsync(guest.Email)).ReturnsAsync((Guest?)null);
        _mockGuestService.Setup(g => g.CreateGuestAsync(It.IsAny<Guest>())).ReturnsAsync(guest);
        _mockBookingService.Setup(b => b.IsPropertyAvailableAsync(PropertyId, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(true);
        _mockTaxService.Setup(t => t.CalculateTouristTaxAsync(PropertyId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), 2))
            .ReturnsAsync(12m);
        _mockBookingService.Setup(b => b.CreateBookingAsync(It.IsAny<Booking>()))
            .ReturnsAsync((Booking b) => { b.Id = Guid.NewGuid(); return b; });

        var result = await _controller.Create(MakeRequest());

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var booking = Assert.IsType<Booking>(created.Value);
        Assert.Equal(PropertyId, booking.PropertyId);
        Assert.Equal(OrgId, booking.OrgId);
        Assert.Equal(guest.Id, booking.GuestId);
        Assert.Equal(450m, booking.BasePrice); // 4 nights * 100 + 50 cleaning
        Assert.Equal(462m, booking.TotalPrice);
    }

    [Fact]
    public async Task Create_ReusesExistingGuestByEmail()
    {
        SetUser(OwnerId);
        var existingGuest = new Guest { Id = Guid.NewGuid(), Email = "mario.rossi@example.com" };

        _mockPropertyService.Setup(s => s.GetPropertyAsync(PropertyId)).ReturnsAsync(MakeProperty());
        _mockAuthz.Setup(a => a.CanAccess(OwnerId, OwnerId, It.IsAny<IEnumerable<string>>())).Returns(true);
        _mockGuestService.Setup(g => g.GetGuestByEmailAsync(existingGuest.Email)).ReturnsAsync(existingGuest);
        _mockBookingService.Setup(b => b.IsPropertyAvailableAsync(PropertyId, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(true);
        _mockTaxService.Setup(t => t.CalculateTouristTaxAsync(PropertyId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), 2))
            .ReturnsAsync(0m);
        _mockBookingService.Setup(b => b.CreateBookingAsync(It.IsAny<Booking>()))
            .ReturnsAsync((Booking b) => { b.Id = Guid.NewGuid(); return b; });

        var result = await _controller.Create(MakeRequest());

        Assert.IsType<CreatedAtActionResult>(result.Result);
        _mockGuestService.Verify(g => g.CreateGuestAsync(It.IsAny<Guest>()), Times.Never);
    }

    [Fact]
    public async Task Create_WhenPropertyNotFound_ReturnsNotFound()
    {
        SetUser(OwnerId);
        _mockPropertyService.Setup(s => s.GetPropertyAsync(PropertyId)).ReturnsAsync((Property?)null);

        var result = await _controller.Create(MakeRequest());

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task Create_WhenUnauthorizedForProperty_ReturnsForbid()
    {
        SetUser(OwnerId);
        _mockPropertyService.Setup(s => s.GetPropertyAsync(PropertyId)).ReturnsAsync(MakeProperty());
        _mockAuthz.Setup(a => a.CanAccess(OwnerId, OwnerId, It.IsAny<IEnumerable<string>>())).Returns(false);

        var result = await _controller.Create(MakeRequest());

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Create_WhenTooManyGuests_ReturnsBadRequest()
    {
        SetUser(OwnerId);
        _mockPropertyService.Setup(s => s.GetPropertyAsync(PropertyId)).ReturnsAsync(MakeProperty());
        _mockAuthz.Setup(a => a.CanAccess(OwnerId, OwnerId, It.IsAny<IEnumerable<string>>())).Returns(true);

        var request = MakeRequest();
        request.NumberOfGuests = 10;

        var result = await _controller.Create(request);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }
}
