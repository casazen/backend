using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

public class BookingsControllerTests
{
    private readonly Mock<IBookingService> _mockBookingService;
    private readonly Mock<IAlloggiatiWebService> _mockAlloggiatiService;
    private readonly Mock<IPropertyService> _mockPropertyService;
    private readonly Mock<IPropertyAuthorizationService> _mockAuthz;
    private readonly Mock<ILogger<BookingsController>> _mockLogger;
    private readonly BookingsController _controller;

    private const string OwnerId = "auth0|owner_123";
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly Guid OrgId = Guid.NewGuid();

    public BookingsControllerTests()
    {
        _mockBookingService = new Mock<IBookingService>();
        // Host pricing (PC-07): nightly rate x nights + cleaning fee; tourist tax unknown unless a test says otherwise.
        _mockBookingService
            .Setup(b => b.PriceHostStayAsync(
                It.IsAny<Property>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<IReadOnlyList<int>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Property property, DateTime checkIn, DateTime checkOut, int _, int _, IReadOnlyList<int>? _, CancellationToken _) =>
                HostQuote(property, checkIn, checkOut, new TouristTaxQuote(TouristTaxQuoteStatus.RateUnavailable, null, 0, 0, false, [], [])));
        _mockAlloggiatiService = new Mock<IAlloggiatiWebService>();
        _mockPropertyService = new Mock<IPropertyService>();
        _mockAuthz = new Mock<IPropertyAuthorizationService>();
        _mockLogger = new Mock<ILogger<BookingsController>>();

        _controller = CreateController();
    }

    private BookingsController CreateController() =>
        new(
            _mockBookingService.Object,
            _mockAlloggiatiService.Object,
            _mockPropertyService.Object,
            _mockAuthz.Object,
            CreatePropertyICalSyncService(),
            _mockLogger.Object);

