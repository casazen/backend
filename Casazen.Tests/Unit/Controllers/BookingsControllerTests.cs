using System.Globalization;
using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Tests.Unit.Email;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Compliance;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    private readonly Mock<IAlloggiatiReportScheduler> _mockAlloggiatiScheduler;
    private readonly Mock<IGuestCheckInService> _mockGuestCheckInService;
    private readonly Mock<IComplianceWizardService> _mockComplianceWizardService;
    private readonly Mock<ICheckoutReminderScheduler> _mockCheckoutReminderScheduler;
    private readonly Mock<IEmailQueue> _mockEmailQueue;
    private readonly List<(string? To, EmailContent Content, string Template)> _queuedEmails = [];
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
        _mockAlloggiatiScheduler = new Mock<IAlloggiatiReportScheduler>();
        _mockGuestCheckInService = new Mock<IGuestCheckInService>();
        _mockComplianceWizardService = new Mock<IComplianceWizardService>();
        _mockCheckoutReminderScheduler = new Mock<ICheckoutReminderScheduler>();
        _mockEmailQueue = new Mock<IEmailQueue>();
        _mockEmailQueue
            .Setup(q => q.Enqueue(It.IsAny<string?>(), It.IsAny<EmailContent>(), It.IsAny<string>()))
            .Callback<string?, EmailContent, string>((to, content, template) => _queuedEmails.Add((to, content, template)))
            .Returns(true);
        _mockCheckoutReminderScheduler
            .Setup(s => s.ScheduleReminder(It.IsAny<Guid>(), It.IsAny<DateTime>()))
            .Returns("job-test");
        _mockLogger = new Mock<ILogger<BookingsController>>();

        _controller = CreateController(EmailTestHelpers.Links("https://public.test"));
    }

    private BookingsController CreateController(PublicSiteLinks publicSiteLinks, TimeProvider? timeProvider = null) =>
        new(
            _mockBookingService.Object,
            _mockTaxService.Object,
            _mockAlloggiatiService.Object,
            _mockPropertyService.Object,
            _mockAuthz.Object,
            CreatePropertyICalSyncService(),
            _mockAlloggiatiScheduler.Object,
            _mockGuestCheckInService.Object,
            _mockComplianceWizardService.Object,
            _mockCheckoutReminderScheduler.Object,
            Options.Create(new ComplianceOptions { CheckoutReminderHourLocal = 20 }),
            _mockEmailQueue.Object,
            publicSiteLinks,
            CreateLocalizer(),
            _mockLogger.Object,
            timeProvider);

    private static IStringLocalizer<SharedResources> CreateLocalizer() =>
        new StringLocalizer<SharedResources>(new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()),
            NullLoggerFactory.Instance));

    private static PropertyICalSyncService CreatePropertyICalSyncService()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:ApiBaseUrl"] = "https://api.test" })
            .Build();
        return new PropertyICalSyncService(
            db,
            Mock.Of<ISafeExternalHttpClient>(),
            new ICalImportService(),
            new ICalExportService(),
            configuration,
            Mock.Of<ILogger<PropertyICalSyncService>>());
    }

    private BookingsController CreateControllerAt(DateTimeOffset utcNow)
    {
        var controller = CreateController(EmailTestHelpers.Links("https://public.test"), new FixedTimeProvider(utcNow));
        var identity = new ClaimsIdentity(new[] { new Claim("sub", OwnerId) }, "TestAuth");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
        return controller;
    }

    private Booking SetupAccessibleBooking(BookingStatus status, DateTime checkIn, DateTime checkOut)
    {
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            PropertyId = PropertyId,
            OrgId = OrgId,
            GuestId = Guid.NewGuid(),
            Status = status,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            NumberOfGuests = 2,
        };
        _mockBookingService.Setup(b => b.GetBookingAsync(booking.Id)).ReturnsAsync(booking);
        _mockBookingService.Setup(b => b.UpdateBookingAsync(It.IsAny<Booking>())).ReturnsAsync((Booking b) => b);
        _mockAuthz.Setup(a => a.CanAccessPropertyAsync(OwnerId, PropertyId, It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(true);
        _mockPropertyService.Setup(p => p.GetPropertyAsync(PropertyId)).ReturnsAsync(MakeProperty());
        return booking;
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
        _mockTaxService.Setup(t => t.CalculateTouristTaxAsync(PropertyId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), 2))
            .ReturnsAsync(12m);
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
        Assert.Equal(462m, stored.TotalPrice);
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

    [Fact]
    public async Task Update_WhenPayloadChangesStatus_PreservesExistingStatus()
    {
        SetUser(OwnerId);
        var bookingId = Guid.NewGuid();
        var guestId = Guid.NewGuid();
        var existing = new Booking
        {
            Id = bookingId,
            PropertyId = PropertyId,
            OrgId = OrgId,
            GuestId = guestId,
            Status = BookingStatus.Pending,
            CheckInDate = DateTime.UtcNow.Date.AddDays(5),
            CheckOutDate = DateTime.UtcNow.Date.AddDays(7),
            NumberOfGuests = 2,
            Source = BookingSource.Direct,
        };
        var payload = new Booking
        {
            Id = Guid.NewGuid(),
            PropertyId = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            GuestId = guestId,
            Status = BookingStatus.CheckedOut,
            CheckInDate = existing.CheckInDate,
            CheckOutDate = existing.CheckOutDate,
            NumberOfGuests = existing.NumberOfGuests,
            Source = BookingSource.Direct,
        };

        _mockBookingService.Setup(b => b.GetBookingAsync(bookingId)).ReturnsAsync(existing);
        _mockAuthz.Setup(a => a.CanAccessPropertyAsync(OwnerId, PropertyId, It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(true);
        _mockBookingService.Setup(b => b.UpdateBookingAsync(It.IsAny<Booking>()))
            .ReturnsAsync((Booking b) => b);

        var result = await _controller.Update(bookingId, payload);

        Assert.IsType<NoContentResult>(result);
        _mockBookingService.Verify(b => b.UpdateBookingAsync(It.Is<Booking>(updated =>
            updated.Id == bookingId &&
            updated.PropertyId == existing.PropertyId &&
            updated.OrgId == existing.OrgId &&
            updated.Status == BookingStatus.Pending)), Times.Once);
    }

    [Fact]
    public async Task Update_PreservesServerOwnedGuestPaymentAndLifecycleFields()
    {
        SetUser(OwnerId);
        var bookingId = Guid.NewGuid();
        var originalGuestId = Guid.NewGuid();
        var maliciousGuestId = Guid.NewGuid();
        var existing = new Booking
        {
            Id = bookingId,
            PropertyId = PropertyId,
            OrgId = OrgId,
            GuestId = originalGuestId,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            ExternalId = "original-external",
            BasePrice = 300m,
            TouristTax = 12m,
            TouristTaxAmount = 12m,
            TotalPrice = 312m,
            PaymentOption = PaymentOption.OnCancellationDeadline,
            FreeRefundDeadline = DateTime.UtcNow.Date.AddDays(5),
            StripeSetupIntentId = "seti_original",
            StripePaymentMethodId = "pm_original",
            StripeCustomerId = "cus_original",
            CheckInToken = Guid.NewGuid(),
            CheckInTokenExpiresAt = DateTime.UtcNow.AddDays(10),
            CheckoutReminderJobId = "reminder-original",
            CheckoutWizardStartedAt = DateTime.UtcNow.AddDays(-1),
            CreatedAt = DateTime.UtcNow.AddDays(-3),
            CheckInDate = DateTime.UtcNow.Date.AddDays(7),
            CheckOutDate = DateTime.UtcNow.Date.AddDays(10),
            NumberOfGuests = 2,
        };
        var update = new Booking
        {
            Id = Guid.NewGuid(),
            PropertyId = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            GuestId = maliciousGuestId,
            Status = BookingStatus.CheckedOut,
            Source = BookingSource.Airbnb,
            ExternalId = "tampered-external",
            BasePrice = 9000m,
            TouristTax = 999m,
            TouristTaxAmount = 999m,
            TotalPrice = 9999m,
            PaymentOption = PaymentOption.OnCancellationDeadline,
            FreeRefundDeadline = DateTime.UtcNow.Date.AddDays(-1),
            StripeSetupIntentId = "seti_tampered",
            StripePaymentMethodId = "pm_tampered",
            StripeCustomerId = "cus_tampered",
            CheckInToken = Guid.NewGuid(),
            CheckInTokenExpiresAt = DateTime.UtcNow.AddYears(1),
            CheckoutReminderJobId = "reminder-tampered",
            CheckoutWizardStartedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            CheckInDate = existing.CheckInDate,
            CheckOutDate = existing.CheckOutDate,
            NumberOfGuests = 3,
            SpecialRequests = "late checkout",
        };

        Booking? saved = null;
        _mockBookingService.Setup(b => b.GetBookingAsync(bookingId)).ReturnsAsync(existing);
        _mockAuthz.Setup(a => a.CanAccessPropertyAsync(OwnerId, PropertyId, It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(true);
        _mockBookingService.Setup(b => b.UpdateBookingAsync(It.IsAny<Booking>()))
            .Callback<Booking>(b => saved = b)
            .ReturnsAsync((Booking b) => b);

        var result = await _controller.Update(bookingId, update);

        Assert.IsType<NoContentResult>(result);
        Assert.NotNull(saved);
        Assert.Equal(bookingId, saved!.Id);
        Assert.Equal(PropertyId, saved.PropertyId);
        Assert.Equal(OrgId, saved.OrgId);
        Assert.Equal(originalGuestId, saved.GuestId);
        Assert.Equal(existing.Status, saved.Status);
        Assert.Equal(existing.Source, saved.Source);
        Assert.Equal(existing.ExternalId, saved.ExternalId);
        Assert.Equal(existing.BasePrice, saved.BasePrice);
        Assert.Equal(existing.TouristTax, saved.TouristTax);
        Assert.Equal(existing.TouristTaxAmount, saved.TouristTaxAmount);
        Assert.Equal(existing.TotalPrice, saved.TotalPrice);
        Assert.Equal(existing.PaymentOption, saved.PaymentOption);
        Assert.Equal(existing.FreeRefundDeadline, saved.FreeRefundDeadline);
        Assert.Equal(existing.StripeSetupIntentId, saved.StripeSetupIntentId);
        Assert.Equal(existing.StripePaymentMethodId, saved.StripePaymentMethodId);
        Assert.Equal(existing.StripeCustomerId, saved.StripeCustomerId);
        Assert.Equal(existing.CheckInToken, saved.CheckInToken);
        Assert.Equal(existing.CheckInTokenExpiresAt, saved.CheckInTokenExpiresAt);
        Assert.Equal(existing.CheckoutReminderJobId, saved.CheckoutReminderJobId);
        Assert.Equal(existing.CheckoutWizardStartedAt, saved.CheckoutWizardStartedAt);
        Assert.Equal(existing.CreatedAt, saved.CreatedAt);
        Assert.Equal("late checkout", saved.SpecialRequests);
    }

    [Fact]
    public async Task CheckIn_PendingBooking_ReturnsBadRequestWithoutMutating()
    {
        SetUser(OwnerId);
        var bookingId = Guid.NewGuid();
        var booking = new Booking
        {
            Id = bookingId,
            PropertyId = PropertyId,
            OrgId = OrgId,
            GuestId = Guid.NewGuid(),
            Status = BookingStatus.Pending,
            CheckInDate = DateTime.UtcNow.Date,
            CheckOutDate = DateTime.UtcNow.Date.AddDays(2),
            NumberOfGuests = 2,
        };

        _mockBookingService.Setup(b => b.GetBookingAsync(bookingId)).ReturnsAsync(booking);
        _mockAuthz.Setup(a => a.CanAccessPropertyAsync(OwnerId, PropertyId, It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(true);
        _mockPropertyService.Setup(p => p.GetPropertyAsync(PropertyId))
            .ReturnsAsync(MakeProperty());

        var result = await _controller.CheckIn(bookingId);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(BookingStatus.Pending, booking.Status);
        _mockBookingService.Verify(b => b.UpdateBookingAsync(It.IsAny<Booking>()), Times.Never);
        _mockAlloggiatiScheduler.Verify(s => s.EnsureScheduledAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task CheckIn_AfterMidnightInRomeOnCheckInDay_ReturnsOk()
    {
        // 22:30 UTC on 30/09 is 00:30 on 01/10 in Rome: the check-in day has started.
        var controller = CreateControllerAt(new DateTimeOffset(2026, 9, 30, 22, 30, 0, TimeSpan.Zero));
        var booking = SetupAccessibleBooking(
            BookingStatus.Confirmed,
            new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc));

        var result = await controller.CheckIn(booking.Id);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(BookingStatus.CheckedIn, booking.Status);
    }

    [Fact]
    public async Task CheckIn_LateEveningInRomeBeforeCheckInDay_ReturnsBadRequest()
    {
        // 21:30 UTC on 30/09 is 23:30 on 30/09 in Rome: check-in (01/10) not reached yet.
        var controller = CreateControllerAt(new DateTimeOffset(2026, 9, 30, 21, 30, 0, TimeSpan.Zero));
        var booking = SetupAccessibleBooking(
            BookingStatus.Confirmed,
            new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc));

        var result = await controller.CheckIn(booking.Id);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(BookingStatus.Confirmed, booking.Status);
    }

    [Fact]
    public async Task CheckOut_AfterMidnightInRomeOnCheckOutDay_ReturnsOk()
    {
        var controller = CreateControllerAt(new DateTimeOffset(2026, 10, 2, 22, 15, 0, TimeSpan.Zero));
        var booking = SetupAccessibleBooking(
            BookingStatus.CheckedIn,
            new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc));

        var result = await controller.CheckOut(booking.Id);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(BookingStatus.CheckedOut, booking.Status);
    }

    [Fact]
    public async Task CheckIn_ConfirmedBooking_RecordsArrivalAndSchedulesAlloggiatiOnce()
    {
        var utcNow = new DateTimeOffset(2026, 10, 10, 14, 30, 0, TimeSpan.Zero);
        var controller = CreateControllerAt(utcNow);
        var bookingId = Guid.NewGuid();
        var booking = new Booking
        {
            Id = bookingId,
            PropertyId = PropertyId,
            OrgId = OrgId,
            GuestId = Guid.NewGuid(),
            Status = BookingStatus.Confirmed,
            CheckInDate = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc),
            NumberOfGuests = 2,
        };

        _mockBookingService.Setup(b => b.GetBookingAsync(bookingId)).ReturnsAsync(booking);
        _mockAuthz.Setup(a => a.CanAccessPropertyAsync(OwnerId, PropertyId, It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(true);
        _mockBookingService.Setup(b => b.UpdateBookingAsync(It.IsAny<Booking>()))
            .ReturnsAsync((Booking b) => b);
        _mockPropertyService.Setup(p => p.GetPropertyAsync(PropertyId))
            .ReturnsAsync(MakeProperty());

        var result = await controller.CheckIn(bookingId);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(utcNow.UtcDateTime, booking.ArrivedAt);
        // The scheduler is idempotent per booking and guest: never a direct Enqueue from the controller.
        _mockAlloggiatiScheduler.Verify(s => s.EnsureScheduledAsync(bookingId), Times.Once);
    }

    [Fact]
    public async Task ResendCheckInLink_ValidBooking_QueuesEmailWithLinkFromConfigAndExpiresPreviousSessions()
    {
        SetUser(OwnerId);
        var bookingId = Guid.NewGuid();
        var guest = new Guest
        {
            Id = Guid.NewGuid(),
            FirstName = "Mario",
            LastName = "Rossi",
            Email = "mario@example.com",
        };
        var booking = new Booking
        {
            Id = bookingId,
            PropertyId = PropertyId,
            OrgId = OrgId,
            GuestId = guest.Id,
            Guest = guest,
            CheckInDate = DateTime.UtcNow.Date.AddDays(1),
            Status = BookingStatus.Confirmed,
        };

        _mockBookingService.Setup(b => b.GetBookingAsync(bookingId)).ReturnsAsync(booking);
        _mockPropertyService.Setup(p => p.GetPropertyAsync(PropertyId)).ReturnsAsync(MakeProperty());
        _mockAuthz.Setup(a => a.CanAccess(OwnerId, OwnerId, It.IsAny<IEnumerable<string>>())).Returns(true);
        _mockGuestCheckInService
            .Setup(s => s.CreateSessionAsync(bookingId, OrgId))
            .ReturnsAsync("new-token");
        _mockGuestCheckInService
            .Setup(s => s.ExpireOtherActiveSessionsAsync(bookingId, "new-token"))
            .Returns(Task.CompletedTask);

        var result = await _controller.ResendCheckInLink(bookingId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<Casazen.Web.DTOs.CheckIn.ResendCheckInLinkResponse>(ok.Value);
        Assert.True(response.Success);
        Assert.Equal("https://public.test/checkin/new-token", response.CheckInLink);
        var (to, content, template) = Assert.Single(_queuedEmails);
        Assert.Equal(guest.Email, to);
        Assert.Equal(EmailTemplates.Names.GuestCheckInLink, template);
        Assert.Contains("Test Villa", content.Subject);
        Assert.Contains("href=\"https://public.test/checkin/new-token\"", content.HtmlBody);
        _mockGuestCheckInService.Verify(s => s.ExpireTokenAsync(It.IsAny<string>()), Times.Never);
        _mockGuestCheckInService.Verify(s => s.ExpireOtherActiveSessionsAsync(bookingId, "new-token"), Times.Once);
    }

    [Fact]
    public async Task ResendCheckInLink_WhenSessionAlreadyComplete_ReturnsConflictWithoutCreatingToken()
    {
        SetUser(OwnerId);
        var bookingId = Guid.NewGuid();
        var guest = new Guest
        {
            Id = Guid.NewGuid(),
            FirstName = "Mario",
            LastName = "Rossi",
            Email = "mario@example.com",
        };
        var booking = new Booking
        {
            Id = bookingId,
            PropertyId = PropertyId,
            OrgId = OrgId,
            GuestId = guest.Id,
            Guest = guest,
            CheckInDate = DateTime.UtcNow.Date.AddDays(1),
            Status = BookingStatus.Confirmed,
        };

        _mockBookingService.Setup(b => b.GetBookingAsync(bookingId)).ReturnsAsync(booking);
        _mockPropertyService.Setup(p => p.GetPropertyAsync(PropertyId)).ReturnsAsync(MakeProperty());
        _mockAuthz.Setup(a => a.CanAccess(OwnerId, OwnerId, It.IsAny<IEnumerable<string>>())).Returns(true);
        _mockGuestCheckInService
            .Setup(s => s.GetSessionForBookingAsync(bookingId))
            .ReturnsAsync(new GuestCheckInSession
            {
                BookingId = bookingId,
                OrgId = OrgId,
                Status = GuestCheckInSessionStatus.Completo,
                CompletedAt = DateTime.UtcNow,
            });

        var result = await _controller.ResendCheckInLink(bookingId);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        var response = Assert.IsType<Casazen.Web.DTOs.CheckIn.ResendCheckInLinkResponse>(conflict.Value);
        Assert.False(response.Success);
        _mockGuestCheckInService.Verify(s => s.CreateSessionAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
        Assert.Empty(_queuedEmails);
    }

    [Fact]
    public async Task ResendCheckInLink_PendingBooking_ReturnsConflictWithoutCreatingToken()
    {
        SetUser(OwnerId);
        var bookingId = Guid.NewGuid();
        var guest = new Guest
        {
            Id = Guid.NewGuid(),
            FirstName = "Mario",
            LastName = "Rossi",
            Email = "mario@example.com",
        };
        var booking = new Booking
        {
            Id = bookingId,
            PropertyId = PropertyId,
            OrgId = OrgId,
            GuestId = guest.Id,
            Guest = guest,
            CheckInDate = DateTime.UtcNow.Date.AddDays(1),
            Status = BookingStatus.Pending,
        };

        _mockBookingService.Setup(b => b.GetBookingAsync(bookingId)).ReturnsAsync(booking);
        _mockPropertyService.Setup(p => p.GetPropertyAsync(PropertyId)).ReturnsAsync(MakeProperty());
        _mockAuthz.Setup(a => a.CanAccess(OwnerId, OwnerId, It.IsAny<IEnumerable<string>>())).Returns(true);

        var result = await _controller.ResendCheckInLink(bookingId);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        var response = Assert.IsType<Casazen.Web.DTOs.CheckIn.ResendCheckInLinkResponse>(conflict.Value);
        Assert.False(response.Success);
        _mockGuestCheckInService.Verify(s => s.CreateSessionAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
        Assert.Empty(_queuedEmails);
    }

    [Fact]
    public async Task ResendCheckInLink_EmailNotQueued_KeepsSessionAndReturnsLink()
    {
        SetUser(OwnerId);
        var bookingId = Guid.NewGuid();
        var guest = new Guest
        {
            Id = Guid.NewGuid(),
            FirstName = "Mario",
            LastName = "Rossi",
            Email = "mario@example.com",
        };
        var booking = new Booking
        {
            Id = bookingId,
            PropertyId = PropertyId,
            OrgId = OrgId,
            GuestId = guest.Id,
            Guest = guest,
            CheckInDate = DateTime.UtcNow.Date.AddDays(1),
            Status = BookingStatus.Confirmed,
        };

        _mockBookingService.Setup(b => b.GetBookingAsync(bookingId)).ReturnsAsync(booking);
        _mockPropertyService.Setup(p => p.GetPropertyAsync(PropertyId)).ReturnsAsync(MakeProperty());
        _mockAuthz.Setup(a => a.CanAccess(OwnerId, OwnerId, It.IsAny<IEnumerable<string>>())).Returns(true);
        _mockGuestCheckInService
            .Setup(s => s.CreateSessionAsync(bookingId, OrgId))
            .ReturnsAsync("new-token");
        _mockEmailQueue
            .Setup(q => q.Enqueue(guest.Email, It.IsAny<EmailContent>(), It.IsAny<string>()))
            .Returns(false);
        _mockGuestCheckInService
            .Setup(s => s.ExpireOtherActiveSessionsAsync(bookingId, "new-token"))
            .Returns(Task.CompletedTask);

        var previousCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo("it-IT");
        ActionResult<Casazen.Web.DTOs.CheckIn.ResendCheckInLinkResponse> result;
        try
        {
            result = await _controller.ResendCheckInLink(bookingId);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<Casazen.Web.DTOs.CheckIn.ResendCheckInLinkResponse>(ok.Value);
        Assert.True(response.Success);
        Assert.Contains("/checkin/new-token", response.CheckInLink);
        Assert.Contains("copia il link", response.Message);
        _mockGuestCheckInService.Verify(s => s.ExpireTokenAsync(It.IsAny<string>()), Times.Never);
        _mockGuestCheckInService.Verify(s => s.ExpireOtherActiveSessionsAsync(bookingId, "new-token"), Times.Once);
    }

    [Fact]
    public async Task ResendCheckInLink_PublicSiteBaseUrlMissing_ThrowsConfigurationErrorWithoutCreatingSession()
    {
        var controller = CreateController(EmailTestHelpers.Links(null));
        var identity = new ClaimsIdentity(new[] { new Claim("sub", OwnerId) }, "TestAuth");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
        var bookingId = Guid.NewGuid();
        var guest = new Guest { Id = Guid.NewGuid(), FirstName = "Mario", LastName = "Rossi", Email = "mario@example.com" };
        _mockBookingService.Setup(b => b.GetBookingAsync(bookingId)).ReturnsAsync(new Booking
        {
            Id = bookingId,
            PropertyId = PropertyId,
            OrgId = OrgId,
            GuestId = guest.Id,
            Guest = guest,
            CheckInDate = DateTime.UtcNow.Date.AddDays(1),
            Status = BookingStatus.Confirmed,
        });
        _mockPropertyService.Setup(p => p.GetPropertyAsync(PropertyId)).ReturnsAsync(MakeProperty());
        _mockAuthz.Setup(a => a.CanAccess(OwnerId, OwnerId, It.IsAny<IEnumerable<string>>())).Returns(true);

        await Assert.ThrowsAsync<EmailConfigurationException>(() => controller.ResendCheckInLink(bookingId));

        _mockGuestCheckInService.Verify(s => s.CreateSessionAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
        Assert.Empty(_queuedEmails);
    }

    [Fact]
    public async Task Cancel_WhenBookingHasCheckoutReminder_CancelsScheduledReminder()
    {
        SetUser(OwnerId);
        var bookingId = Guid.NewGuid();
        var booking = new Booking
        {
            Id = bookingId,
            PropertyId = PropertyId,
            OrgId = OrgId,
            Status = BookingStatus.CheckedIn,
            CheckoutReminderJobId = "checkout-reminder-job",
            CheckInDate = DateTime.UtcNow.Date.AddDays(-1),
            CheckOutDate = DateTime.UtcNow.Date,
            NumberOfGuests = 2,
        };

        _mockBookingService.Setup(b => b.GetBookingAsync(bookingId)).ReturnsAsync(booking);
        _mockAuthz.Setup(a => a.CanAccessPropertyAsync(OwnerId, PropertyId, It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(true);
        _mockBookingService.Setup(b => b.CancelBookingAsync(bookingId)).ReturnsAsync(true);

        var result = await _controller.Cancel(bookingId);

        Assert.IsType<NoContentResult>(result);
        _mockCheckoutReminderScheduler.Verify(s => s.CancelReminder("checkout-reminder-job"), Times.Once);
    }

    [Fact]
    public async Task StartCheckoutWizard_WhenUnauthorizedForProperty_ReturnsNotFoundWithoutStartingWizard()
    {
        SetUser(OwnerId);
        var bookingId = Guid.NewGuid();
        var booking = new Booking
        {
            Id = bookingId,
            PropertyId = PropertyId,
            OrgId = OrgId,
            Status = BookingStatus.CheckedIn,
            CheckOutDate = DateTime.UtcNow.Date,
            NumberOfGuests = 2,
        };

        _mockBookingService.Setup(b => b.GetBookingAsync(bookingId)).ReturnsAsync(booking);
        _mockAuthz.Setup(a => a.CanAccessPropertyAsync(OwnerId, PropertyId, It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(false);

        var result = await _controller.StartCheckoutWizard(bookingId);

        Assert.IsType<NotFoundResult>(result.Result);
        _mockComplianceWizardService.Verify(
            s => s.StartCheckoutWizardAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CompleteCheckoutWizard_WhenUnauthorizedForProperty_ReturnsNotFoundWithoutCompletingWizard()
    {
        SetUser(OwnerId);
        var bookingId = Guid.NewGuid();
        var booking = new Booking
        {
            Id = bookingId,
            PropertyId = PropertyId,
            OrgId = OrgId,
            Status = BookingStatus.CheckedIn,
            CheckOutDate = DateTime.UtcNow.Date,
            NumberOfGuests = 2,
        };

        _mockBookingService.Setup(b => b.GetBookingAsync(bookingId)).ReturnsAsync(booking);
        _mockAuthz.Setup(a => a.CanAccessPropertyAsync(OwnerId, PropertyId, It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(false);

        var result = await _controller.CompleteCheckoutWizard(
            bookingId,
            new CompleteCheckoutWizardRequest { ConfirmDeparture = true });

        Assert.IsType<NotFoundResult>(result.Result);
        _mockComplianceWizardService.Verify(
            s => s.CompleteCheckoutWizardAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<CompleteCheckoutWizardInput>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _mockCheckoutReminderScheduler.Verify(s => s.CancelReminder(It.IsAny<string?>()), Times.Never);
    }
}
