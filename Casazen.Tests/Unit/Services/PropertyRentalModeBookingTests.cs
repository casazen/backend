using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// PM-01: a property in long-term mode takes no stay. The public checkout and its quote, the host's price of a stay (quote,
/// new and changed manual booking) and the creation of a manual booking refuse it with 422
/// <c>property_not_bookable_in_long_mode</c> (localized key <c>PropertyNotBookableInLongMode</c>) before anything is
/// written; a short-rent property keeps answering as before (404 when it is not published).
/// </summary>
public class PropertyRentalModeBookingTests
{
    private const string ConsentVersion = "2026-06-direct-checkout-v1";

    private readonly Mock<IBookingRepository> _bookings = new();
    private readonly Mock<IGuestRepository> _guests = new();
    private readonly Mock<IPropertyRepository> _properties = new();
    private readonly Mock<IOrgService> _orgs = new();
    private readonly Mock<ICheckoutHoldExpiryService> _holdExpiry = new();
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    private readonly BookingService _service;

    public PropertyRentalModeBookingTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DirectBooking:ConsentVersion"] = ConsentVersion,
                ["DirectBooking:PendingTtlMinutes"] = "15",
                ["Stripe:PublishableKey"] = "pk_test",
            })
            .Build();

        _service = new BookingService(
            _bookings.Object,
            _properties.Object,
            _orgs.Object,
            _guests.Object,
            new Mock<ITouristTaxQuoteService>().Object,
            new Mock<IStripeService>().Object,
            new Mock<IPaymentRepository>().Object,
            ICalTestServices.PropertySync(_db, Mock.Of<ISafeExternalHttpClient>(), configuration),
            configuration,
            new Mock<ILogger<BookingService>>().Object,
            _holdExpiry.Object,
            new OnSiteRequestNotifier(
                _db, new RecordingEmailQueue(), EmailTestHelpers.Links(), Mock.Of<ILogger<OnSiteRequestNotifier>>()));
    }

    [Theory]
    [InlineData(PropertyComplianceStatus.Active)]
    [InlineData(PropertyComplianceStatus.Pending)]
    public async Task CreateDirectBookingAsync_LongProperty_Is422BeforeAnythingIsWritten(PropertyComplianceStatus compliance)
    {
        var property = StoredProperty(RentalMode.Long, compliance);
        var input = CheckoutInput(property.Id);

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => _service.CreateDirectBookingAsync(input));

        AssertNotBookableInLongMode(error);
        _orgs.Verify(o => o.GetByIdAsync(It.IsAny<Guid>()), Times.Never);
        _bookings.Verify(b => b.AddAsync(It.IsAny<Booking>()), Times.Never);
        _holdExpiry.Verify(
            h => h.ExpireOverlappingHoldsAsync(
                It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task QuoteDirectBookingAsync_LongProperty_Is422()
    {
        var property = StoredProperty(RentalMode.Long, PropertyComplianceStatus.Active);

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => _service.QuoteDirectBookingAsync(
            new DirectBookingQuoteInput(property.Id, Today(10), Today(12), 2, 0, null)));

        AssertNotBookableInLongMode(error);
    }

    [Fact]
    public async Task QuoteDirectBookingAsync_ShortPropertyNotPublished_IsStillNotFound()
    {
        // The old answers do not change: only a long-term property gets the reason.
        var property = StoredProperty(RentalMode.Short, PropertyComplianceStatus.Pending);

        await Assert.ThrowsAsync<NotFoundException>(() => _service.QuoteDirectBookingAsync(
            new DirectBookingQuoteInput(property.Id, Today(10), Today(12), 2, 0, null)));
    }

    [Fact]
    public async Task QuoteDirectBookingAsync_UnknownProperty_IsStillNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _service.QuoteDirectBookingAsync(
            new DirectBookingQuoteInput(Guid.NewGuid(), Today(10), Today(12), 2, 0, null)));
    }

    [Fact]
    public async Task PriceHostStayAsync_LongProperty_Is422EvenBeforeTheGuestsCheck()
    {
        // A long-term property has no guest capacity (0): "too many guests" would hide the reason.
        var property = new Property { Id = Guid.NewGuid(), RentalMode = RentalMode.Long, MaxGuests = 0 };

        var error = await Assert.ThrowsAsync<DomainRuleException>(() =>
            _service.PriceHostStayAsync(property, Today(10), Today(12), 2, 0, null));

        AssertNotBookableInLongMode(error);
    }

    [Fact]
    public async Task PriceHostStayAsync_ShortPropertyWithoutCapacity_KeepsTheTooManyGuestsAnswer()
    {
        var property = new Property { Id = Guid.NewGuid(), RentalMode = RentalMode.Short, MaxGuests = 1 };

        var error = await Assert.ThrowsAsync<DomainRuleException>(() =>
            _service.PriceHostStayAsync(property, Today(10), Today(12), 2, 0, null));

        Assert.Equal(BookingErrorCodes.TooManyGuests, error.Code);
    }

    [Fact]
    public async Task CreateManualBookingAsync_LongProperty_Is422AndStoresNeitherBookingNorGuest()
    {
        var property = StoredProperty(RentalMode.Long, PropertyComplianceStatus.Pending);
        _properties.Setup(p => p.GetRecordAsync(property.Id)).ReturnsAsync(property);
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            CheckInDate = Today(10),
            CheckOutDate = Today(12),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
        };

        var error = await Assert.ThrowsAsync<DomainRuleException>(() =>
            _service.CreateManualBookingAsync(booking, new Guest { FirstName = "Mario", LastName = "Rossi" }));

        AssertNotBookableInLongMode(error);
        _guests.Verify(g => g.AddAsync(It.IsAny<Guest>()), Times.Never);
        _bookings.Verify(b => b.AddAsync(It.IsAny<Booking>()), Times.Never);
    }

    [Fact]
    public async Task CreateManualBookingAsync_ShortProperty_StoresTheBookingAsBefore()
    {
        var property = StoredProperty(RentalMode.Short, PropertyComplianceStatus.Active);
        _properties.Setup(p => p.GetRecordAsync(property.Id)).ReturnsAsync(property);
        _bookings
            .Setup(b => b.IsAvailableAsync(property.Id, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(true);
        _bookings.Setup(b => b.AddAsync(It.IsAny<Booking>())).ReturnsAsync((Booking b) => b);
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            CheckInDate = Today(10),
            CheckOutDate = Today(12),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            TotalPrice = 200m,
            BasePrice = 200m,
        };

        var created = await _service.CreateManualBookingAsync(booking, new Guest { FirstName = "Mario", LastName = "Rossi" });

        Assert.Equal(BookingStatus.Confirmed, created.Status);
        _bookings.Verify(b => b.AddAsync(It.IsAny<Booking>()), Times.Once);
    }

    [Fact]
    public async Task CreateManualBookingAsync_PropertyNotFound_IsLeftToTheOtherChecks()
    {
        // The guard needs the property; a missing one is not its business (the booking checks and the key answer).
        _properties.Setup(p => p.GetRecordAsync(It.IsAny<Guid>())).ReturnsAsync((Property?)null);
        var booking = new Booking
        {
            PropertyId = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            CheckInDate = Today(10),
            CheckOutDate = Today(12),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
        };
        _bookings
            .Setup(b => b.IsAvailableAsync(booking.PropertyId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(true);
        _bookings.Setup(b => b.AddAsync(It.IsAny<Booking>())).ReturnsAsync((Booking b) => b);

        var created = await _service.CreateManualBookingAsync(booking, new Guest { FirstName = "Mario", LastName = "Rossi" });

        Assert.Equal(BookingStatus.Confirmed, created.Status);
    }

    private static void AssertNotBookableInLongMode(DomainRuleException error)
    {
        Assert.Equal(PropertyRentalModeErrorCodes.NotBookableInLongMode, error.Code);
        Assert.Equal("property_not_bookable_in_long_mode", error.Code);
        Assert.Equal("PropertyNotBookableInLongMode", error.MessageKey);
    }

    /// <summary>A stored property that is otherwise bookable on the site (active, not paused, a rate and a capacity).</summary>
    private Property StoredProperty(RentalMode mode, PropertyComplianceStatus compliance)
    {
        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            Name = "Casa PM-01",
            IsActive = true,
            MaxGuests = 4,
            NightlyRate = 100m,
            RentalMode = mode,
            ComplianceStatus = compliance,
        };
        _properties.Setup(p => p.GetByIdAsync(property.Id)).ReturnsAsync(property);
        return property;
    }

    private static DateTime Today(int plusDays) => TimeProvider.System.TodayInRome().AddDays(plusDays);

    private static DirectBookingCreateInput CheckoutInput(Guid propertyId) => new(
        propertyId,
        Today(10),
        Today(12),
        2,
        0,
        new DirectBookingGuestInput("Ada", "Lovelace", "ada@example.com", null, "IT"),
        ConsentVersion,
        "127.0.0.1",
        null,
        PaymentOption.Immediate);
}
