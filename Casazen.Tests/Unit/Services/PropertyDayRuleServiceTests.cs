using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E08/F05/US01/T02 (NF-36, NF-78): the store of the price and the rules of the nights and the read of every day of the calendar, on EF InMemory.
/// The three proof tests are the money, permission and data rules of the brief; the others pin the behaviour around them.
/// </summary>
public class PropertyDayRuleServiceTests
{
    // 2026-10-09 10:00 UTC: the 9th of October in Rome and everywhere else the tests look.
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);

    // --- The proofs --------------------------------------------------------------------------------------------

    /// <summary>
    /// PROOF TEST: money rule. A price of 120.505 is stored as 120.51 (half away from zero, as the quote rounds), the price read back is the
    /// price stored, a price of 0 or 100000.01 (and one that rounds to nothing) is refused with its code and stages nothing, and the edges of the
    /// range are allowed.
    /// </summary>
    [Fact]
    public async Task DayRulePrice_StoredToTheCent_WithinBounds_Test()
    {
        await using var db = NewDb();
        var property = AddProperty(db, Guid.NewGuid());
        await db.SaveChangesAsync();
        var service = NewService(db);
        var night = new DateOnly(2026, 12, 24);

        var result = await service.ApplyAsync(property.Id, night, night, null, new DayRulePatch(Price: FieldPatch<decimal>.Set(120.505m)), "auth0|host");
        await db.SaveChangesAsync();

        Assert.Equal(new DayRuleApplyResult(1, 0, 0), result);
        var stored = await db.PropertyDayRules.AsNoTracking().SingleAsync();
        Assert.Equal(120.51m, stored.Price);
        Assert.Equal(night, stored.StayDate);
        var readBack = await service.GetRulesAsync(property.Id, night, night.AddDays(1));
        Assert.Equal(120.51m, readBack[night].Price);

        var next = night.AddDays(1);
        foreach (var refused in new[] { 0m, 0.004m, -1m, 100000.01m })
        {
            var ex = await Assert.ThrowsAsync<DomainRuleException>(
                () => service.ApplyAsync(property.Id, next, next, null, new DayRulePatch(Price: FieldPatch<decimal>.Set(refused)), "auth0|host"));
            Assert.Equal(DayRuleErrorCodes.InvalidPrice, ex.Code);
        }

        // A refusal stages nothing: the context holds the one saved row, as it was.
        Assert.All(db.ChangeTracker.Entries<PropertyDayRule>(), entry => Assert.Equal(EntityState.Unchanged, entry.State));

        // The edges of the range are allowed.
        await service.ApplyAsync(property.Id, next, next, null, new DayRulePatch(Price: FieldPatch<decimal>.Set(0.01m)), "auth0|host");
        await service.ApplyAsync(property.Id, next.AddDays(1), next.AddDays(1), null, new DayRulePatch(Price: FieldPatch<decimal>.Set(100000m)), "auth0|host");
        await db.SaveChangesAsync();
        var edges = await service.GetRulesAsync(property.Id, next, next.AddDays(2));
        Assert.Equal([0.01m, 100000m], edges.OrderBy(pair => pair.Key).Select(pair => pair.Value.Price!.Value));
    }

    /// <summary>
    /// PROOF TEST: permission rule. A collaborator limited to one property gets the days and the prices of that property only, even when it asks for
    /// the others by id: the hidden property of its own org and the property of another org are left out, as if they did not exist. The owner of the
    /// org reaches both of its properties and never the other org's.
    /// </summary>
    [Fact]
    public async Task CalendarDays_UnreachableProperty_IsLeftOut_Test()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var world = await HostScopeScenario.SeedAsync(db);
        var night = new DateOnly(2026, 12, 24);
        db.PropertyDayRules.AddRange(
            Rule(world.Granted, night, 555m),
            Rule(world.Hidden, night, 777m),
            Rule(world.OtherOrgProperty, night, 888m));
        await db.SaveChangesAsync();
        var service = NewService(db);
        var query = new CalendarDaysQuery(night, night.AddDays(2));
        var askedForAll = query with { PropertyIds = [world.Granted.Id, world.Hidden.Id, world.OtherOrgProperty.Id] };

        var collaborator = await service.GetDaysAsync(world.Restricted, query);
        var collaboratorAsking = await service.GetDaysAsync(world.Restricted, askedForAll);
        var owner = await service.GetDaysAsync(world.OrgWide, query);
        var ownerAsking = await service.GetDaysAsync(world.OrgWide, askedForAll);

        Assert.Equal(world.Granted.Id, Assert.Single(collaborator.Properties).PropertyId);
        Assert.Equal(world.Granted.Id, Assert.Single(collaboratorAsking.Properties).PropertyId);
        // Neither the days nor the price of what the collaborator cannot reach is in the answer.
        Assert.DoesNotContain(
            collaboratorAsking.Properties.SelectMany(p => p.Days),
            day => day.PriceCents is 77_700L or 88_800L);
        Assert.Equal(55_500L, collaboratorAsking.Properties.Single().Days.First(day => day.Date == night).PriceCents);
        Assert.Equal(
            new[] { world.Granted.Id, world.Hidden.Id }.Order(),
            owner.Properties.Select(p => p.PropertyId).Order());
        Assert.Equal(owner.Properties.Select(p => p.PropertyId), ownerAsking.Properties.Select(p => p.PropertyId));
        Assert.DoesNotContain(ownerAsking.Properties, p => p.PropertyId == world.OtherOrgProperty.Id);
        // A caller with nobody behind it gets nothing at all.
        Assert.Empty((await service.GetDaysAsync(new HostScope(Guid.NewGuid()), query)).Properties);
    }

    /// <summary>
    /// PROOF TEST: data rule. A rule saved for 2026-03-29 (the day the summer time starts) and for 2026-10-25 (the day it ends) is stored, read back
    /// and listed on the same date, whatever the time zone of the property: a stay date is a calendar date of the property and is never converted.
    /// Only «today» follows the zone: at the same instant the night of the 28th is over for a property in Auckland and ahead for one in Rome.
    /// </summary>
    [Fact]
    public async Task DayRules_StayDate_IsNeverShifted_Test()
    {
        var springForward = new DateOnly(2026, 3, 29);
        var fallBack = new DateOnly(2026, 10, 25);
        var early = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

        foreach (var zone in new[] { "Europe/Rome", "Pacific/Auckland", "America/Los_Angeles", "Asia/Tokyo" })
        {
            await using var db = NewDb();
            var orgId = Guid.NewGuid();
            var property = AddProperty(db, orgId, timezone: zone);
            await db.SaveChangesAsync();
            var service = NewService(db, early);

            await service.ApplyAsync(property.Id, springForward, springForward, null, new DayRulePatch(Price: FieldPatch<decimal>.Set(150m)), "auth0|host");
            await service.ApplyAsync(property.Id, fallBack, fallBack, null, new DayRulePatch(MinNights: FieldPatch<int>.Set(2)), "auth0|host");
            await db.SaveChangesAsync();

            var stored = await db.PropertyDayRules.AsNoTracking().OrderBy(r => r.StayDate).Select(r => r.StayDate).ToListAsync();
            Assert.Equal([springForward, fallBack], stored);
            var readBack = await service.GetRulesAsync(property.Id, springForward, fallBack.AddDays(1));
            Assert.Equal([springForward, fallBack], readBack.Keys.Order());
            var days = (await service.GetDaysAsync(new HostScope(orgId), new CalendarDaysQuery(springForward.AddDays(-1), springForward.AddDays(1), [property.Id])))
                .Properties.Single().Days;
            Assert.Equal([springForward.AddDays(-1), springForward, springForward.AddDays(1)], days.Select(day => day.Date));
            Assert.Equal([false, true, false], days.Select(day => day.HasRule));
            Assert.Equal(zone, (await service.GetDaysAsync(new HostScope(orgId), new CalendarDaysQuery(springForward, springForward))).Properties.Single().Timezone);
        }

        // 2026-03-28 20:00 UTC is already the 29th in Auckland (13 hours ahead) and still the 28th in Rome: the night of the 28th is over there only.
        var evening = new DateTimeOffset(2026, 3, 28, 20, 0, 0, TimeSpan.Zero);
        var counted = new Dictionary<string, DayRuleApplyResult>();
        foreach (var zone in new[] { "Europe/Rome", "Pacific/Auckland" })
        {
            await using var db = NewDb();
            var property = AddProperty(db, Guid.NewGuid(), timezone: zone);
            await db.SaveChangesAsync();
            counted[zone] = await NewService(db, evening).ApplyAsync(
                property.Id, springForward.AddDays(-1), springForward, null, new DayRulePatch(Price: FieldPatch<decimal>.Set(99m)), "auth0|host");
        }

        Assert.Equal(new DayRuleApplyResult(2, 0, 0), counted["Europe/Rome"]);
        Assert.Equal(new DayRuleApplyResult(1, 0, 1), counted["Pacific/Auckland"]);
    }

    // --- The change of the rules -------------------------------------------------------------------------------

    [Fact]
    public async Task Apply_NightsBeforeTodayOfTheProperty_AreSkippedAndCounted_TheRestIsStaged()
    {
        await using var db = NewDb();
        var property = AddProperty(db, Guid.NewGuid());
        await db.SaveChangesAsync();

        // Today is the 9th: the 7th and the 8th are over, the 9th (today) and the 10th are not.
        var result = await NewService(db).ApplyAsync(
            property.Id, new DateOnly(2026, 10, 7), new DateOnly(2026, 10, 10), null, new DayRulePatch(ClosedToArrival: FieldPatch<bool>.Set(true)), "auth0|host");
        await db.SaveChangesAsync();

        Assert.Equal(new DayRuleApplyResult(2, 0, 2), result);
        Assert.Equal(
            [new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 10)],
            await db.PropertyDayRules.AsNoTracking().OrderBy(r => r.StayDate).Select(r => r.StayDate).ToListAsync());
    }

    [Fact]
    public async Task Apply_OnlyTheChosenWeekdays_AreTouched()
    {
        await using var db = NewDb();
        var property = AddProperty(db, Guid.NewGuid());
        await db.SaveChangesAsync();

        // 2026-12-21 is a Monday: the week from Monday to Sunday, only the Friday and the Saturday.
        var result = await NewService(db).ApplyAsync(
            property.Id,
            new DateOnly(2026, 12, 21),
            new DateOnly(2026, 12, 27),
            new HashSet<DayOfWeek> { DayOfWeek.Friday, DayOfWeek.Saturday },
            new DayRulePatch(Price: FieldPatch<decimal>.Set(180m)),
            "auth0|host");
        await db.SaveChangesAsync();

        Assert.Equal(new DayRuleApplyResult(2, 0, 0), result);
        Assert.Equal(
            [new DateOnly(2026, 12, 25), new DateOnly(2026, 12, 26)],
            await db.PropertyDayRules.AsNoTracking().OrderBy(r => r.StayDate).Select(r => r.StayDate).ToListAsync());
    }

    [Fact]
    public async Task Apply_AnEmptySetOfWeekdays_IsRefused_NeverMeansEveryDay()
    {
        await using var db = NewDb();
        var property = AddProperty(db, Guid.NewGuid());
        await db.SaveChangesAsync();
        var night = new DateOnly(2026, 12, 24);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => NewService(db).ApplyAsync(
            property.Id, night, night.AddDays(6), new HashSet<DayOfWeek>(), new DayRulePatch(Price: FieldPatch<decimal>.Set(100m)), "auth0|host"));

        Assert.Equal(DayRuleErrorCodes.NothingToChange, refused.Code);
        Assert.Empty(db.ChangeTracker.Entries<PropertyDayRule>());
    }

    [Fact]
    public async Task Apply_ARowLeftWithNoValueIsDeleted_AndANightThePatchDoesNotChangeIsCountedUnchanged()
    {
        await using var db = NewDb();
        var property = AddProperty(db, Guid.NewGuid());
        var night = new DateOnly(2026, 12, 24);
        db.PropertyDayRules.Add(new PropertyDayRule { OrgId = property.OrgId, PropertyId = property.Id, StayDate = night, ClosedToArrival = true });
        db.PropertyDayRules.Add(new PropertyDayRule { OrgId = property.OrgId, PropertyId = property.Id, StayDate = night.AddDays(1), Price = 140m, MinNights = 2 });
        await db.SaveChangesAsync();
        var service = NewService(db);

        // Opening the arrival empties the first row; the second one has no arrival closed already, so it is unchanged. The third night has no row.
        var result = await service.ApplyAsync(
            property.Id, night, night.AddDays(2), null, new DayRulePatch(ClosedToArrival: FieldPatch<bool>.Clear), "auth0|host");
        await db.SaveChangesAsync();

        Assert.Equal(new DayRuleApplyResult(1, 2, 0), result);
        var left = Assert.Single(await db.PropertyDayRules.AsNoTracking().ToListAsync());
        Assert.Equal(night.AddDays(1), left.StayDate);
        Assert.Equal((140m, 2), (left.Price, left.MinNights));
    }

    [Fact]
    public async Task Apply_ANightThatChanges_KeepsWhatThePatchDoesNotNameAndRecordsWhoAndWhen()
    {
        await using var db = NewDb();
        var property = AddProperty(db, Guid.NewGuid());
        var night = new DateOnly(2026, 12, 24);
        var firstWritten = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        db.PropertyDayRules.Add(new PropertyDayRule
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            StayDate = night,
            Price = 140m,
            MinNights = 2,
            CreatedAt = firstWritten,
            UpdatedAt = firstWritten,
            UpdatedByUserId = "auth0|earlier",
        });
        await db.SaveChangesAsync();

        await NewService(db).ApplyAsync(property.Id, night, night, null, new DayRulePatch(MaxNights: FieldPatch<int>.Set(5)), "auth0|host");
        await db.SaveChangesAsync();

        var row = await db.PropertyDayRules.AsNoTracking().SingleAsync();
        Assert.Equal((140m, 2, 5), (row.Price, row.MinNights, row.MaxNights));
        Assert.Equal(firstWritten, row.CreatedAt);
        Assert.Equal(Now.UtcDateTime, row.UpdatedAt);
        Assert.Equal("auth0|host", row.UpdatedByUserId);
        Assert.Equal(property.OrgId, row.OrgId);
    }

    [Fact]
    public async Task Apply_AValueThatIsNotAllowedOnTheMergedNight_RefusesTheWholeChangeAndStagesNothing()
    {
        await using var db = NewDb();
        var property = AddProperty(db, Guid.NewGuid());
        var night = new DateOnly(2026, 12, 24);
        db.PropertyDayRules.Add(new PropertyDayRule { OrgId = property.OrgId, PropertyId = property.Id, StayDate = night.AddDays(2), MinNights = 4 });
        await db.SaveChangesAsync();

        // A maximum of 3 nights is fine on the nights without a rule and below the minimum of 4 on the third night: the third one refuses all.
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => NewService(db).ApplyAsync(
            property.Id, night, night.AddDays(3), null, new DayRulePatch(MaxNights: FieldPatch<int>.Set(3)), "auth0|host"));

        Assert.Equal(DayRuleErrorCodes.InvalidStay, refused.Code);
        Assert.DoesNotContain(db.ChangeTracker.Entries<PropertyDayRule>(), entry => entry.State != EntityState.Unchanged);
    }

    [Fact]
    public async Task Apply_TheRangeAndThePatchAreCheckedBeforeAnything_WithTheirCodes()
    {
        await using var db = NewDb();
        var property = AddProperty(db, Guid.NewGuid());
        await db.SaveChangesAsync();
        var service = NewService(db);
        var first = new DateOnly(2026, 12, 1);

        async Task<string> CodeOf(DateOnly from, DateOnly to, DayRulePatch patch) =>
            (await Assert.ThrowsAsync<DomainRuleException>(() => service.ApplyAsync(property.Id, from, to, null, patch, "auth0|host"))).Code;

        var price = new DayRulePatch(Price: FieldPatch<decimal>.Set(100m));
        Assert.Equal(DayRuleErrorCodes.RangeInvalid, await CodeOf(first, first.AddDays(-1), price));
        Assert.Equal(DayRuleErrorCodes.RangeTooLong, await CodeOf(first, first.AddDays(DayRuleLimits.MaxRangeDays), price));
        Assert.Equal(DayRuleErrorCodes.NothingToChange, await CodeOf(first, first, new DayRulePatch()));
        // A year and a day of a leap year is the longest range that goes through.
        var year = await service.ApplyAsync(property.Id, first, first.AddDays(DayRuleLimits.MaxRangeDays - 1), null, price, "auth0|host");
        Assert.Equal(new DayRuleApplyResult(DayRuleLimits.MaxRangeDays, 0, 0), year);
    }

    [Fact]
    public async Task Apply_APropertyOfAnotherOrg_IsNotFound_ThroughTheTenantFilter()
    {
        var databaseName = $"day-rules-tenant-{Guid.NewGuid():N}";
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        Guid propertyOfB;
        await using (var system = NewDb(databaseName, tenant: null))
        {
            propertyOfB = AddProperty(system, orgB).Id;
            await system.SaveChangesAsync();
        }

        await using var asOrgA = NewDb(databaseName, new FixedTenantContext(orgA));
        var night = new DateOnly(2026, 12, 24);

        await Assert.ThrowsAsync<NotFoundException>(() => NewService(asOrgA).ApplyAsync(
            propertyOfB, night, night, null, new DayRulePatch(Price: FieldPatch<decimal>.Set(100m)), "auth0|host"));
        await Assert.ThrowsAsync<NotFoundException>(() => NewService(asOrgA).ApplyAsync(
            Guid.NewGuid(), night, night, null, new DayRulePatch(Price: FieldPatch<decimal>.Set(100m)), "auth0|host"));
        Assert.Empty(asOrgA.ChangeTracker.Entries<PropertyDayRule>());
    }

    [Fact]
    public async Task Apply_AfterAFailedSave_TheCallerForgetsTheStagedRowsAndStagesAgain()
    {
        await using var db = NewDb();
        var property = AddProperty(db, Guid.NewGuid());
        await db.SaveChangesAsync();
        var service = NewService(db);
        var night = new DateOnly(2026, 12, 24);
        var patch = new DayRulePatch(Price: FieldPatch<decimal>.Set(100m));

        await service.ApplyAsync(property.Id, night, night, null, patch, "auth0|host");
        Assert.Single(db.ChangeTracker.Entries<PropertyDayRule>(), entry => entry.State == EntityState.Added);

        // The save did not happen (say, the unique index refused it): what was staged is forgotten, and staging again gives the same single row.
        service.DiscardStaged();
        Assert.Empty(db.ChangeTracker.Entries<PropertyDayRule>());
        await service.ApplyAsync(property.Id, night, night, null, patch, "auth0|host");
        await db.SaveChangesAsync();
        Assert.Single(await db.PropertyDayRules.AsNoTracking().ToListAsync());

        // The service tells a refusal of that unique index from anything else.
        Assert.False(service.IsDateConflict(new InvalidOperationException("no")));
        Assert.False(service.IsDateConflict(new DbUpdateException("no", new InvalidOperationException("inner"))));
    }

    // --- The days of the calendar ------------------------------------------------------------------------------

    [Fact]
    public async Task GetDays_ThePriceOfEachNightIsTheNumberOfTheQuote_WithItsSourceAndItsRules()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var property = AddProperty(db, orgId, rate: 100m, weekend: 20m, minNights: 2);
        // Thursday the 24th: a day rule with a price and the limits; Friday the 25th: a seasonal price the host confirmed; Saturday the 26th: the
        // weekend (and taken by a block); Sunday the 27th: the rate. Monday the 28th: a rule without a price over a seasonal one.
        var thursday = new DateOnly(2026, 12, 24);
        db.PropertyDayRules.AddRange(
            new PropertyDayRule
            {
                OrgId = orgId,
                PropertyId = property.Id,
                StayDate = thursday,
                Price = 150m,
                MinNights = 3,
                MaxNights = 6,
                ClosedToArrival = true,
                MinAdvanceDays = 2,
                MaxAdvanceDays = 90,
            },
            new PropertyDayRule { OrgId = orgId, PropertyId = property.Id, StayDate = thursday.AddDays(4), ClosedToDeparture = true });
        db.SeasonalPriceSuggestions.AddRange(
            Seasonal(property, thursday.AddDays(1), 130m),
            Seasonal(property, thursday.AddDays(4), 135m));
        db.CalendarBlocks.Add(new CalendarBlock
        {
            OrgId = orgId,
            PropertyId = property.Id,
            Source = CalendarBlockSource.Manual,
            StartUtc = new DateTime(2026, 12, 26, 0, 0, 0, DateTimeKind.Utc),
            EndUtc = new DateTime(2026, 12, 27, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();

        var calendar = await NewService(db).GetDaysAsync(new HostScope(orgId), new CalendarDaysQuery(thursday, thursday.AddDays(4)));

        Assert.Equal(new DateOnly(2026, 10, 9), calendar.Today);
        var listed = Assert.Single(calendar.Properties);
        Assert.Equal((property.Id, "Europe/Rome", "EUR", false), (listed.PropertyId, listed.Timezone, listed.Currency, listed.Mirrored));
        Assert.Equal(5, listed.Days.Count);
        var days = listed.Days.ToDictionary(day => day.Date);

        var rule = days[thursday];
        Assert.Equal((15000L, DayPriceSource.DayRule, 3, 6), (rule.PriceCents, rule.PriceSource, rule.MinNights, rule.MaxNights));
        Assert.Equal((true, false, 2, 90, true, false), (rule.ClosedToArrival, rule.ClosedToDeparture, rule.MinAdvanceDays, rule.MaxAdvanceDays, rule.HasRule, rule.Taken));

        var seasonal = days[thursday.AddDays(1)];
        Assert.Equal((13000L, DayPriceSource.Seasonal, 2), (seasonal.PriceCents, seasonal.PriceSource, seasonal.MinNights));
        Assert.Equal((null, false, false), (seasonal.MaxNights, seasonal.HasRule, seasonal.Taken));

        var weekend = days[thursday.AddDays(2)];
        Assert.Equal((12000L, DayPriceSource.Weekend, true), (weekend.PriceCents, weekend.PriceSource, weekend.Taken));

        var plain = days[thursday.AddDays(3)];
        Assert.Equal((10000L, DayPriceSource.Base, 2, false), (plain.PriceCents, plain.PriceSource, plain.MinNights, plain.Taken));

        // A rule with no price leaves the seasonal price, and says the departure is closed.
        var closedDeparture = days[thursday.AddDays(4)];
        Assert.Equal((13500L, DayPriceSource.Seasonal, true, true), (closedDeparture.PriceCents, closedDeparture.PriceSource, closedDeparture.ClosedToDeparture, closedDeparture.HasRule));

        // Each number is the one the quote adds up for a one-night stay.
        foreach (var day in listed.Days)
        {
            var hostPrices = DayRulePricing.HostPrices(
                new Dictionary<DateOnly, decimal> { [thursday.AddDays(1)] = 130m, [thursday.AddDays(4)] = 135m },
                new Dictionary<DateOnly, DayRuleValues> { [thursday] = new(Price: 150m) });
            Assert.Equal(StayPricing.ToCents(StayPricing.NightPrice(day.Date, 100m, 20m, hostPrices)), day.PriceCents);
        }
    }

    [Fact]
    public async Task GetDays_TheLimitsAreThoseOfTheCalendarOfTheHost()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var property = AddProperty(db, orgId);
        await db.SaveChangesAsync();
        var service = NewService(db);
        var first = new DateOnly(2026, 12, 1);

        var tooLong = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.GetDaysAsync(new HostScope(orgId), new CalendarDaysQuery(first, first.AddDays(HostCalendarLimits.MaxWindowDays))));
        var tooMany = await Assert.ThrowsAsync<DomainRuleException>(() => service.GetDaysAsync(
            new HostScope(orgId),
            new CalendarDaysQuery(first, first, Enumerable.Range(0, HostCalendarLimits.MaxProperties + 1).Select(_ => Guid.NewGuid()).ToList())));

        Assert.Equal(HostCalendarErrorCodes.WindowTooLarge, tooLong.Code);
        Assert.Equal(HostCalendarErrorCodes.TooManyProperties, tooMany.Code);
        var longest = await service.GetDaysAsync(new HostScope(orgId), new CalendarDaysQuery(first, first.AddDays(HostCalendarLimits.MaxWindowDays - 1)));
        Assert.Equal(HostCalendarLimits.MaxWindowDays, Assert.Single(longest.Properties).Days.Count);
        Assert.Equal(property.Id, longest.Properties[0].PropertyId);
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetDaysAsync(new HostScope(orgId), new CalendarDaysQuery(first, first.AddDays(-1))));
    }

    [Fact]
    public async Task GetDays_ALongTermOrInactiveProperty_IsNotOnTheCalendar()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var shortRent = AddProperty(db, orgId);
        var longTerm = AddProperty(db, orgId);
        longTerm.RentalMode = RentalMode.Long;
        var inactive = AddProperty(db, orgId);
        inactive.IsActive = false;
        await db.SaveChangesAsync();

        var calendar = await NewService(db).GetDaysAsync(new HostScope(orgId), new CalendarDaysQuery(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 2)));

        Assert.Equal(shortRent.Id, Assert.Single(calendar.Properties).PropertyId);
    }

    [Fact]
    public async Task GetDays_MirroredIsTrueWhenTheChannelManagerHasATarget_AndNeverBlocksTheCalendar()
    {
        await using var db = NewDb();
        var orgId = Guid.NewGuid();
        var mirrored = AddProperty(db, orgId, name: "A mirrored");
        var unmapped = AddProperty(db, orgId, name: "B unmapped");
        var failing = AddProperty(db, orgId, name: "C failing");
        await db.SaveChangesAsync();
        var targets = new Mock<IOctorateBookingTargets>();
        targets.Setup(t => t.LookupAsync(mirrored.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OctorateTargetLookup.Of(new OctorateBookingTarget("ACC-TEST", "ROOM-TEST")));
        targets.Setup(t => t.LookupAsync(unmapped.Id, It.IsAny<CancellationToken>())).ReturnsAsync(OctorateTargetLookup.NotMirrored);
        targets.Setup(t => t.LookupAsync(failing.Id, It.IsAny<CancellationToken>())).ThrowsAsync(new OctorateServerException(500));
        var query = new CalendarDaysQuery(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 2));

        var on = await NewService(db, targets: targets.Object, connector: true).GetDaysAsync(new HostScope(orgId), query);
        var off = await NewService(db, targets: targets.Object, connector: false).GetDaysAsync(new HostScope(orgId), query);

        // A failed lookup leaves the property shown as not mirrored; the days of every property are there.
        Assert.Equal([true, false, false], on.Properties.Select(p => p.Mirrored));
        Assert.All(on.Properties, p => Assert.Equal(2, p.Days.Count));
        // With the connector off nothing is asked of the channel manager at all.
        Assert.All(off.Properties, p => Assert.False(p.Mirrored));
        targets.Verify(t => t.LookupAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    // --- The rules for the booking site ------------------------------------------------------------------------

    [Fact]
    public async Task GetRulesForSite_IsAPublicRead_TheHostRead_IsNot()
    {
        var databaseName = $"day-rules-site-{Guid.NewGuid():N}";
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var night = new DateOnly(2026, 12, 24);
        var propertyOfA = Guid.NewGuid();
        await using (var system = NewDb(databaseName, tenant: null))
        {
            system.PropertyDayRules.AddRange(
                new PropertyDayRule { OrgId = orgA, PropertyId = propertyOfA, StayDate = night, Price = 150m, MinNights = 3, UpdatedByUserId = "auth0|secret-user" },
                new PropertyDayRule { OrgId = orgA, PropertyId = propertyOfA, StayDate = night.AddDays(5), ClosedToArrival = true });
            await system.SaveChangesAsync();
        }

        // A signed-in host of another org looks at the site of org A.
        await using var asOrgB = NewDb(databaseName, new FixedTenantContext(orgB));
        var service = NewService(asOrgB);

        var forTheSite = await service.GetRulesForSiteAsync([propertyOfA, Guid.NewGuid()], night, night.AddDays(10));
        var forTheHost = await service.GetRulesAsync(propertyOfA, night, night.AddDays(10));

        var rules = Assert.Single(forTheSite).Value;
        Assert.Equal(propertyOfA, Assert.Single(forTheSite).Key);
        Assert.Equal(new DayRuleValues(Price: 150m, MinNights: 3), rules[night]);
        Assert.True(rules[night.AddDays(5)].ClosedToArrival);
        Assert.Empty(forTheHost);
        // Only the nights asked for, and nothing for no house or an empty range.
        Assert.Single(Assert.Single(await service.GetRulesForSiteAsync([propertyOfA], night, night.AddDays(1))).Value);
        Assert.Empty(await service.GetRulesForSiteAsync([], night, night.AddDays(1)));
        Assert.Empty(await service.GetRulesForSiteAsync([propertyOfA], night, night));
        Assert.Empty(await service.GetRulesAsync(propertyOfA, night, night));
    }

    // --- Helpers -----------------------------------------------------------------------------------------------

    private static AppDbContext NewDb() => NewDb($"day-rules-{Guid.NewGuid():N}", tenant: null);

    private static AppDbContext NewDb(string databaseName, ITenantContext? tenant) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(databaseName).Options, tenant);

    private static PropertyDayRuleService NewService(
        AppDbContext db,
        DateTimeOffset? now = null,
        IOctorateBookingTargets? targets = null,
        bool connector = false)
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(connector);
        return new PropertyDayRuleService(
            db,
            targets ?? Mock.Of<IOctorateBookingTargets>(),
            flags.Object,
            new ConfigurationBuilder().Build(),
            NullLogger<PropertyDayRuleService>.Instance,
            new FixedTimeProvider(now ?? Now));
    }

    private static Property AddProperty(
        AppDbContext db,
        Guid orgId,
        string name = "Casa di prova",
        string timezone = "Europe/Rome",
        decimal rate = 100m,
        decimal weekend = 0m,
        int? minNights = null)
    {
        var property = HostScopeScenario.NewProperty(orgId, "auth0|owner", name);
        property.Timezone = timezone;
        property.NightlyRate = rate;
        property.WeekendSurchargePercent = weekend;
        property.MinNights = minNights;
        db.Properties.Add(property);
        return property;
    }

    private static PropertyDayRule Rule(Property property, DateOnly night, decimal price) => new()
    {
        OrgId = property.OrgId,
        PropertyId = property.Id,
        StayDate = night,
        Price = price,
    };

    private static SeasonalPriceSuggestion Seasonal(Property property, DateOnly night, decimal applied) => new()
    {
        OrgId = property.OrgId,
        PropertyId = property.Id,
        StayDate = night,
        BasePrice = property.NightlyRate,
        SuggestedPrice = applied,
        Multiplier = 1m,
        AppliedPrice = applied,
        AppliedAt = Now.UtcDateTime,
        ComputedAt = Now.UtcDateTime,
    };

    private sealed class FixedTenantContext(Guid? orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