    private static PropertyICalSyncService CreatePropertyICalSyncService()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:ApiBaseUrl"] = "https://api.test" })
            .Build();
        return ICalTestServices.PropertySync(db, Mock.Of<ISafeExternalHttpClient>(), configuration);
    }

    private void SetUser(string userId)
    {
        var identity = new ClaimsIdentity(new[] { new Claim("sub", userId) }, "TestAuth");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity),
                // ApiProblem (FD-05 contract) localizes the detail through SharedResources.
                RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
            },
        };
    }

    // TN-3: the real resource handler, with the caller in the property's org unless told otherwise.
    private static IAuthorizationService HostAuthorization(Guid? callerOrgId = null) =>
        HostAuthorizationTestHarness.Create(callerOrgId ?? OrgId);

    /// <summary>What <c>BookingService.PriceHostStayAsync</c> returns: nightly rate x nights + cleaning fee, plus the tax.</summary>
    private static DirectBookingQuote HostQuote(Property property, DateTime checkIn, DateTime checkOut, TouristTaxQuote tax)
    {
        var nights = (checkOut.Date - checkIn.Date).Days;
        var basePrice = property.NightlyRate * nights + property.CleaningFee;
        return new DirectBookingQuote(
            property.Id, checkIn.Date, checkOut.Date, nights, property.NightlyRate, property.CleaningFee, basePrice, tax,
            basePrice + tax.AmountOrZero, "EUR",
            DirectBookingPaymentRules.FreeRefundDeadline(checkIn, property.CancellationPolicy),
            new DirectBookingPaymentOptions(DeferredPaymentAvailable: false, DeferredChargeDate: null, FreeCancellationUntil: null));
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
    public async Task GetAll_ReturnsBookingResponseDtosWithoutCircularRefs()
    {
        SetUser(OwnerId);
        var guest = new Guest { Id = Guid.NewGuid(), FirstName = "Mario", LastName = "Rossi", Email = "mario@test.com" };
        var property = MakeProperty();
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            PropertyId = PropertyId,
            OrgId = OrgId,
            GuestId = guest.Id,
            Guest = guest,
            Property = property,
            CheckInDate = DateTime.UtcNow.AddDays(1),
            CheckOutDate = DateTime.UtcNow.AddDays(3),
            NumberOfGuests = 2,
            BasePrice = 200m,
            TouristTax = 10m,
            TotalPrice = 210m,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
        };

        _mockBookingService.Setup(b => b.GetAllBookingsAsync()).ReturnsAsync([booking]);
        _mockAuthz.Setup(a => a.CanAccessPropertyAsync(OwnerId, PropertyId, It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(true);

        var result = await _controller.GetAll(null);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var items = Assert.IsAssignableFrom<IEnumerable<BookingResponseDto>>(ok.Value);
        var dto = Assert.Single(items);
        Assert.Equal(booking.Id, dto.Id);
        Assert.Equal("Test Villa", dto.PropertyName);
        Assert.Equal("mario@test.com", dto.Guest.Email);
    }

    [Fact]
    public async Task GetAll_WithPropertyId_WhenUnauthorized_ReturnsNotFound()
    {
        SetUser(OwnerId);
        _mockAuthz.Setup(a => a.CanAccessPropertyAsync(OwnerId, PropertyId, It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(false);

        var result = await _controller.GetAll(PropertyId);

        Assert.IsType<NotFoundResult>(result.Result);
        _mockBookingService.Verify(b => b.GetPropertyBookingsAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task GetAll_WithGuestId_FiltersOutBookingsFromUnauthorizedProperties()
    {
        SetUser(OwnerId);
        var guestId = Guid.NewGuid();
        var accessibleBooking = new Booking
        {
            Id = Guid.NewGuid(),
            PropertyId = PropertyId,
            OrgId = OrgId,
            GuestId = guestId,
            Guest = new Guest { Id = guestId, Email = "guest@example.com" },
            Property = MakeProperty(),
            CheckInDate = DateTime.UtcNow.AddDays(1),
            CheckOutDate = DateTime.UtcNow.AddDays(2),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
        };
        var otherPropertyId = Guid.NewGuid();
        var leakedBooking = new Booking
        {
            Id = Guid.NewGuid(),
            PropertyId = otherPropertyId,
            OrgId = Guid.NewGuid(),
            GuestId = guestId,
            Guest = new Guest { Id = guestId, Email = "guest@example.com" },
            CheckInDate = DateTime.UtcNow.AddDays(3),
            CheckOutDate = DateTime.UtcNow.AddDays(4),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
        };

        _mockBookingService.Setup(b => b.GetGuestBookingsAsync(guestId))
            .ReturnsAsync([accessibleBooking, leakedBooking]);
        _mockAuthz.Setup(a => a.CanAccessPropertyAsync(OwnerId, PropertyId, It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(true);
        _mockAuthz.Setup(a => a.CanAccessPropertyAsync(OwnerId, otherPropertyId, It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(false);

        var result = await _controller.GetAll(null, guestId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var items = Assert.IsAssignableFrom<IEnumerable<BookingResponseDto>>(ok.Value);
        var dto = Assert.Single(items);
        Assert.Equal(accessibleBooking.Id, dto.Id);
    }

    [Fact]
    public async Task Create_WithValidRequest_CreatesManualBookingWithGuestSnapshotOfPropertyOrg()
    {
        SetUser(OwnerId);
        Booking? stored = null;
        Guest? storedGuest = null;

        _mockPropertyService.Setup(s => s.GetPropertyAsync(PropertyId)).ReturnsAsync(MakeProperty());
        // No minors in the request: every guest counts as an adult for the tourist tax (BK-03).
        _mockBookingService
            .Setup(b => b.PriceHostStayAsync(
                It.Is<Property>(p => p.Id == PropertyId),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                2,
                0,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Property property, DateTime checkIn, DateTime checkOut, int _, int _, IReadOnlyList<int>? _, CancellationToken _) =>
                HostQuote(property, checkIn, checkOut, new TouristTaxQuote(TouristTaxQuoteStatus.Calculated, 12m, 4, 4, false, [], [])));
        _mockBookingService.Setup(b => b.CreateManualBookingAsync(It.IsAny<Booking>(), It.IsAny<Guest>()))
            .Callback<Booking, Guest>((booking, guest) =>
            {
                booking.Id = Guid.NewGuid();
                booking.Status = BookingStatus.Confirmed;
                booking.Source = BookingSource.Manual;
                booking.Guest = guest;
                booking.Property = MakeProperty();
                stored = booking;
                storedGuest = guest;
            })
            .ReturnsAsync((Booking b, Guest _) => b);
        _mockBookingService.Setup(b => b.GetBookingAsync(It.IsAny<Guid>())).ReturnsAsync(() => stored);

        var result = await _controller.Create(MakeRequest(), HostAuthorization());

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var dto = Assert.IsType<BookingResponseDto>(created.Value);
        Assert.Equal(PropertyId, dto.PropertyId);
        Assert.Equal("Confirmed", dto.Status);
        Assert.Equal("Manual", dto.Source);
        Assert.Equal("mario.rossi@example.com", dto.Guest.Email);
        Assert.NotNull(stored);
        Assert.Equal(OrgId, stored!.OrgId);
        Assert.Equal(450m, stored.BasePrice);
        Assert.Equal(50m, stored.CleaningFee);
        Assert.Equal(462m, stored.TotalPrice);
        Assert.Equal(12m, stored.TouristTax);
        Assert.Equal(12m, stored.TouristTaxAmount);
        Assert.Equal(2, stored.NumberOfAdults);
        Assert.Equal(0, stored.NumberOfChildren);
        Assert.NotNull(storedGuest);
        Assert.Equal(OrgId, storedGuest!.OrgId);
        Assert.Equal("Mario", storedGuest.FirstName);
        Assert.Equal("+393331234567", storedGuest.PhoneNumber);
        Assert.Equal("Italia", storedGuest.Country);
    }

    [Fact]
    public async Task Create_WhenDatesOverlap_PropagatesConflictForTheErrorMiddleware()
    {
        SetUser(OwnerId);
        _mockPropertyService.Setup(s => s.GetPropertyAsync(PropertyId)).ReturnsAsync(MakeProperty());
        _mockBookingService.Setup(b => b.CreateManualBookingAsync(It.IsAny<Booking>(), It.IsAny<Guest>()))
            .ThrowsAsync(new DomainConflictException(BookingErrorCodes.DatesUnavailable, "BookingDatesUnavailable"));

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() =>
            _controller.Create(MakeRequest(), HostAuthorization()));

        Assert.Equal("booking_dates_unavailable", ex.Code);
    }

    [Fact]
    public async Task Create_WhenPropertyNotFound_ReturnsNotFoundProblem()
    {
        SetUser(OwnerId);
        _mockPropertyService.Setup(s => s.GetPropertyAsync(PropertyId)).ReturnsAsync((Property?)null);

        var result = await _controller.Create(MakeRequest(), HostAuthorization());

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        _mockBookingService.Verify(b => b.CreateManualBookingAsync(It.IsAny<Booking>(), It.IsAny<Guest>()), Times.Never);
    }

    [Fact]
    public async Task Create_WhenCallerDoesNotOwnPropertyAndHasNoOrgWideRole_ReturnsForbid()
    {
        SetUser("auth0|colleague");
        _mockPropertyService.Setup(s => s.GetPropertyAsync(PropertyId)).ReturnsAsync(MakeProperty());

        var result = await _controller.Create(MakeRequest(), HostAuthorization());

        Assert.IsType<ForbidResult>(result.Result);
        _mockBookingService.Verify(b => b.CreateManualBookingAsync(It.IsAny<Booking>(), It.IsAny<Guest>()), Times.Never);
    }

    [Fact]
    public async Task Create_WhenCallerIsInAnotherOrg_ReturnsForbid()
    {
        SetUser(OwnerId);
        _mockPropertyService.Setup(s => s.GetPropertyAsync(PropertyId)).ReturnsAsync(MakeProperty());

        var result = await _controller.Create(MakeRequest(), HostAuthorization(Guid.NewGuid()));

        Assert.IsType<ForbidResult>(result.Result);
        _mockBookingService.Verify(b => b.CreateManualBookingAsync(It.IsAny<Booking>(), It.IsAny<Guest>()), Times.Never);
    }

    [Fact]
    public async Task Create_WhenTooManyGuests_ReturnsUnprocessableProblemWithStableCode()
    {
        SetUser(OwnerId);
        _mockPropertyService.Setup(s => s.GetPropertyAsync(PropertyId)).ReturnsAsync(MakeProperty());

        var request = MakeRequest();
        request.NumberOfGuests = 10;

        var result = await _controller.Create(request, HostAuthorization());

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, problem.StatusCode);
        var details = Assert.IsAssignableFrom<ProblemDetails>(problem.Value);
        Assert.Equal("booking_too_many_guests", details.Extensions["code"]);
        _mockBookingService.Verify(b => b.CreateManualBookingAsync(It.IsAny<Booking>(), It.IsAny<Guest>()), Times.Never);
    }
}
