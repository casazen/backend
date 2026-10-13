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
    private readonly Mock<IStripeService> _mockStripe = new();
    private readonly Mock<IPricingAdapterService> _mockPricing = new();
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
            _mockStripe.Object,
            new Mock<IPaymentRepository>().Object,
            CreatePropertyICalSyncService(_db, configuration),
            configuration,
            new Mock<ILogger<BookingService>>().Object,
            _mockHoldExpiry.Object,
            new OnSiteRequestNotifier(
                _db, _emails, EmailTestHelpers.Links(), Mock.Of<ILogger<OnSiteRequestNotifier>>()),
            _mockPricing.Object);
        _mockPricing
            .Setup(s => s.GetAppliedNightlyPricesAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<DateOnly, decimal>());
    }

    private static PropertyICalSyncService CreatePropertyICalSyncService(AppDbContext db, IConfiguration configuration)
    {
        return ICalTestServices.PropertySync(db, Mock.Of<ISafeExternalHttpClient>(), configuration);
    }

    [Fact]
    public async Task CreateDirectBookingAsync_WithInvalidPaymentOption_RejectsBeforePersisting()
    {
        var input = new DirectBookingCreateInput(
            Guid.NewGuid(),
            TimeProvider.System.TodayInRome().AddDays(10),
            TimeProvider.System.TodayInRome().AddDays(12),
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
        var checkIn = TimeProvider.System.TodayInRome().AddDays(30);

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
        _db.Orgs.Add(org);
        _db.Properties.Add(property);
        await _db.SaveChangesAsync();
        var checkIn = TimeProvider.System.TodayInRome().AddDays(30);

        var result = await _service.CreateDirectBookingAsync(OnSiteInput(property.Id, checkIn, checkIn.AddDays(14)));

        var stored = await _db.Bookings.AsNoTracking().SingleAsync(b => b.Id == result.BookingId);
        // D5: never confirmed by the checkout.
        Assert.Equal(BookingStatus.Pending, stored.Status);
        Assert.Equal(PaymentOption.OnSite, stored.PaymentOption);
        Assert.Null(stored.GuestEmailVerifiedAt);
        _mockRepository.Verify(x => x.UpdateAsync(It.IsAny<Booking>()), Times.Never);
        // BK-18: the guest snapshot is saved with its booking (one insert), never on its own.
        _mockGuestRepository.Verify(x => x.AddAsync(It.IsAny<Guest>()), Times.Never);
        Assert.Equal("guest@example.com", (await _db.Guests.AsNoTracking().SingleAsync(g => g.Id == stored.GuestId)).Email);
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
    public async Task CreateDirectBookingAsync_PastCheckIn_Throws422WithoutStoringBookingOrGuest()
    {
        // A3-33: the guest (personal data and consent) used to be saved before this check and left behind.
        var property = ConnectReadyProperty();
        var yesterday = TimeProvider.System.TodayInRome().AddDays(-1);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => _service.CreateDirectBookingAsync(
            DirectInput(property.Id, yesterday, yesterday.AddDays(3), PaymentOption.Immediate)));

        Assert.Equal(DirectBookingErrorCodes.InvalidStay, ex.Code);
        _mockGuestRepository.Verify(x => x.AddAsync(It.IsAny<Guest>()), Times.Never);
        _mockRepository.Verify(x => x.AddAsync(It.IsAny<Booking>()), Times.Never);
    }

    [Fact]
    public async Task CreateDirectBookingAsync_ValidStay_SavesTheGuestSnapshotWithTheBookingInOneInsert()
    {
        var property = ConnectReadyProperty();
        Booking? inserted = null;
        _mockRepository.Setup(x => x.AddAsync(It.IsAny<Booking>()))
            .Callback((Booking b) => inserted = b)
            .ReturnsAsync((Booking b) => b);
        _mockStripe
            .Setup(x => x.CreateConnectedAccountPaymentIntentAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()))
            .ReturnsAsync(new Stripe.PaymentIntent { Id = "pi_bk18", ClientSecret = "pi_bk18_secret" });
        var checkIn = TimeProvider.System.TodayInRome().AddDays(30);

        await _service.CreateDirectBookingAsync(DirectInput(property.Id, checkIn, checkIn.AddDays(3), PaymentOption.Immediate));

        Assert.NotNull(inserted);
        Assert.Equal(inserted.GuestId, inserted.Guest.Id);
        Assert.Equal("guest@example.com", inserted.Guest.Email);
        Assert.Equal(property.OrgId, inserted.Guest.OrgId);
        _mockGuestRepository.Verify(x => x.AddAsync(It.IsAny<Guest>()), Times.Never);
        _mockRepository.Verify(x => x.DiscardCheckoutAttemptAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task CreateDirectBookingAsync_DatesTakenConcurrently_ThrowsConflictWithoutSavingTheGuest()
    {
        var property = ConnectReadyProperty();
        _mockRepository.Setup(x => x.AddAsync(It.IsAny<Booking>()))
            .ThrowsAsync(new InvalidOperationException("Property not available for selected dates"));
        var checkIn = TimeProvider.System.TodayInRome().AddDays(30);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => _service.CreateDirectBookingAsync(
            DirectInput(property.Id, checkIn, checkIn.AddDays(3), PaymentOption.Immediate)));

        Assert.Equal(BookingErrorCodes.DatesUnavailable, ex.Code);
        _mockGuestRepository.Verify(x => x.AddAsync(It.IsAny<Guest>()), Times.Never);
        _mockGuestRepository.Verify(x => x.DeleteAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Theory]
    [InlineData(PaymentOption.Immediate)]
    [InlineData(PaymentOption.OnCancellationDeadline)]
    public async Task CreateDirectBookingAsync_StripeFailsToStartThePayment_DiscardsTheAttemptWithItsGuest(PaymentOption option)
    {
        var property = ConnectReadyProperty();
        _mockRepository.Setup(x => x.AddAsync(It.IsAny<Booking>())).ReturnsAsync((Booking b) => b);
        var stripeDown = new Stripe.StripeException("Stripe unavailable");
        _mockStripe
            .Setup(x => x.CreateConnectedAccountPaymentIntentAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()))
            .ThrowsAsync(stripeDown);
        _mockStripe
            .Setup(x => x.CreateConnectedAccountSetupIntentAsync(
                It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ThrowsAsync(stripeDown);
        var checkIn = TimeProvider.System.TodayInRome().AddDays(30);

        await Assert.ThrowsAsync<PaymentProcessingException>(() => _service.CreateDirectBookingAsync(
            DirectInput(property.Id, checkIn, checkIn.AddDays(3), option)));

        // Removed with its guest, not left as a cancelled booking that keeps the guest's personal data.
        _mockRepository.Verify(x => x.DiscardCheckoutAttemptAsync(It.IsAny<Guid>()), Times.Once);
        _mockRepository.Verify(x => x.UpdateAsync(It.IsAny<Booking>()), Times.Never);
    }

    [Fact]
    public async Task CreateDirectBookingAsync_DiscardFailsAfterStripeFailure_StillThrowsThePaymentError()
    {
        var property = ConnectReadyProperty();
        _mockRepository.Setup(x => x.AddAsync(It.IsAny<Booking>())).ReturnsAsync((Booking b) => b);
        _mockRepository.Setup(x => x.DiscardCheckoutAttemptAsync(It.IsAny<Guid>()))
            .ThrowsAsync(new TimeoutException("database unavailable"));
        _mockStripe
            .Setup(x => x.CreateConnectedAccountPaymentIntentAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()))
            .ThrowsAsync(new Stripe.StripeException("Stripe unavailable"));
        var checkIn = TimeProvider.System.TodayInRome().AddDays(30);

        await Assert.ThrowsAsync<PaymentProcessingException>(() => _service.CreateDirectBookingAsync(
            DirectInput(property.Id, checkIn, checkIn.AddDays(3), PaymentOption.Immediate)));
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
    public async Task QuoteDirectBookingAsync_PausedProperty_ThrowsNotFound()
    {
        // PC-03, A2-05: a paused property is hidden from new guest bookings, like an inactive or non-compliant one,
        // but stays a normal, active property otherwise (existing bookings untouched, its own host still sees it).
        var property = ConnectReadyProperty();
        property.IsPaused = true;
        var checkIn = TimeProvider.System.TodayInRome().AddDays(30);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => _service.QuoteDirectBookingAsync(
            new DirectBookingQuoteInput(property.Id, checkIn, checkIn.AddDays(3), 2, 0)));

        Assert.Equal("PropertyNotFound", ex.MessageKey);
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

    // ─── Minimum stay, weekend surcharge and marketing consent (DB-03) ───────────────────────────────

    /// <summary>The first Friday on or after <paramref name="from"/>: a stay from it has two weekend nights.</summary>
    private static DateTime FridayOnOrAfter(DateTime from)
    {
        var day = from.Date;
        while (day.DayOfWeek != DayOfWeek.Friday)
            day = day.AddDays(1);

        return DateTime.SpecifyKind(day, DateTimeKind.Utc);
    }

    [Fact]
    public async Task QuoteDirectBookingAsync_StayShorterThanTheMinimum_Throws422WithTheMinimum()
    {
        var property = ConnectReadyProperty();
        property.MinNights = 3;
        var checkIn = TimeProvider.System.TodayInRome().AddDays(30);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => _service.QuoteDirectBookingAsync(
            new DirectBookingQuoteInput(property.Id, checkIn, checkIn.AddDays(2), 2, 0)));

        Assert.Equal(DirectBookingErrorCodes.MinNightsNotMet, ex.Code);
        Assert.Equal("DirectBookingMinNightsNotMet", ex.MessageKey);
        Assert.Equal(3, Assert.Single(ex.MessageArgs));
        // Nothing is priced for a stay that cannot be booked.
        _mockTouristTax.Verify(
            x => x.QuoteAsync(It.IsAny<TouristTaxComune>(), It.IsAny<TouristTaxStay>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(3, 4)]
    [InlineData(1, 1)]
    [InlineData(30, 30)]
    public async Task QuoteDirectBookingAsync_StayAtOrAboveTheMinimum_IsPriced(int minNights, int nights)
    {
        var property = ConnectReadyProperty();
        property.MinNights = minNights;
        var checkIn = TimeProvider.System.TodayInRome().AddDays(30);

        var quote = await _service.QuoteDirectBookingAsync(
            new DirectBookingQuoteInput(property.Id, checkIn, checkIn.AddDays(nights), 2, 0));

        Assert.Equal(nights, quote.Nights);
        Assert.Equal(100m * nights, quote.TotalPrice);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(9)]
    public async Task QuoteDirectBookingAsync_NoMinimum_AnyLengthIsPriced(int nights)
    {
        var property = ConnectReadyProperty();
        Assert.Null(property.MinNights);
        var checkIn = TimeProvider.System.TodayInRome().AddDays(30);

        var quote = await _service.QuoteDirectBookingAsync(
            new DirectBookingQuoteInput(property.Id, checkIn, checkIn.AddDays(nights), 2, 0));

        Assert.Equal(nights, quote.Nights);
    }

    [Fact]
    public async Task CreateDirectBookingAsync_StayShorterThanTheMinimum_Throws422BeforeAnythingIsStored()
    {
        var property = ConnectReadyProperty();
        property.MinNights = 4;
        var checkIn = TimeProvider.System.TodayInRome().AddDays(30);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => _service.CreateDirectBookingAsync(
            DirectInput(property.Id, checkIn, checkIn.AddDays(3), PaymentOption.Immediate)));

        Assert.Equal(DirectBookingErrorCodes.MinNightsNotMet, ex.Code);
        Assert.Equal(4, Assert.Single(ex.MessageArgs));
        _mockRepository.Verify(x => x.AddAsync(It.IsAny<Booking>()), Times.Never);
        _mockRepository.Verify(x => x.AddWithGuestConsentAsync(It.IsAny<Booking>(), It.IsAny<GuestConsentRecord>()), Times.Never);
        _mockGuestRepository.Verify(x => x.AddAsync(It.IsAny<Guest>()), Times.Never);
        _mockStripe.Verify(
            x => x.CreateConnectedAccountPaymentIntentAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()),
            Times.Never);
    }

    [Fact]
    public async Task PriceHostStayAsync_StayShorterThanTheMinimum_IsPricedBecauseTheMinimumIsARuleOfTheGuests()
    {
        // A stay the host enters by hand (a friend for a night, a block for the owner) is not refused by the minimum.
        var property = ConnectReadyProperty();
        property.MinNights = 5;
        var checkIn = TimeProvider.System.TodayInRome().AddDays(30);

        var quote = await _service.PriceHostStayAsync(property, checkIn, checkIn.AddDays(1), 2, 0, null);

        Assert.Equal(1, quote.Nights);
        Assert.Equal(100m, quote.TotalPrice);
    }

    [Fact]
    public async Task QuoteDirectBookingAsync_DefaultRules_TotalIsTheNightlyRateTimesTheNightsPlusCleaningAndTax()
    {
        // The non-regression of DB-03: a property with no minimum and no surcharge (every property until the host sets them)
        // is priced as it always was, whatever the day the stay starts, Fridays and Saturdays included.
        var property = ConnectReadyProperty();
        property.NightlyRate = 123.45m;
        property.CleaningFee = 60m;
        _mockTouristTax
            .Setup(x => x.QuoteAsync(It.IsAny<TouristTaxComune>(), It.IsAny<TouristTaxStay>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TouristTaxQuote(TouristTaxQuoteStatus.Calculated, 8.40m, 1, 1, false, [], []));
        var first = TimeProvider.System.TodayInRome().AddDays(30);

        for (var offset = 0; offset < 14; offset++)
        {
            for (var nights = 1; nights <= 9; nights++)
            {
                var checkIn = first.AddDays(offset);
                var quote = await _service.QuoteDirectBookingAsync(
                    new DirectBookingQuoteInput(property.Id, checkIn, checkIn.AddDays(nights), 2, 0));

                var oldBase = property.NightlyRate * nights + property.CleaningFee;
                Assert.Equal(oldBase, quote.BasePrice);
                Assert.Equal(oldBase + 8.40m, quote.TotalPrice);
                Assert.Equal(property.NightlyRate * nights, quote.Lodging.Total);
                Assert.Equal(0, quote.Lodging.WeekendNights);
                Assert.Equal(quote.TotalPrice, StayPricing.Lines(quote)[^1].AmountCents / 100m);
            }
        }
    }

    [Fact]
    public async Task QuoteDirectBookingAsync_WeekendSurcharge_PricesFridayAndSaturdayNightsAndGivesTheTaxThePriceOfEachNight()
    {
        var property = ConnectReadyProperty();
        property.NightlyRate = 100m;
        property.CleaningFee = 40m;
        property.WeekendSurchargePercent = 15m;
        TouristTaxStay? taxStay = null;
        _mockTouristTax
            .Setup(x => x.QuoteAsync(It.IsAny<TouristTaxComune>(), It.IsAny<TouristTaxStay>(), It.IsAny<CancellationToken>()))
            .Callback<TouristTaxComune, TouristTaxStay, CancellationToken>((_, stay, _) => taxStay = stay)
            .ReturnsAsync(new TouristTaxQuote(TouristTaxQuoteStatus.Calculated, 10m, 4, 4, false, [], []));
        // Thursday to Monday: Thursday, Friday, Saturday, Sunday.
        var checkIn = FridayOnOrAfter(TimeProvider.System.TodayInRome().AddDays(30)).AddDays(-1);

        var quote = await _service.QuoteDirectBookingAsync(
            new DirectBookingQuoteInput(property.Id, checkIn, checkIn.AddDays(4), 2, 0));

        Assert.Equal(2, quote.Lodging.WeekdayNights);
        Assert.Equal(2, quote.Lodging.WeekendNights);
        Assert.Equal(115m, quote.Lodging.WeekendNightlyRate);
        Assert.Equal(2 * 100m + 2 * 115m, quote.Lodging.Total);
        Assert.Equal(quote.Lodging.Total + 40m, quote.BasePrice);
        Assert.Equal(quote.BasePrice + 10m, quote.TotalPrice);
        // The tax engine reads the price of each night of the stay (a percentage rate follows the surcharge).
        Assert.NotNull(taxStay);
        Assert.Equal([100m, 115m, 115m, 100m], taxStay.NightlyPrices);
        Assert.Equal(100m, taxStay.NightlyPrice);
    }

    [Fact]
    public async Task QuoteDirectBookingAsync_PriceTheHostConfirmedForADate_IsThePriceOfThatNightAndTheSurchargeDoesNotRaiseIt()
    {
        var property = ConnectReadyProperty();
        property.NightlyRate = 100m;
        property.CleaningFee = 40m;
        property.WeekendSurchargePercent = 15m;
        TouristTaxStay? taxStay = null;
        _mockTouristTax
            .Setup(x => x.QuoteAsync(It.IsAny<TouristTaxComune>(), It.IsAny<TouristTaxStay>(), It.IsAny<CancellationToken>()))
            .Callback<TouristTaxComune, TouristTaxStay, CancellationToken>((_, stay, _) => taxStay = stay)
            .ReturnsAsync(new TouristTaxQuote(TouristTaxQuoteStatus.Calculated, 10m, 4, 4, false, [], []));
        // Thursday to Monday; the host confirmed 180 for the Friday and 90 for the Sunday (PC-15).
        var checkIn = FridayOnOrAfter(TimeProvider.System.TodayInRome().AddDays(30)).AddDays(-1);
        var friday = DateOnly.FromDateTime(checkIn).AddDays(1);
        var sunday = DateOnly.FromDateTime(checkIn).AddDays(3);
        DateOnly? askedFrom = null;
        DateOnly? askedTo = null;
        _mockPricing
            .Setup(s => s.GetAppliedNightlyPricesAsync(property.Id, It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, DateOnly, DateOnly, CancellationToken>((_, from, to, _) => (askedFrom, askedTo) = (from, to))
            .ReturnsAsync(new Dictionary<DateOnly, decimal> { [friday] = 180m, [sunday] = 90m });

        var quote = await _service.QuoteDirectBookingAsync(
            new DirectBookingQuoteInput(property.Id, checkIn, checkIn.AddDays(4), 2, 0));

        // Thursday 100, Friday 180 (the host's price, no surcharge on top), Saturday 115 (rate + 15 %), Sunday 90.
        Assert.Equal(DateOnly.FromDateTime(checkIn), askedFrom);
        Assert.Equal(DateOnly.FromDateTime(checkIn).AddDays(4), askedTo);
        Assert.Equal(100m + 180m + 115m + 90m, quote.Lodging.Total);
        Assert.Equal((1, 1, 4), (quote.Lodging.WeekdayNights, quote.Lodging.WeekendNights, quote.Lodging.Nights));
        Assert.Equal(quote.Lodging.Total + 40m, quote.BasePrice);
        Assert.Equal(quote.BasePrice + 10m, quote.TotalPrice);
        Assert.Equal([100m, 180m, 115m, 90m], taxStay!.NightlyPrices);
        var lines = StayPricing.Lines(quote);
        Assert.Equal(quote.TotalPrice, lines[^1].AmountCents / 100m);
        Assert.Equal(lines[^1].AmountCents, lines.Take(lines.Count - 1).Sum(l => l.AmountCents));
    }

    [Fact]
    public async Task QuoteDirectBookingAsync_NoSurcharge_GivesTheTaxEngineTheOneNightlyRateAsBefore()
    {
        var property = ConnectReadyProperty();
        TouristTaxStay? taxStay = null;
        _mockTouristTax
            .Setup(x => x.QuoteAsync(It.IsAny<TouristTaxComune>(), It.IsAny<TouristTaxStay>(), It.IsAny<CancellationToken>()))
            .Callback<TouristTaxComune, TouristTaxStay, CancellationToken>((_, stay, _) => taxStay = stay)
            .ReturnsAsync(new TouristTaxQuote(TouristTaxQuoteStatus.RateUnavailable, null, 3, 0, false, [], []));
        var checkIn = FridayOnOrAfter(TimeProvider.System.TodayInRome().AddDays(30));

        await _service.QuoteDirectBookingAsync(new DirectBookingQuoteInput(property.Id, checkIn, checkIn.AddDays(3), 2, 0));

        Assert.NotNull(taxStay);
        Assert.Equal(100m, taxStay.NightlyPrice);
        Assert.Null(taxStay.NightlyPrices);
    }

    [Fact]
    public async Task CreateDirectBookingAsync_WeekendSurcharge_RecordsAndChargesTheTotalOfTheQuote()
    {
        var property = ConnectReadyProperty();
        property.NightlyRate = 100m;
        property.CleaningFee = 40m;
        property.WeekendSurchargePercent = 15m;
        Booking? inserted = null;
        _mockRepository.Setup(x => x.AddAsync(It.IsAny<Booking>()))
            .Callback((Booking b) => inserted = b)
            .ReturnsAsync((Booking b) => b);
        long chargedCents = 0;
        _mockStripe
            .Setup(x => x.CreateConnectedAccountPaymentIntentAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()))
            .Callback((string _, long cents, string _, Dictionary<string, string> _) => chargedCents = cents)
            .ReturnsAsync(new Stripe.PaymentIntent { Id = "pi_db03", ClientSecret = "pi_db03_secret" });
        var checkIn = FridayOnOrAfter(TimeProvider.System.TodayInRome().AddDays(30));

        var quote = await _service.QuoteDirectBookingAsync(
            new DirectBookingQuoteInput(property.Id, checkIn, checkIn.AddDays(3), 2, 0));
        var result = await _service.CreateDirectBookingAsync(DirectInput(property.Id, checkIn, checkIn.AddDays(3), PaymentOption.Immediate));

        // Fri + Sat at 115, Sun at 100, plus 40 of cleaning: 370.
        Assert.Equal(370m, quote.TotalPrice);
        Assert.NotNull(inserted);
        Assert.Equal(quote.TotalPrice, inserted.TotalPrice);
        Assert.Equal(quote.BasePrice, inserted.BasePrice);
        Assert.Equal(quote.TotalPrice, result.Amount);
        Assert.Equal(37000, chargedCents);
    }

    [Fact]
    public async Task CreateDirectBookingAsync_MarketingConsentWithAVersion_IsRecordedOnTheGuestAndInTheRegisterInOneInsert()
    {
        var service = NewServiceWithSettings(("Gdpr:MarketingConsentVersion", " marketing-2026-10 "));
        var property = ConnectReadyProperty();
        Booking? insertedBooking = null;
        GuestConsentRecord? insertedConsent = null;
        _mockRepository.Setup(x => x.AddWithGuestConsentAsync(It.IsAny<Booking>(), It.IsAny<GuestConsentRecord>()))
            .Callback((Booking b, GuestConsentRecord c) =>
            {
                insertedBooking = b;
                insertedConsent = c;
            })
            .ReturnsAsync((Booking b, GuestConsentRecord _) => b);
        _mockStripe
            .Setup(x => x.CreateConnectedAccountPaymentIntentAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()))
            .ReturnsAsync(new Stripe.PaymentIntent { Id = "pi_mk", ClientSecret = "pi_mk_secret" });
        var checkIn = TimeProvider.System.TodayInRome().AddDays(30);
        var input = DirectInput(property.Id, checkIn, checkIn.AddDays(3), PaymentOption.Immediate) with
        {
            MarketingConsent = true,
            ConsentIpAddress = "203.0.113.7",
        };

        await service.CreateDirectBookingAsync(input);

        Assert.NotNull(insertedBooking);
        Assert.NotNull(insertedConsent);
        var guest = insertedBooking.Guest;
        Assert.True(guest.MarketingConsent);
        Assert.NotNull(guest.MarketingConsentDate);
        Assert.Equal(guest.Id, insertedConsent.GuestId);
        Assert.Equal(property.OrgId, insertedConsent.OrgId);
        Assert.Equal(GuestConsentPurpose.Marketing, insertedConsent.Purpose);
        Assert.Equal(GuestConsentAction.Granted, insertedConsent.Action);
        Assert.Equal("marketing-2026-10", insertedConsent.Version);
        Assert.Equal(GuestConsentSource.BookingCheckout, insertedConsent.Source);
        Assert.Equal("203.0.113.7", insertedConsent.IpAddress);
        Assert.Equal(guest.MarketingConsentDate, insertedConsent.RecordedAt);
        // The plain insert is for the checkouts without consent.
        _mockRepository.Verify(x => x.AddAsync(It.IsAny<Booking>()), Times.Never);
    }

    [Fact]
    public async Task CreateDirectBookingAsync_MarketingConsentWithoutAVersion_Throws422AndStoresNothing()
    {
        // Like the check-in portal (CO-15): no versioned text, no consent that can be proved.
        var property = ConnectReadyProperty();
        var checkIn = TimeProvider.System.TodayInRome().AddDays(30);
        var input = DirectInput(property.Id, checkIn, checkIn.AddDays(3), PaymentOption.Immediate) with { MarketingConsent = true };

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => _service.CreateDirectBookingAsync(input));

        Assert.Equal(DirectBookingErrorCodes.MarketingConsentUnavailable, ex.Code);
        Assert.Equal("DirectBookingMarketingConsentUnavailable", ex.MessageKey);
        _mockRepository.Verify(x => x.AddAsync(It.IsAny<Booking>()), Times.Never);
        _mockRepository.Verify(x => x.AddWithGuestConsentAsync(It.IsAny<Booking>(), It.IsAny<GuestConsentRecord>()), Times.Never);
        _mockPropertyRepository.Verify(x => x.GetByIdAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("marketing-2026-10")]
    public async Task CreateDirectBookingAsync_NoMarketingConsent_KeepsTheTodayFlowWhetherOrNotTheTextHasAVersion(string? version)
    {
        var service = version is null
            ? _service
            : NewServiceWithSettings(("Gdpr:MarketingConsentVersion", version));
        var property = ConnectReadyProperty();
        Booking? inserted = null;
        _mockRepository.Setup(x => x.AddAsync(It.IsAny<Booking>()))
            .Callback((Booking b) => inserted = b)
            .ReturnsAsync((Booking b) => b);
        _mockStripe
            .Setup(x => x.CreateConnectedAccountPaymentIntentAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()))
            .ReturnsAsync(new Stripe.PaymentIntent { Id = "pi_plain", ClientSecret = "pi_plain_secret" });
        var checkIn = TimeProvider.System.TodayInRome().AddDays(30);

        await service.CreateDirectBookingAsync(DirectInput(property.Id, checkIn, checkIn.AddDays(3), PaymentOption.Immediate));

        Assert.NotNull(inserted);
        Assert.False(inserted.Guest.MarketingConsent);
        Assert.Null(inserted.Guest.MarketingConsentDate);
        _mockRepository.Verify(x => x.AddWithGuestConsentAsync(It.IsAny<Booking>(), It.IsAny<GuestConsentRecord>()), Times.Never);
    }

    /// <summary>A service like <c>_service</c> with more settings (same mocks), for the settings that default to none.</summary>
    private BookingService NewServiceWithSettings(params (string Key, string? Value)[] settings)
    {
        var values = new Dictionary<string, string?>
        {
            ["DirectBooking:ConsentVersion"] = "2026-06-direct-checkout-v1",
            ["DirectBooking:PendingTtlMinutes"] = "15",
            ["DirectBooking:OnSiteMaxNights"] = "14",
            ["Stripe:PublishableKey"] = "pk_test",
        };
        foreach (var (key, value) in settings)
            values[key] = value;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new BookingService(
            _mockRepository.Object,
            _mockPropertyRepository.Object,
            _mockOrgService.Object,
            _mockGuestRepository.Object,
            _mockTouristTax.Object,
            _mockStripe.Object,
            new Mock<IPaymentRepository>().Object,
            CreatePropertyICalSyncService(_db, configuration),
            configuration,
            new Mock<ILogger<BookingService>>().Object,
            _mockHoldExpiry.Object,
            new OnSiteRequestNotifier(
                _db, _emails, EmailTestHelpers.Links(), Mock.Of<ILogger<OnSiteRequestNotifier>>()),
            _mockPricing.Object);
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
            CheckInDate = TimeProvider.System.TodayInRome().AddDays(1),
            CheckOutDate = TimeProvider.System.TodayInRome().AddDays(5),
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
            CheckInDate = TimeProvider.System.TodayInRome().AddDays(-2),
            CheckOutDate = TimeProvider.System.TodayInRome().AddDays(1),
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
            CheckInDate = TimeProvider.System.TodayInRome().AddDays(10),
            CheckOutDate = TimeProvider.System.TodayInRome().AddDays(14),
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
            CheckInDate = TimeProvider.System.TodayInRome().AddDays(10),
            CheckOutDate = TimeProvider.System.TodayInRome().AddDays(14),
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
        var checkIn = TimeProvider.System.TodayInRome().AddDays(10);
        var checkOut = TimeProvider.System.TodayInRome().AddDays(15);
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
        var start = TimeProvider.System.TodayInRome();
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
        var checkIn = TimeProvider.System.TodayInRome().AddDays(-2);
        var checkOut = TimeProvider.System.TodayInRome();
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
        var checkIn = TimeProvider.System.TodayInRome().AddDays(10);
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
