using Casazen.Core.Entities;
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

public class BookingServiceTests
{
    private readonly Mock<IBookingRepository> _mockRepository;
    private readonly Mock<IGuestRepository> _mockGuestRepository = new();
    private readonly Mock<ICheckoutHoldExpiryService> _mockHoldExpiry = new();
    private readonly Mock<IPropertyRepository> _mockPropertyRepository = new();
    private readonly Mock<IOrgService> _mockOrgService = new();
    private readonly Mock<ITouristTaxQuoteService> _mockTouristTax = new();
    private readonly RecordingEmailQueue _emails = new();
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);
    private readonly BookingService _service;

    public BookingServiceTests()
    {
        _mockRepository = new Mock<IBookingRepository>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DirectBooking:ConsentVersion"] = "2026-06-direct-checkout-v1",
                ["DirectBooking:PendingTtlMinutes"] = "15",
                ["DirectBooking:OnSiteMaxNights"] = "14",
                ["Stripe:PublishableKey"] = "pk_test",
            })
            .Build();

        _service = new BookingService(
            _mockRepository.Object,
            _mockPropertyRepository.Object,
            _mockOrgService.Object,
            _mockGuestRepository.Object,
            _mockTouristTax.Object,
            new Mock<IStripeService>().Object,
            new Mock<IPaymentRepository>().Object,
            CreatePropertyICalSyncService(_db, configuration),
            configuration,
            new Mock<ILogger<BookingService>>().Object,
            _mockHoldExpiry.Object,
            new OnSiteRequestNotifier(
                _db, _emails, EmailTestHelpers.Links(), Mock.Of<ILogger<OnSiteRequestNotifier>>()));
    }

    private static PropertyICalSyncService CreatePropertyICalSyncService(AppDbContext db, IConfiguration configuration)
    {
        return new PropertyICalSyncService(
            db,
            Mock.Of<ISafeExternalHttpClient>(),
            new ICalImportService(),
            new ICalExportService(),
            configuration,
            Mock.Of<ILogger<PropertyICalSyncService>>());
    }

    [Fact]
    public async Task CreateDirectBookingAsync_WithInvalidPaymentOption_RejectsBeforePersisting()
    {
        var input = new DirectBookingCreateInput(
            Guid.NewGuid(),
            DateTime.UtcNow.Date.AddDays(10),
            DateTime.UtcNow.Date.AddDays(12),
            1,
            0,
            new DirectBookingGuestInput(
                "Ada",
                "Lovelace",
                "ada@example.com",
                null,
                "IT"),
            "2026-06-direct-checkout-v1",
            "127.0.0.1",
            null,
            (PaymentOption)999);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            _service.CreateDirectBookingAsync(input));

        Assert.Equal(DirectBookingErrorCodes.InvalidPaymentOption, ex.Code);
        _mockRepository.Verify(x => x.AddAsync(It.IsAny<Booking>()), Times.Never);
        _mockHoldExpiry.Verify(x => x.ExpireOverlappingHoldsAsync(
            It.IsAny<Guid>(),
            It.IsAny<DateTime>(),
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateDirectBookingAsync_OnSiteStayLongerThanConfiguredMaxNights_Throws422RuleBeforePersisting()
    {
        // A3-06: a "pay at the property" request holds dates with no guarantee, so its length is capped
        // (DirectBooking:OnSiteMaxNights, 14 here).
        var property = new Property
        {
            OrgId = Guid.NewGuid(),
            IsActive = true,
            ComplianceStatus = Core.Entities.Enums.PropertyComplianceStatus.Active,
            MaxGuests = 4,
            NightlyRate = 100m,
        };
        _mockPropertyRepository.Setup(x => x.GetByIdAsync(property.Id)).ReturnsAsync(property);
        _mockOrgService.Setup(x => x.GetByIdAsync(property.OrgId)).ReturnsAsync(new OrgEntity
        {
            Id = property.OrgId,
            StripeConnectedAccountId = "acct_onsite",
            ConnectChargesEnabled = true,
        });
        var checkIn = DateTime.UtcNow.Date.AddDays(30);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => _service.CreateDirectBookingAsync(OnSiteInput(
            property.Id, checkIn, checkIn.AddDays(15))));

        Assert.Equal(OnSiteRequestErrorCodes.TooManyNights, ex.Code);
        Assert.Equal("OnSiteRequestTooManyNights", ex.MessageKey);
        Assert.Equal(14, Assert.Single(ex.MessageArgs));
        _mockRepository.Verify(x => x.AddAsync(It.IsAny<Booking>()), Times.Never);
        _mockGuestRepository.Verify(x => x.AddAsync(It.IsAny<Guest>()), Times.Never);
        Assert.Empty(_emails.Queued);
    }

    [Fact]
    public async Task CreateDirectBookingAsync_OnSiteWithinMaxNights_StaysPendingWithEmailTokenAndQueuesConfirmationEmail()
    {
        var property = new Property
        {
            OrgId = Guid.NewGuid(),
            Name = "Villa Rosa",
            IsActive = true,
            ComplianceStatus = Core.Entities.Enums.PropertyComplianceStatus.Active,
            MaxGuests = 4,
            NightlyRate = 100m,
        };
        var org = new OrgEntity
        {
            Id = property.OrgId,
            Slug = "villa-rosa",
            StripeConnectedAccountId = "acct_onsite",
            ConnectChargesEnabled = true,
        };
        _mockPropertyRepository.Setup(x => x.GetByIdAsync(property.Id)).ReturnsAsync(property);
        _mockOrgService.Setup(x => x.GetByIdAsync(property.OrgId)).ReturnsAsync(org);
        _mockTouristTax
            .Setup(x => x.QuoteAsync(It.IsAny<TouristTaxComune>(), It.IsAny<TouristTaxStay>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TouristTaxQuote(TouristTaxQuoteStatus.RateUnavailable, null, 14, 0, false, [], []));
        _mockRepository.Setup(x => x.IsAvailableAsync(
                It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>()))
            .ReturnsAsync(true);
        // The notifier reads the saved request from the database, as in production.
        _mockRepository.Setup(x => x.AddAsync(It.IsAny<Booking>())).ReturnsAsync((Booking b) =>
        {
            _db.Bookings.Add(b);
            _db.SaveChanges();
            return b;
        });
        _mockGuestRepository.Setup(x => x.AddAsync(It.IsAny<Guest>())).ReturnsAsync((Guest g) =>
        {
            _db.Guests.Add(g);
            _db.SaveChanges();
            return g;
        });
        _db.Orgs.Add(org);
        _db.Properties.Add(property);
        await _db.SaveChangesAsync();
        var checkIn = DateTime.UtcNow.Date.AddDays(30);

        var result = await _service.CreateDirectBookingAsync(OnSiteInput(property.Id, checkIn, checkIn.AddDays(14)));

        var stored = await _db.Bookings.AsNoTracking().SingleAsync(b => b.Id == result.BookingId);
        // D5: never confirmed by the checkout.
        Assert.Equal(BookingStatus.Pending, stored.Status);
        Assert.Equal(PaymentOption.OnSite, stored.PaymentOption);
        Assert.Null(stored.GuestEmailVerifiedAt);
        _mockRepository.Verify(x => x.UpdateAsync(It.IsAny<Booking>()), Times.Never);
        // Email confirmation window = the checkout TTL (15 minutes) when OnSiteEmailVerificationMinutes is not set.
        Assert.InRange(stored.RequestExpiresAt!.Value, DateTime.UtcNow.AddMinutes(14), DateTime.UtcNow.AddMinutes(16));
        Assert.Equal(stored.RequestExpiresAt, result.OnSiteRequestExpiresAt);

        var email = Assert.Single(_emails.Queued);
        Assert.Equal("guest@example.com", email.To);
        Assert.Equal("onsite-request-received", email.Template);
        var link = System.Text.RegularExpressions.Regex.Match(
            email.Content.HtmlBody,
            $"href=\"{EmailTestHelpers.PublicSiteBaseUrl}/book/villa-rosa/requests/{stored.Id:D}/confirm\\?token=([A-Za-z0-9_-]+)\"");
        Assert.True(link.Success, "confirmation link missing");
        // Only the hash is stored; the raw token is only in the email.
        Assert.NotEqual(link.Groups[1].Value, stored.GuestEmailVerificationTokenHash);
        Assert.True(OnSiteRequests.EmailVerificationTokenMatches(stored.GuestEmailVerificationTokenHash, link.Groups[1].Value));
        // BK-07: the checkout token of the outcome page is returned once; only its hash is stored.
        Assert.False(string.IsNullOrWhiteSpace(result.CheckoutToken));
        Assert.NotEqual(result.CheckoutToken, stored.CheckoutTokenHash);
        Assert.True(CheckoutOutcomes.TokenMatches(stored.CheckoutTokenHash, result.CheckoutToken));
    }

    [Fact]
    public async Task CreateDirectBookingAsync_DeferredPaymentWithArrivalTomorrow_Throws422RuleBeforePersisting()
    {
        // A3-16: 10 nights from tomorrow used to be offered "Paga alla scadenza" with a deadline already past.
        var property = ConnectReadyProperty();
        var checkIn = TimeProvider.System.TodayInRome().AddDays(1);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => _service.CreateDirectBookingAsync(
            DirectInput(property.Id, checkIn, checkIn.AddDays(10), PaymentOption.OnCancellationDeadline)));

        Assert.Equal(DirectBookingErrorCodes.DeferredPaymentUnavailable, ex.Code);
        Assert.Equal("DirectBookingDeferredPaymentUnavailable", ex.MessageKey);
        _mockRepository.Verify(x => x.AddAsync(It.IsAny<Booking>()), Times.Never);
        _mockGuestRepository.Verify(x => x.AddAsync(It.IsAny<Guest>()), Times.Never);
    }

    [Fact]
    public async Task QuoteDirectBookingAsync_PropertyWithCancellationPolicy_ChargesTheDeferredPaymentOnItsLastFullRefundDay()
    {
        var property = ConnectReadyProperty();
        property.CancellationPolicy = new CancellationPolicy { Name = "Moderata", FullRefundHours = 14 * 24 };
        var checkIn = TimeProvider.System.TodayInRome().AddDays(30);

        var quote = await _service.QuoteDirectBookingAsync(
            new DirectBookingQuoteInput(property.Id, checkIn, checkIn.AddDays(3), 2, 0));

        // 14 days before the start of the check-in day: the last whole day is the 15th day before.
        Assert.Equal(checkIn.AddDays(-15), quote.FreeRefundDeadline);
        Assert.True(quote.PaymentOptions.DeferredPaymentAvailable);
        Assert.Equal(checkIn.AddDays(-15), quote.PaymentOptions.DeferredChargeDate);
        Assert.Null(quote.PaymentOptions.FreeCancellationUntil);
    }

    [Fact]
    public async Task QuoteDirectBookingAsync_ArrivalTomorrowForTenNights_DoesNotOfferTheDeferredPayment()
    {
        var property = ConnectReadyProperty();
        var checkIn = TimeProvider.System.TodayInRome().AddDays(1);

        var quote = await _service.QuoteDirectBookingAsync(
            new DirectBookingQuoteInput(property.Id, checkIn, checkIn.AddDays(10), 2, 0));

        Assert.False(quote.PaymentOptions.DeferredPaymentAvailable);
        Assert.Null(quote.PaymentOptions.DeferredChargeDate);
    }

    private Property ConnectReadyProperty()
    {
        var property = new Property
        {
            OrgId = Guid.NewGuid(),
            Name = "Villa Rosa",
            IsActive = true,
            ComplianceStatus = Core.Entities.Enums.PropertyComplianceStatus.Active,
            MaxGuests = 4,
            NightlyRate = 100m,
        };
        _mockPropertyRepository.Setup(x => x.GetByIdAsync(property.Id)).ReturnsAsync(property);
        _mockOrgService.Setup(x => x.GetByIdAsync(property.OrgId)).ReturnsAsync(new OrgEntity
        {
            Id = property.OrgId,
            StripeConnectedAccountId = "acct_deferred",
            ConnectChargesEnabled = true,
        });
        _mockTouristTax
            .Setup(x => x.QuoteAsync(It.IsAny<TouristTaxComune>(), It.IsAny<TouristTaxStay>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TouristTaxQuote(TouristTaxQuoteStatus.RateUnavailable, null, 10, 0, false, [], []));
        _mockRepository.Setup(x => x.IsAvailableAsync(
                It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>()))
            .ReturnsAsync(true);
        return property;
    }

    private static DirectBookingCreateInput DirectInput(
        Guid propertyId, DateTime checkIn, DateTime checkOut, PaymentOption paymentOption) => new(
        propertyId,
        checkIn,
        checkOut,
        2,
        0,
        new DirectBookingGuestInput("Ada", "Lovelace", "guest@example.com", null, "IT"),
        "2026-06-direct-checkout-v1",
        "127.0.0.1",
        null,
        paymentOption);

    private static DirectBookingCreateInput OnSiteInput(Guid propertyId, DateTime checkIn, DateTime checkOut) => new(
        propertyId,
        checkIn,
        checkOut,
        2,
        0,
        new DirectBookingGuestInput("Ada", "Lovelace", "guest@example.com", null, "IT"),
        "2026-06-direct-checkout-v1",
        "127.0.0.1",
        null,
        PaymentOption.OnSite);

    [Fact]
    public async Task CreateManualBookingAsync_WithValidBooking_StoresConfirmedManualBookingWithGuestSnapshot()
    {
        var orgId = Guid.NewGuid();
        var booking = new Booking
        {
            PropertyId = Guid.NewGuid(),
            OrgId = orgId,
            CheckInDate = DateTime.UtcNow.Date.AddDays(1),
            CheckOutDate = DateTime.UtcNow.Date.AddDays(5),
            TotalPrice = 500m,
            NumberOfGuests = 2,
            Status = BookingStatus.Pending,
            Source = BookingSource.Direct,
        };
        var guest = new Guest { FirstName = "Mario", LastName = "Rossi", Email = "mario@example.com" };
        _mockRepository.Setup(x => x.AddAsync(It.IsAny<Booking>())).ReturnsAsync((Booking b) => b);
        _mockRepository.Setup(x => x.IsAvailableAsync(
                It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>()))
            .ReturnsAsync(true);
        _mockGuestRepository.Setup(x => x.AddAsync(It.IsAny<Guest>())).ReturnsAsync((Guest g) => g);

        var result = await _service.CreateManualBookingAsync(booking, guest);

        Assert.Equal(BookingStatus.Confirmed, result.Status);
        Assert.Equal(BookingSource.Manual, result.Source);
        Assert.Equal(guest.Id, result.GuestId);
        Assert.Equal(orgId, guest.OrgId);
        _mockGuestRepository.Verify(x => x.AddAsync(guest), Times.Once);
        _mockRepository.Verify(x => x.AddAsync(It.Is<Booking>(b =>
            b.Status == BookingStatus.Confirmed && b.Source == BookingSource.Manual)), Times.Once);
        _mockHoldExpiry.Verify(x => x.ExpireOverlappingHoldsAsync(
            booking.PropertyId, booking.CheckInDate, booking.CheckOutDate, It.IsAny<CancellationToken>()), Times.Once);
        _mockRepository.Verify(x => x.IsAvailableAsync(
            booking.PropertyId, booking.CheckInDate, booking.CheckOutDate, 15), Times.Once);
    }

    [Fact]
    public async Task CreateManualBookingAsync_WithPastCheckIn_ThrowsDomainRuleWithoutStoringGuest()
    {
        var booking = new Booking
        {
            PropertyId = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            CheckInDate = DateTime.UtcNow.Date.AddDays(-2),
            CheckOutDate = DateTime.UtcNow.Date.AddDays(1),
            TotalPrice = 500m,
            NumberOfGuests = 2,
        };

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            _service.CreateManualBookingAsync(booking, new Guest()));

        Assert.Equal(BookingErrorCodes.CreateInvalid, ex.Code);
        _mockRepository.Verify(x => x.AddAsync(It.IsAny<Booking>()), Times.Never);
        _mockGuestRepository.Verify(x => x.AddAsync(It.IsAny<Guest>()), Times.Never);
    }

    [Fact]
    public async Task CreateManualBookingAsync_OverlappingDates_ThrowsConflictWithoutStoringGuest()
    {
        var booking = new Booking
        {
            PropertyId = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            CheckInDate = DateTime.UtcNow.Date.AddDays(10),
            CheckOutDate = DateTime.UtcNow.Date.AddDays(14),
            TotalPrice = 500m,
            NumberOfGuests = 2,
        };
        _mockRepository.Setup(x => x.IsAvailableAsync(
                It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>()))
            .ReturnsAsync(false);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() =>
            _service.CreateManualBookingAsync(booking, new Guest()));

        Assert.Equal(BookingErrorCodes.DatesUnavailable, ex.Code);
        _mockRepository.Verify(x => x.AddAsync(It.IsAny<Booking>()), Times.Never);
        _mockGuestRepository.Verify(x => x.AddAsync(It.IsAny<Guest>()), Times.Never);
    }

    [Fact]
    public async Task CreateManualBookingAsync_DatesTakenConcurrently_ThrowsConflictAndRemovesGuestSnapshot()
    {
        var booking = new Booking
        {
            PropertyId = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            CheckInDate = DateTime.UtcNow.Date.AddDays(10),
            CheckOutDate = DateTime.UtcNow.Date.AddDays(14),
            TotalPrice = 500m,
            NumberOfGuests = 2,
        };
        var guest = new Guest();
        _mockRepository.Setup(x => x.IsAvailableAsync(
                It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>()))
            .ReturnsAsync(true);
        _mockGuestRepository.Setup(x => x.AddAsync(It.IsAny<Guest>())).ReturnsAsync((Guest g) => g);
        _mockRepository.Setup(x => x.AddAsync(It.IsAny<Booking>()))
            .ThrowsAsync(new InvalidOperationException("Property not available for selected dates"));

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() =>
            _service.CreateManualBookingAsync(booking, guest));

        Assert.Equal(BookingErrorCodes.DatesUnavailable, ex.Code);
        _mockGuestRepository.Verify(x => x.DeleteAsync(guest.Id), Times.Once);
    }

    [Fact]
    public async Task IsPropertyAvailableAsync_WithAvailableProperty_ExpiresOverlappingHoldsFirstAndReturnsTrue()
    {
        var propertyId = Guid.NewGuid();
        var checkIn = DateTime.Now.AddDays(10);
        var checkOut = DateTime.Now.AddDays(15);
        var sequence = new List<string>();
        _mockHoldExpiry
            .Setup(x => x.ExpireOverlappingHoldsAsync(propertyId, checkIn, checkOut, It.IsAny<CancellationToken>()))
            .Callback(() => sequence.Add("expire"))
            .ReturnsAsync(CheckoutHoldExpiryRun.Empty);
        _mockRepository.Setup(x => x.IsAvailableAsync(propertyId, checkIn, checkOut, 15))
            .Callback(() => sequence.Add("availability"))
            .ReturnsAsync(true);

        var result = await _service.IsPropertyAvailableAsync(propertyId, checkIn, checkOut);

        Assert.True(result);
        // BK-21: the expired holds of these dates go through the expiry routine (intent cancelled on Stripe) before the check.
        Assert.Equal(["expire", "availability"], sequence);
    }

    [Fact]
    public async Task GetCalendarAsync_ReadsWithHoldTtlAndCancelsNothing()
    {
        var propertyId = Guid.NewGuid();
        var start = DateTime.UtcNow.Date;
        var end = start.AddDays(30);
        _mockRepository.Setup(x => x.GetByDateRangeAsync(propertyId, start, end, 15))
            .ReturnsAsync([]);

        var result = await _service.GetCalendarAsync(propertyId, start, end);

        Assert.Empty(result);
        // BK-21: expired holds are left out by the read itself; cancelling them is the job's work, never a read's.
        _mockRepository.Verify(x => x.GetByDateRangeAsync(propertyId, start, end, 15), Times.Once);
        _mockHoldExpiry.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateBookingAsync_WithPastUnchangedCheckIn_AllowsCheckoutStatusUpdate()
    {
        var bookingId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var guestId = Guid.NewGuid();
        var checkIn = DateTime.UtcNow.Date.AddDays(-2);
        var checkOut = DateTime.UtcNow.Date;
        var existing = new Booking
        {
            Id = bookingId,
            PropertyId = propertyId,
            GuestId = guestId,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            Status = BookingStatus.CheckedIn,
            TotalPrice = 500m,
            NumberOfGuests = 2,
        };
        var update = new Booking
        {
            Id = bookingId,
            PropertyId = propertyId,
            GuestId = guestId,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            Status = BookingStatus.CheckedOut,
            TotalPrice = 500m,
            NumberOfGuests = 2,
        };

        _mockRepository.Setup(x => x.GetByIdAsync(bookingId)).ReturnsAsync(existing);
        _mockRepository.Setup(x => x.UpdateAsync(update)).ReturnsAsync(update);

        var result = await _service.UpdateBookingAsync(update);

        Assert.Equal(BookingStatus.CheckedOut, result.Status);
        _mockRepository.Verify(x => x.UpdateAsync(update), Times.Once);
    }

    [Fact]
    public async Task UpdateBookingAsync_CancelledBooking_ThrowsDomainRuleException()
    {
        var (existing, update) = CreateFutureBookingPair(BookingStatus.Cancelled);
        _mockRepository.Setup(x => x.GetByIdAsync(existing.Id)).ReturnsAsync(existing);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => _service.UpdateBookingAsync(update));

        Assert.Equal("booking_update_invalid", ex.Code);
        Assert.Equal("BookingUpdateInvalid", ex.MessageKey);
        _mockRepository.Verify(x => x.UpdateAsync(It.IsAny<Booking>()), Times.Never);
    }

    [Fact]
    public async Task UpdateBookingAsync_OverlappingBooking_ThrowsDomainConflictException()
    {
        var (existing, update) = CreateFutureBookingPair(BookingStatus.Confirmed);
        _mockRepository.Setup(x => x.GetByIdAsync(existing.Id)).ReturnsAsync(existing);
        _mockRepository
            .Setup(x => x.UpdateAsync(update))
            .ThrowsAsync(new InvalidOperationException("Property not available for selected dates"));

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => _service.UpdateBookingAsync(update));

        Assert.Equal("booking_dates_unavailable", ex.Code);
        Assert.Equal("BookingDatesUnavailable", ex.MessageKey);
    }

    [Fact]
    public async Task UpdateBookingAsync_RepositoryFailsForOtherReason_PropagatesOriginalException()
    {
        var (existing, update) = CreateFutureBookingPair(BookingStatus.Confirmed);
        _mockRepository.Setup(x => x.GetByIdAsync(existing.Id)).ReturnsAsync(existing);
        _mockRepository
            .Setup(x => x.UpdateAsync(update))
            .ThrowsAsync(new InvalidOperationException("The instance of entity type 'Booking' cannot be tracked"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.UpdateBookingAsync(update));
    }

    private static (Booking Existing, Booking Update) CreateFutureBookingPair(BookingStatus existingStatus)
    {
        var bookingId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var guestId = Guid.NewGuid();
        var checkIn = DateTime.UtcNow.Date.AddDays(10);
        var checkOut = checkIn.AddDays(3);
        Booking Create(BookingStatus status) => new()
        {
            Id = bookingId,
            PropertyId = propertyId,
            GuestId = guestId,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            Status = status,
            TotalPrice = 500m,
            NumberOfGuests = 2,
        };

        return (Create(existingStatus), Create(BookingStatus.Confirmed));
    }
}
