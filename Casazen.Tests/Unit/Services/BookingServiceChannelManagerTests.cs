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
/// E08/F01/US02/T01 (NF-74, NF-80, AC-NF80-1): the final check on the dates of a booking of CasaZen asks the channel manager as well, before
/// anything is written. Octorate answering "not available" is the 409 of a night CasaZen has taken, for the guest's checkout and for the host's
/// manual booking; a channel manager that does not refuse, or cannot be asked, never stops a booking; and CasaZen's own check comes first, so a
/// night CasaZen already has taken costs no call of the quota.
/// </summary>
public class BookingServiceChannelManagerTests
{
    private readonly Mock<IBookingRepository> _repository = new();
    private readonly Mock<IGuestRepository> _guests = new();
    private readonly Mock<IPropertyRepository> _properties = new();
    private readonly Mock<IOrgService> _orgs = new();
    private readonly Mock<ITouristTaxQuoteService> _touristTax = new();
    private readonly Mock<IChannelManagerAvailability> _channelManager = new();
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);
    private readonly RecordingEmailQueue _emails = new();
    private readonly BookingService _service;

    public BookingServiceChannelManagerTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DirectBooking:ConsentVersion"] = "2026-06-direct-checkout-v1",
                ["DirectBooking:PendingTtlMinutes"] = "15",
                ["DirectBooking:OnSiteMaxNights"] = "14",
                ["Stripe:PublishableKey"] = "pk_test",
            })
            .Build();
        var pricing = new Mock<IPricingAdapterService>();
        pricing
            .Setup(s => s.GetAppliedNightlyPricesAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<DateOnly, decimal>());

        _service = new BookingService(
            _repository.Object,
            _properties.Object,
            _orgs.Object,
            _guests.Object,
            _touristTax.Object,
            new Mock<IStripeService>().Object,
            new Mock<IPaymentRepository>().Object,
            ICalTestServices.PropertySync(_db, Mock.Of<ISafeExternalHttpClient>(), configuration),
            configuration,
            new Mock<ILogger<BookingService>>().Object,
            new Mock<ICheckoutHoldExpiryService>().Object,
            new OnSiteRequestNotifier(_db, _emails, EmailTestHelpers.Links(), Mock.Of<ILogger<OnSiteRequestNotifier>>()),
            pricing.Object,
            channelManager: _channelManager.Object,
            dayRules: NoDayRules.Instance);
    }

    // --- The host's manual booking -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateManualBookingAsync_OctorateSaysNotAvailable_ThrowsTheConflictOfATakenNightAndStoresNothing()
    {
        var booking = ManualBooking();
        CasaZenSaysFree();
        ChannelManagerSays(ChannelManagerAnswer.NotAvailable);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => _service.CreateManualBookingAsync(booking, new Guest()));

        Assert.Equal(BookingErrorCodes.DatesUnavailable, ex.Code);
        _guests.Verify(x => x.AddAsync(It.IsAny<Guest>()), Times.Never);
        _repository.Verify(x => x.AddAsync(It.IsAny<Booking>()), Times.Never);
        // The property, the org and the nights asked are the booking's, and the guests are the ones who come.
        _channelManager.Verify(
            c => c.CheckAsync(
                booking.PropertyId,
                booking.OrgId,
                DateOnly.FromDateTime(booking.CheckInDate),
                DateOnly.FromDateTime(booking.CheckOutDate),
                2,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(ChannelManagerAnswer.Available)]
    [InlineData(ChannelManagerAnswer.Unanswered)]
    [InlineData(ChannelManagerAnswer.NotMirrored)]
    public async Task CreateManualBookingAsync_AChannelManagerThatDoesNotRefuse_NeverStopsTheBooking(ChannelManagerAnswer answer)
    {
        var booking = ManualBooking();
        CasaZenSaysFree();
        ChannelManagerSays(answer);
        _guests.Setup(x => x.AddAsync(It.IsAny<Guest>())).ReturnsAsync((Guest g) => g);
        _repository.Setup(x => x.AddAsync(It.IsAny<Booking>())).ReturnsAsync((Booking b) => b);

        var created = await _service.CreateManualBookingAsync(booking, new Guest { FirstName = "Mario", LastName = "Rossi", Email = "mario@example.com" });

        Assert.Equal(BookingStatus.Confirmed, created.Status);
        _repository.Verify(x => x.AddAsync(It.IsAny<Booking>()), Times.Once);
    }

    [Fact]
    public async Task CreateManualBookingAsync_CasaZenAlreadyHasTheNightsTaken_TheChannelManagerIsNotAsked()
    {
        var booking = ManualBooking();
        _repository
            .Setup(x => x.IsAvailableAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>()))
            .ReturnsAsync(false);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => _service.CreateManualBookingAsync(booking, new Guest()));

        Assert.Equal(BookingErrorCodes.DatesUnavailable, ex.Code);
        _channelManager.Verify(
            c => c.CheckAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // --- The guest's checkout ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateDirectBookingAsync_OctorateSaysNotAvailable_ThrowsTheConflictBeforeTheHoldIsWritten()
    {
        var property = ConnectReadyProperty();
        ChannelManagerSays(ChannelManagerAnswer.NotAvailable);
        var checkIn = TimeProvider.System.TodayInRome().AddDays(30);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => _service.CreateDirectBookingAsync(DirectInput(property.Id, checkIn, checkIn.AddDays(3))));

        Assert.Equal(BookingErrorCodes.DatesUnavailable, ex.Code);
        _repository.Verify(x => x.AddAsync(It.IsAny<Booking>()), Times.Never);
        _repository.Verify(x => x.AddWithGuestConsentsAsync(It.IsAny<Booking>(), It.IsAny<IReadOnlyList<GuestConsentRecord>>()), Times.Never);
        _guests.Verify(x => x.AddAsync(It.IsAny<Guest>()), Times.Never);
        _channelManager.Verify(
            c => c.CheckAsync(
                property.Id,
                property.OrgId,
                DateOnly.FromDateTime(checkIn),
                DateOnly.FromDateTime(checkIn.AddDays(3)),
                2,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateDirectBookingAsync_CasaZenAlreadyHasTheNightsTaken_TheChannelManagerIsNotAsked()
    {
        var property = ConnectReadyProperty();
        _repository
            .Setup(x => x.IsAvailableAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>()))
            .ReturnsAsync(false);
        var checkIn = TimeProvider.System.TodayInRome().AddDays(30);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => _service.CreateDirectBookingAsync(DirectInput(property.Id, checkIn, checkIn.AddDays(3))));

        Assert.Equal(BookingErrorCodes.DatesUnavailable, ex.Code);
        _channelManager.Verify(
            c => c.CheckAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // --- Helpers ------------------------------------------------------------------------------------------------------------------

    private void CasaZenSaysFree() =>
        _repository
            .Setup(x => x.IsAvailableAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>()))
            .ReturnsAsync(true);

    private void ChannelManagerSays(ChannelManagerAnswer answer) =>
        _channelManager
            .Setup(c => c.CheckAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChannelManagerAvailability(answer));

    private static Booking ManualBooking() => new()
    {
        PropertyId = Guid.NewGuid(),
        OrgId = Guid.NewGuid(),
        CheckInDate = TimeProvider.System.TodayInRome().AddDays(10),
        CheckOutDate = TimeProvider.System.TodayInRome().AddDays(14),
        TotalPrice = 500m,
        NumberOfGuests = 2,
    };

    /// <summary>A property whose org can take payments, free on CasaZen's side for any dates.</summary>
    private Property ConnectReadyProperty()
    {
        var property = new Property
        {
            OrgId = Guid.NewGuid(),
            Name = "Villa Rosa",
            IsActive = true,
            ComplianceStatus = PropertyComplianceStatus.Active,
            MaxGuests = 4,
            NightlyRate = 100m,
        };
        _properties.Setup(x => x.GetByIdAsync(property.Id)).ReturnsAsync(property);
        _orgs.Setup(x => x.GetByIdAsync(property.OrgId)).ReturnsAsync(new OrgEntity
        {
            Id = property.OrgId,
            StripeConnectedAccountId = "acct_channel_manager",
            ConnectChargesEnabled = true,
        });
        _touristTax
            .Setup(x => x.QuoteAsync(It.IsAny<TouristTaxComune>(), It.IsAny<TouristTaxStay>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TouristTaxQuote(TouristTaxQuoteStatus.RateUnavailable, null, 10, 0, false, [], []));
        CasaZenSaysFree();
        return property;
    }

    private static DirectBookingCreateInput DirectInput(Guid propertyId, DateTime checkIn, DateTime checkOut) => new(
        propertyId,
        checkIn,
        checkOut,
        2,
        0,
        new DirectBookingGuestInput("Ada", "Lovelace", "guest@example.com", null, "IT"),
        "2026-06-direct-checkout-v1",
        "127.0.0.1",
        null,
        PaymentOption.Immediate);
}
