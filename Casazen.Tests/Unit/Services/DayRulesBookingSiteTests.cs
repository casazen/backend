using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E08/F05/US01/T03 (NF-36): the price and the rules of the nights where a guest meets them: the quote, the checkout and the public calendar of
/// a property, built with the real services on EF InMemory (the repositories and the payment gateway are the only mocks). The two proof tests are
/// the money and the data rule of the brief.
/// </summary>
public class DayRulesBookingSiteTests
{
    // 2026-10-09 10:00 UTC: the 9th of October in Rome, so every night of December below is some two months ahead.
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);

    private readonly string _databaseName = $"day-rules-site-{Guid.NewGuid():N}";
    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly FixedTimeProvider _clock = new(Now);
    private readonly Mock<IBookingRepository> _repository = new();
    private readonly Mock<IGuestRepository> _guests = new();
    private readonly Mock<IPropertyRepository> _properties = new();
    private readonly Mock<IOrgService> _orgs = new();
    private readonly Mock<ITouristTaxQuoteService> _touristTax = new();
    private readonly PropertyDayRuleService _rules;
    private readonly PricingAdapterService _pricing;
    private readonly BookingService _booking;
    private readonly PublicCalendarService _calendar;
    private readonly Property _property;

    public DayRulesBookingSiteTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_databaseName).Options);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DirectBooking:ConsentVersion"] = "2026-06-direct-checkout-v1",
                ["DirectBooking:PendingTtlMinutes"] = "15",
                ["DirectBooking:OnSiteMaxNights"] = "14",
                ["Stripe:PublishableKey"] = "pk_test",
            })
            .Build();
        _configuration = configuration;
        var clock = _clock;

        _rules = new PropertyDayRuleService(
            _db,
            Mock.Of<IOctorateBookingTargets>(),
            Mock.Of<IFeatureFlags>(),
            configuration,
            NullLogger<PropertyDayRuleService>.Instance,
            clock);
        _pricing = new PricingAdapterService(_db, Mock.Of<IPricingAdapterConfigRepository>(), NullLogger<PricingAdapterService>.Instance, clock);
        _booking = new BookingService(
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
            new OnSiteRequestNotifier(_db, new RecordingEmailQueue(), EmailTestHelpers.Links(), Mock.Of<ILogger<OnSiteRequestNotifier>>()),
            _pricing,
            NoChannelManager.Instance,
            timeProvider: clock,
            dayRules: _rules);
        _calendar = new PublicCalendarService(_db, _pricing, configuration, _rules, clock);

        var org = new OrgEntity
        {
            Id = Guid.NewGuid(),
            Name = "Casa Regole",
            Slug = "casa-regole",
            DisplayName = "Casa Regole",
            ContactEmail = "regole@example.com",
            OrgType = OrgType.Host,
            PlanTier = PlanTier.Starter,
            StripeConnectedAccountId = "acct_day_rules",
            ConnectChargesEnabled = true,
        };
        _property = new Property
        {
            OrgId = org.Id,
            OwnerId = "auth0|owner",
            Name = "Casa Regole",
            Address = "Via delle Regole 1",
            City = "Como",
            IsActive = true,
            ComplianceStatus = PropertyComplianceStatus.Active,
            MaxGuests = 4,
            NightlyRate = 100m,
            WeekendSurchargePercent = 20m,
        };
        _db.Orgs.Add(org);
        _db.Properties.Add(_property);
        _db.SaveChanges();

        _properties.Setup(x => x.GetByIdAsync(_property.Id)).ReturnsAsync(_property);
        _orgs.Setup(x => x.GetByIdAsync(org.Id)).ReturnsAsync(org);
        _touristTax
            .Setup(x => x.QuoteAsync(It.IsAny<TouristTaxComune>(), It.IsAny<TouristTaxStay>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TouristTaxQuote(TouristTaxQuoteStatus.RateUnavailable, null, 10, 0, false, [], []));
        _repository
            .Setup(x => x.IsAvailableAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>()))
            .ReturnsAsync(true);
    }

    // --- The proofs --------------------------------------------------------------------------------------------

    /// <summary>
    /// PROOF TEST: money rule. On a date with a price written in the calendar the quote of a one-night stay equals the price of that night in the
    /// public calendar, to the cent: over a seasonal price the host confirmed for the same date, over the rate and over the weekend surcharge;
    /// and when the host clears the day price the seasonal price (or the rate) comes back, in the quote and in the calendar alike.
    /// </summary>
    [Fact]
    public async Task DayRulePrice_QuoteEqualsCalendarNightPrice_Test()
    {
        var saturday = new DateOnly(2026, 12, 12);
        var sunday = saturday.AddDays(1);
        var monday = saturday.AddDays(2);
        var tuesday = saturday.AddDays(3);

        // Saturday: a weekend night at 120 (100 + 20%) that gets a day price of 155.55. Sunday: a seasonal price of 130 that gets a day price of
        // 150. Monday: a seasonal price of 130, no day price. Tuesday: nothing at all (the rate).
        _db.SeasonalPriceSuggestions.AddRange(Seasonal(sunday, 130m), Seasonal(monday, 130m));
        await _db.SaveChangesAsync();
        await ApplyAsync(saturday, saturday, new DayRulePatch(Price: FieldPatch<decimal>.Set(155.555m)));
        await ApplyAsync(sunday, sunday, new DayRulePatch(Price: FieldPatch<decimal>.Set(150m)));

        var nights = new[] { saturday, sunday, monday, tuesday };
        Assert.Equal([15556L, 15000L, 13000L, 10000L], await QuotedCentsAsync(nights));
        Assert.Equal([15556L, 15000L, 13000L, 10000L], await CalendarCentsAsync(nights));

        // The host takes the day prices away: the weekend night costs the weekend rate again and the seasonal price is back.
        await ApplyAsync(saturday, sunday, new DayRulePatch(Price: FieldPatch<decimal>.Clear));
        Assert.Equal([12000L, 13000L, 13000L, 10000L], await QuotedCentsAsync(nights));
        Assert.Equal(await QuotedCentsAsync(nights), await CalendarCentsAsync(nights));
        Assert.Empty(await _db.PropertyDayRules.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// PROOF TEST: data rule. The quote refuses a stay that arrives on a night closed to arrival, leaves on a day closed to departure, is shorter
    /// than the minimum or longer than the maximum of its arrival date, or breaks the advance limits, each with its own code, its sentence, the date
    /// and the limit; the checkout refuses the same before it stores anything; a stay that respects every rule is quoted, and the host's own quote
    /// of the same dates is not refused.
    /// </summary>
    [Fact]
    public async Task Checkout_StayBreakingADayRule_IsRefused_Test()
    {
        // 2026-12-10: booked at least 100 days ahead (it is 62). 12-12: at most 30 days ahead (it is 64). 12-14: closed to arrival. 12-17: closed to
        // departure. 12-19: at least 3 nights. 12-22: at most 2 nights.
        await ApplyAsync(new DateOnly(2026, 12, 10), new DateOnly(2026, 12, 10), new DayRulePatch(MinAdvanceDays: FieldPatch<int>.Set(100)));
        await ApplyAsync(new DateOnly(2026, 12, 12), new DateOnly(2026, 12, 12), new DayRulePatch(MaxAdvanceDays: FieldPatch<int>.Set(30)));
        await ApplyAsync(new DateOnly(2026, 12, 14), new DateOnly(2026, 12, 14), new DayRulePatch(ClosedToArrival: FieldPatch<bool>.Set(true)));
        await ApplyAsync(new DateOnly(2026, 12, 17), new DateOnly(2026, 12, 17), new DayRulePatch(ClosedToDeparture: FieldPatch<bool>.Set(true)));
        await ApplyAsync(new DateOnly(2026, 12, 19), new DateOnly(2026, 12, 19), new DayRulePatch(MinNights: FieldPatch<int>.Set(3)));
        await ApplyAsync(new DateOnly(2026, 12, 22), new DateOnly(2026, 12, 22), new DayRulePatch(MaxNights: FieldPatch<int>.Set(2)));

        var refusals = new (DateOnly Arrival, int Nights, string Code, string Key, int? Limit, string Date)[]
        {
            (new DateOnly(2026, 12, 14), 2, DirectBookingErrorCodes.ArrivalClosed, "DirectBookingArrivalClosed", null, "2026-12-14"),
            (new DateOnly(2026, 12, 15), 2, DirectBookingErrorCodes.DepartureClosed, "DirectBookingDepartureClosed", null, "2026-12-17"),
            (new DateOnly(2026, 12, 19), 2, DirectBookingErrorCodes.MinNightsNotMet, "DirectBookingMinNightsNotMet", 3, "2026-12-19"),
            (new DateOnly(2026, 12, 22), 3, DirectBookingErrorCodes.MaxNightsExceeded, "DirectBookingMaxNightsExceeded", 2, "2026-12-22"),
            (new DateOnly(2026, 12, 10), 2, DirectBookingErrorCodes.AdvanceTooShort, "DirectBookingAdvanceTooShort", 100, "2026-12-10"),
            (new DateOnly(2026, 12, 12), 2, DirectBookingErrorCodes.AdvanceTooLong, "DirectBookingAdvanceTooLong", 30, "2026-12-12"),
        };
        foreach (var (arrival, nights, code, key, limit, date) in refusals)
        {
            var quote = await Assert.ThrowsAsync<DomainRuleException>(() => _booking.QuoteDirectBookingAsync(QuoteInput(arrival, nights)));
            var checkout = await Assert.ThrowsAsync<DomainRuleException>(() => _booking.CreateDirectBookingAsync(CheckoutInput(arrival, nights)));

            foreach (var refused in new[] { quote, checkout })
            {
                Assert.Equal((code, key), (refused.Code, refused.MessageKey));
                Assert.Equal(limit is { } number ? new object[] { number } : [], refused.MessageArgs);
                Assert.Equal(date, refused.Extensions["date"]);
                Assert.Equal(limit is null, !refused.Extensions.ContainsKey("limit"));
            }
        }

        // Nothing was stored, nothing was asked of the gateway, no tourist tax was worked out for a stay that cannot be booked.
        _repository.Verify(x => x.AddAsync(It.IsAny<Booking>()), Times.Never);
        _repository.Verify(x => x.AddWithGuestConsentsAsync(It.IsAny<Booking>(), It.IsAny<IReadOnlyList<GuestConsentRecord>>()), Times.Never);
        _guests.Verify(x => x.AddAsync(It.IsAny<Guest>()), Times.Never);
        _touristTax.Verify(x => x.QuoteAsync(It.IsAny<TouristTaxComune>(), It.IsAny<TouristTaxStay>(), It.IsAny<CancellationToken>()), Times.Never);

        // A stay that respects every rule is quoted: three nights from the 19th (the minimum), two from the 22nd (the maximum).
        foreach (var (arrival, nights) in new[] { (new DateOnly(2026, 12, 19), 3), (new DateOnly(2026, 12, 22), 2), (new DateOnly(2026, 12, 27), 5) })
        {
            var quote = await _booking.QuoteDirectBookingAsync(QuoteInput(arrival, nights));
            Assert.Equal(nights, quote.Nights);
        }

        // The host's own quote and its manual booking are not refused by the rules: a stay entered by hand can break them.
        var closedArrival = new DateTime(2026, 12, 14, 0, 0, 0, DateTimeKind.Utc);
        var hostQuote = await _booking.PriceHostStayAsync(_property, closedArrival, closedArrival.AddDays(1), 2, 0, null);
        Assert.Equal(1, hostQuote.Nights);
    }

    // --- The rest of the behaviour -----------------------------------------------------------------------------

    [Fact]
    public async Task PublicCalendar_EachDayCarriesItsRules_AndANightClosedToArrivalIsStillFree()
    {
        var friday = new DateOnly(2026, 12, 11);
        _property.MinNights = 2;
        await _db.SaveChangesAsync();
        await ApplyAsync(
            friday,
            friday,
            new DayRulePatch(
                MinNights: FieldPatch<int>.Set(4),
                MaxNights: FieldPatch<int>.Set(7),
                ClosedToArrival: FieldPatch<bool>.Set(true),
                ClosedToDeparture: FieldPatch<bool>.Set(true),
                MinAdvanceDays: FieldPatch<int>.Set(1),
                MaxAdvanceDays: FieldPatch<int>.Set(200)));

        var calendar = await _calendar.GetAsync(_property.Id, friday, friday.AddDays(3));

        var ruled = calendar.Days[0];
        Assert.Equal(friday, ruled.Date);
        Assert.Equal((true, 4, 7, true, true, 1, 200), (ruled.Available, ruled.MinNights, ruled.MaxNights, ruled.ClosedToArrival, ruled.ClosedToDeparture, ruled.MinAdvanceDays, ruled.MaxAdvanceDays));
        // The day after has no rule: the minimum of the property, no maximum, nothing closed.
        var plain = calendar.Days[1];
        Assert.Equal((true, 2, null, false, false, null, null), (plain.Available, plain.MinNights, plain.MaxNights, plain.ClosedToArrival, plain.ClosedToDeparture, plain.MinAdvanceDays, plain.MaxAdvanceDays));
        // The price of the night with a rule but no price is still the one of the rate and the weekend (a Friday: 120).
        Assert.Equal(12000L, ruled.NightlyPriceCents);
    }

    [Fact]
    public async Task PublicCalendar_APropertyWithoutRules_IsAsItWas()
    {
        var monday = new DateOnly(2026, 12, 14);

        var calendar = await _calendar.GetAsync(_property.Id, monday, monday.AddDays(2));

        Assert.All(calendar.Days, day =>
        {
            Assert.Equal(1, day.MinNights);
            Assert.Null(day.MaxNights);
            Assert.False(day.ClosedToArrival);
            Assert.False(day.ClosedToDeparture);
            Assert.Null(day.MinAdvanceDays);
            Assert.Null(day.MaxAdvanceDays);
        });
        Assert.Equal([10000L, 10000L], calendar.Days.Select(day => day.NightlyPriceCents));
    }

    [Fact]
    public async Task PublicCalendar_AnotherOrgHostLookingAtTheSite_SeesTheSameRulesAndPrices()
    {
        var night = new DateOnly(2026, 12, 16);
        await ApplyAsync(night, night, new DayRulePatch(Price: FieldPatch<decimal>.Set(222m), ClosedToArrival: FieldPatch<bool>.Set(true)));

        // A signed-in host of another org looks at the site: the tenant filter is on for it, and the services read the site as everybody does.
        await using var asOtherOrg = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_databaseName).Options,
            new FixedTenant(Guid.NewGuid()));
        var rules = new PropertyDayRuleService(
            asOtherOrg, Mock.Of<IOctorateBookingTargets>(), Mock.Of<IFeatureFlags>(), _configuration, NullLogger<PropertyDayRuleService>.Instance, _clock);
        var pricing = new PricingAdapterService(asOtherOrg, Mock.Of<IPricingAdapterConfigRepository>(), NullLogger<PricingAdapterService>.Instance, _clock);
        var calendar = await new PublicCalendarService(asOtherOrg, pricing, _configuration, rules, _clock)
            .GetAsync(_property.Id, night, night.AddDays(1));

        var day = Assert.Single(calendar.Days);
        Assert.Equal((22200L, true), (day.NightlyPriceCents, day.ClosedToArrival));
    }

    [Fact]
    public async Task Quote_TheMinimumOfThePropertyStillHolds_WhereTheNightHasNoMinimumOfItsOwn()
    {
        _property.MinNights = 3;
        await _db.SaveChangesAsync();
        var monday = new DateOnly(2026, 12, 14);
        await ApplyAsync(monday.AddDays(7), monday.AddDays(7), new DayRulePatch(MinNights: FieldPatch<int>.Set(1)));

        // No rule on the 14th: the property's minimum (3) refuses two nights. The 21st has its own minimum of one night, which wins over the property's.
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => _booking.QuoteDirectBookingAsync(QuoteInput(monday, 2)));
        var accepted = await _booking.QuoteDirectBookingAsync(QuoteInput(monday.AddDays(7), 1));

        Assert.Equal((DirectBookingErrorCodes.MinNightsNotMet, 3), (refused.Code, Assert.Single(refused.MessageArgs)));
        Assert.Equal(1, accepted.Nights);
    }

    /// <summary>
    /// The search, the public calendar and the checkout agree (E08/F05/US01/T04, AC-NF36-2): for every arrival and every length tried, with rules of
    /// every kind scattered over the days, the search offers a house exactly when the quote of the checkout accepts the stay, and the first free date
    /// it proposes is a stay the quote accepts.
    /// </summary>
    [Fact]
    public async Task Search_OffersAStayExactlyWhenTheCheckoutAcceptsIt_AndItsFirstFreeDateIsOneToo()
    {
        _property.MinNights = 2;
        await _db.SaveChangesAsync();
        var first = new DateOnly(2026, 12, 8);
        await ApplyAsync(new DateOnly(2026, 12, 11), new DateOnly(2026, 12, 11), new DayRulePatch(ClosedToArrival: FieldPatch<bool>.Set(true)));
        await ApplyAsync(new DateOnly(2026, 12, 12), new DateOnly(2026, 12, 12), new DayRulePatch(ClosedToArrival: FieldPatch<bool>.Set(true)));
        await ApplyAsync(new DateOnly(2026, 12, 15), new DateOnly(2026, 12, 15), new DayRulePatch(ClosedToDeparture: FieldPatch<bool>.Set(true)));
        await ApplyAsync(new DateOnly(2026, 12, 17), new DateOnly(2026, 12, 18), new DayRulePatch(MinNights: FieldPatch<int>.Set(4)));
        await ApplyAsync(new DateOnly(2026, 12, 20), new DateOnly(2026, 12, 21), new DayRulePatch(MaxNights: FieldPatch<int>.Set(2)));
        await ApplyAsync(new DateOnly(2026, 12, 22), new DateOnly(2026, 12, 22), new DayRulePatch(MinAdvanceDays: FieldPatch<int>.Set(100)));
        await ApplyAsync(new DateOnly(2026, 12, 24), new DateOnly(2026, 12, 24), new DayRulePatch(MaxAdvanceDays: FieldPatch<int>.Set(40)));
        await ApplyAsync(new DateOnly(2026, 12, 26), new DateOnly(2026, 12, 26), new DayRulePatch(MinNights: FieldPatch<int>.Set(1), Price: FieldPatch<decimal>.Set(90m)));

        var rules = await _rules.GetRulesForSiteAsync([_property.Id], first, first.AddDays(80));
        var house = new PublicStayHouse(
            _property.Id, "casa-regole", "Casa Regole", "Como", null, 45.8m, 9.08m, [], 2, 1, 4, 60, 100m, 0m, 20m, _property.MinNights, null, null, null, []);
        var data = new PublicStayData(
            new DateOnly(2026, 10, 9),
            true,
            new Dictionary<Guid, IReadOnlySet<DateOnly>>(),
            new Dictionary<Guid, IReadOnlyDictionary<DateOnly, decimal>>(),
            [],
            new Dictionary<Guid, PublicRatingSummary>(),
            rules);
        async Task<bool> CheckoutAcceptsAsync(DateOnly arrival, int nights)
        {
            try
            {
                await _booking.QuoteDirectBookingAsync(QuoteInput(arrival, nights));
                return true;
            }
            catch (DomainRuleException)
            {
                return false;
            }
        }

        var tried = 0;
        var refused = 0;
        for (var arrival = first; arrival < first.AddDays(24); arrival = arrival.AddDays(1))
        {
            for (var nights = 1; nights <= 5; nights++)
            {
                var criteria = new PublicStaysCriteria(
                    arrival, arrival.AddDays(nights), 2, 0, false, null, null, null, null, null, null, [], [], [], PublicSiteSearch.Recommended, 1, null, []);
                var card = Assert.Single(PublicStayPlanner.Search([house], data, criteria, Now.UtcDateTime).Items);
                var accepted = await CheckoutAcceptsAsync(arrival, nights);

                Assert.True(accepted == card.Availability.Available, $"{arrival:yyyy-MM-dd} for {nights} night(s): the search says {card.Availability.Available} ({card.Availability.Reason}), the checkout {accepted}.");
                tried++;
                if (!accepted)
                    refused++;

                // The first free date the search proposes is a stay the checkout accepts too (as long as the stay asked, or the minimum of that night).
                if (card.Availability.FirstFreeCheckIn is { } proposed && !card.Availability.Available)
                {
                    var length = Math.Max(nights, DayRuleDefaults.MinNightsOf(rules[_property.Id].GetValueOrDefault(proposed), _property.MinNights));
                    Assert.True(await CheckoutAcceptsAsync(proposed, length), $"{arrival:yyyy-MM-dd}: the first free date {proposed:yyyy-MM-dd} for {length} night(s) is refused by the checkout.");
                }
            }
        }

        // The scenario really has stays on both sides, or the comparison proves nothing.
        Assert.InRange(refused, 20, tried - 20);
    }

    // --- Helpers -----------------------------------------------------------------------------------------------

    private async Task ApplyAsync(DateOnly from, DateOnly to, DayRulePatch patch)
    {
        await _rules.ApplyAsync(new HostScope(_property.OrgId), _property.Id, from, to, null, patch, "auth0|host");
        await _db.SaveChangesAsync();
    }

    private SeasonalPriceSuggestion Seasonal(DateOnly night, decimal applied) => new()
    {
        OrgId = _property.OrgId,
        PropertyId = _property.Id,
        StayDate = night,
        BasePrice = _property.NightlyRate,
        SuggestedPrice = applied,
        Multiplier = 1m,
        AppliedPrice = applied,
        AppliedAt = Now.UtcDateTime,
        ComputedAt = Now.UtcDateTime,
    };

    private static DateTime Utc(DateOnly day) => day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    private DirectBookingQuoteInput QuoteInput(DateOnly arrival, int nights) =>
        new(_property.Id, Utc(arrival), Utc(arrival.AddDays(nights)), 2, 0);

    private DirectBookingCreateInput CheckoutInput(DateOnly arrival, int nights) => new(
        _property.Id,
        Utc(arrival),
        Utc(arrival.AddDays(nights)),
        2,
        0,
        new DirectBookingGuestInput("Ada", "Lovelace", "guest@example.com", null, "IT"),
        "2026-06-direct-checkout-v1",
        "127.0.0.1",
        null,
        PaymentOption.Immediate);

    // What the quote of a one-night stay on each night adds up to, in cents.
    private async Task<List<long>> QuotedCentsAsync(IEnumerable<DateOnly> nights)
    {
        var cents = new List<long>();
        foreach (var night in nights)
            cents.Add(StayPricing.ToCents((await _booking.QuoteDirectBookingAsync(QuoteInput(night, 1))).Lodging.Total));

        return cents;
    }

    // What the public calendar says each of the nights costs, in cents.
    private async Task<List<long>> CalendarCentsAsync(IEnumerable<DateOnly> nights)
    {
        var wanted = nights.ToList();
        var first = wanted.Min();
        var calendar = await _calendar.GetAsync(_property.Id, first, wanted.Max().AddDays(1));
        return wanted.Select(night => calendar.Days.Single(day => day.Date == night).NightlyPriceCents).ToList();
    }

    private sealed class FixedTenant(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
