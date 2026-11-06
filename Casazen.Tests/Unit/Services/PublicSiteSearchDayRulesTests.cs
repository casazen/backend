using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E08/F05/US01/T04 (NF-36): the search of a site with the rules of the nights, through the real service on EF InMemory: the rules of all the
/// houses are read once for the whole search (never one read per house), the cards follow them, and the price of a card is the one of the quote
/// with the price the host wrote in its calendar.
/// </summary>
public class PublicSiteSearchDayRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Arrival = new(2026, 12, 14);

    [Fact]
    public async Task SearchStays_ReadsTheRulesOfAllTheHousesOnce_TheCardsFollowThem_AndThePricesAreTheOnesTheHostsWrote()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"search-day-rules-{Guid.NewGuid():N}").Options);
        var org = NewOrg();
        var priced = NewHouse(org, "A con prezzo");
        var closed = NewHouse(org, "B chiusa");
        var plain = NewHouse(org, "C senza regole");
        db.Orgs.Add(org);
        db.Properties.AddRange(priced, closed, plain);
        db.PropertyDayRules.AddRange(
            new PropertyDayRule { OrgId = org.Id, PropertyId = priced.Id, StayDate = Arrival, Price = 150m },
            new PropertyDayRule { OrgId = org.Id, PropertyId = closed.Id, StayDate = Arrival, ClosedToArrival = true });
        await db.SaveChangesAsync();
        var configuration = new ConfigurationBuilder().Build();
        var clock = new FixedTimeProvider(Now);
        var rules = new CountingRules(new PropertyDayRuleService(
            db,
            Mock.Of<IOctorateBookingTargets>(),
            Mock.Of<Core.Features.IFeatureFlags>(),
            configuration,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PropertyDayRuleService>.Instance,
            clock));
        var taxRates = new Mock<ITouristTaxRateRepository>();
        taxRates
            .Setup(r => r.GetActiveInPeriodAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TouristTaxRate>());
        var search = new PublicSiteSearchService(db, taxRates.Object, configuration, new PublicReadCache(configuration), rules, clock);

        var result = await search.SearchStaysAsync(org, Criteria(Arrival, 2));

        // One read of the rules for the whole search, whatever the number of houses.
        Assert.Equal(1, rules.SiteReads);
        var cards = result.Items.ToDictionary(card => card.Name);
        Assert.Equal(3, cards.Count);

        // The house with a day price: free, and its card prices the stay as the quote does: 150 and 100 for the two nights.
        Assert.True(cards["A con prezzo"].Availability.Available);
        Assert.Equal((12500L, 25000L), (cards["A con prezzo"].Price.PerNightCents, cards["A con prezzo"].Price.TotalCents));
        // The house closed to arrival on the 14th: not free, with its reason, no total, and a first free date that is not the 14th.
        var refused = cards["B chiusa"];
        Assert.False(refused.Availability.Available);
        Assert.Equal(PublicStayReasons.ArrivalClosed, refused.Availability.Reason);
        Assert.Null(refused.Price.TotalCents);
        Assert.Equal(Arrival.AddDays(1), refused.Availability.FirstFreeCheckIn);
        // The house with no rule is as it always was.
        Assert.True(cards["C senza regole"].Availability.Available);
        Assert.Equal(20000L, cards["C senza regole"].Price.TotalCents);
        Assert.Equal(2, result.AvailableCount);
    }

    [Fact]
    public async Task SiteCalendar_ReadsTheRulesOnce_AndANightClosedToArrivalIsNotAvailableForTheHouse()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"site-day-rules-{Guid.NewGuid():N}").Options);
        var org = NewOrg();
        var only = NewHouse(org, "Unica");
        db.Orgs.Add(org);
        db.Properties.Add(only);
        db.PropertyDayRules.Add(new PropertyDayRule { OrgId = org.Id, PropertyId = only.Id, StayDate = Arrival, ClosedToArrival = true });
        db.PropertyDayRules.Add(new PropertyDayRule { OrgId = org.Id, PropertyId = only.Id, StayDate = Arrival.AddDays(1), MinNights = 4 });
        await db.SaveChangesAsync();
        var configuration = new ConfigurationBuilder().Build();
        var clock = new FixedTimeProvider(Now);
        var rules = new CountingRules(new PropertyDayRuleService(
            db,
            Mock.Of<IOctorateBookingTargets>(),
            Mock.Of<Core.Features.IFeatureFlags>(),
            configuration,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PropertyDayRuleService>.Instance,
            clock));
        var search = new PublicSiteSearchService(db, Mock.Of<ITouristTaxRateRepository>(), configuration, new PublicReadCache(configuration), rules, clock);

        var calendar = await search.GetSiteCalendarAsync(org, Arrival, Arrival.AddDays(3), 2, 0);

        Assert.Equal(1, rules.SiteReads);
        Assert.Equal(
            [(false, (int?)null), (true, 4), (true, 1)],
            calendar.Days.Select(day => (day.AnyAvailable, day.MinNights)));
    }

    // --- Helpers -----------------------------------------------------------------------------------------------

    private static OrgEntity NewOrg() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Casa Regole",
        Slug = $"regole-{Guid.NewGuid():N}"[..20],
        DisplayName = "Casa Regole",
        ContactEmail = "regole@example.com",
        OrgType = OrgType.Host,
        PlanTier = PlanTier.Starter,
        StripeConnectedAccountId = "acct_search_rules",
        ConnectChargesEnabled = true,
    };

    private static Property NewHouse(OrgEntity org, string name) => new()
    {
        OrgId = org.Id,
        OwnerId = "auth0|owner",
        Name = name,
        Address = $"Via {name} 1",
        City = "Como",
        IsActive = true,
        ComplianceStatus = PropertyComplianceStatus.Active,
        MaxGuests = 4,
        NightlyRate = 100m,
    };

    private static PublicStaysCriteria Criteria(DateOnly checkIn, int nights) => new(
        checkIn, checkIn.AddDays(nights), 2, 0, false, null, null, null, null, null, null, [], [], [], PublicSiteSearch.Recommended, 1, null, []);

    // The real service, counting how many times the search asked it for the rules of the houses of a site.
    private sealed class CountingRules(IPropertyDayRuleService inner) : IPropertyDayRuleService
    {
        public int SiteReads { get; private set; }

        public Task<CalendarDays> GetDaysAsync(HostScope scope, CalendarDaysQuery query, CancellationToken cancellationToken = default) =>
            inner.GetDaysAsync(scope, query, cancellationToken);

        public Task<IReadOnlyDictionary<DateOnly, DayRuleValues>> GetRulesAsync(
            Guid propertyId, DateOnly from, DateOnly toExclusive, CancellationToken cancellationToken = default) =>
            inner.GetRulesAsync(propertyId, from, toExclusive, cancellationToken);

        public Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<DateOnly, DayRuleValues>>> GetRulesForSiteAsync(
            IReadOnlyCollection<Guid> propertyIds, DateOnly from, DateOnly toExclusive, CancellationToken cancellationToken = default)
        {
            SiteReads++;
            return inner.GetRulesForSiteAsync(propertyIds, from, toExclusive, cancellationToken);
        }

        public Task<DayRuleApplyResult> ApplyAsync(
            Guid propertyId,
            DateOnly from,
            DateOnly toInclusive,
            IReadOnlySet<DayOfWeek>? days,
            DayRulePatch patch,
            string? userId,
            CancellationToken cancellationToken = default) =>
            inner.ApplyAsync(propertyId, from, toInclusive, days, patch, userId, cancellationToken);

        public void DiscardStaged() => inner.DiscardStaged();

        public bool IsDateConflict(Exception exception) => inner.IsDateConflict(exception);
    }
}
