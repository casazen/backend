using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SR-03: the booking list that holds up with many bookings. By days (a stay is in the range when it has a day in it, arrival and
/// departure day included), by status, by text (guest, property, booking code), a page at a time in a total order, with the
/// total; always inside the caller's scope.
/// </summary>
public class BookingSearchServiceTests
{
    private const string Collaborator = "auth0|collaboratore-sr03";

    private static DateTime Day(int month, int day) => new(2026, month, day, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Fixture(
        AppDbContext Db,
        Guid OrgId,
        Property Trullo,
        Property CasaBianca,
        Booking B1,
        Booking B2,
        Booking B3,
        Booking B4,
        HostScope OrgWide,
        HostScope Restricted);

    /// <summary>
    ///   b1  Trullo       Anna Verdi   Confirmed   10-13 Oct   ABCDE-FGHJK
    ///   b2  Trullo       Mario Rossi  Cancelled   12-14 Oct   MNPQR-STVWX
    ///   b3  Casa Bianca  Luca Neri    CheckedOut  1-5 Sep     01234-56789
    ///   b4  Casa Bianca  Anna Verdi   Pending     1-3 Nov     ZZZZZ-00000
    /// plus a booking of Anna Verdi in another org, that nobody of this one may ever see.
    /// </summary>
    private static async Task<Fixture> SeedAsync()
    {
        var db = HostScopeScenario.NewInMemoryDb();
        var orgId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();
        var trullo = HostScopeScenario.NewProperty(orgId, "auth0|titolare-sr03", "Trullo");
        var casaBianca = HostScopeScenario.NewProperty(orgId, "auth0|titolare-sr03", "Casa Bianca");
        var foreign = HostScopeScenario.NewProperty(otherOrgId, "auth0|altro-sr03", "Trullo");
        db.Properties.AddRange(trullo, casaBianca, foreign);
        db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = orgId, UserId = Collaborator, PropertyId = trullo.Id });

        var anna = Person(orgId, "Anna", "Verdi", "anna.verdi@example.com");
        var mario = Person(orgId, "Mario", "Rossi", "mario.rossi@example.com");
        var luca = Person(orgId, "Luca", "Neri", "luca.neri@example.com");
        var foreignAnna = Person(otherOrgId, "Anna", "Verdi", "anna.verdi@altro.example.com");
        db.Guests.AddRange(anna, mario, luca, foreignAnna);

        var b1 = Stay(trullo, anna, BookingStatus.Confirmed, Day(10, 10), Day(10, 13), "ABCDEFGHJK");
        var b2 = Stay(trullo, mario, BookingStatus.Cancelled, Day(10, 12), Day(10, 14), "MNPQRSTVWX");
        var b3 = Stay(casaBianca, luca, BookingStatus.CheckedOut, Day(9, 1), Day(9, 5), "0123456789");
        var b4 = Stay(casaBianca, anna, BookingStatus.Pending, Day(11, 1), Day(11, 3), "ZZZZZ00000");
        db.Bookings.AddRange(b1, b2, b3, b4, Stay(foreign, foreignAnna, BookingStatus.Confirmed, Day(10, 10), Day(10, 13), "ABCDEFGH11"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return new Fixture(
            db, orgId, trullo, casaBianca, b1, b2, b3, b4, new HostScope(orgId), new HostScope(orgId, GrantedToUserId: Collaborator));
    }

    private static Guest Person(Guid orgId, string first, string last, string email) =>
        new() { OrgId = orgId, FirstName = first, LastName = last, Email = email };

    private static Booking Stay(Property property, Guest guest, BookingStatus status, DateTime checkIn, DateTime checkOut, string code) => new()
    {
        OrgId = property.OrgId,
        PropertyId = property.Id,
        GuestId = guest.Id,
        Status = status,
        Source = BookingSource.Direct,
        CheckInDate = checkIn,
        CheckOutDate = checkOut,
        NumberOfGuests = 2,
        BookingCode = code,
    };

    private static BookingSearchService Search(AppDbContext db) => new(db);

    private static async Task<List<Guid>> IdsAsync(Fixture world, BookingSearchCriteria criteria, HostScope? scope = null) =>
        (await Search(world.Db).SearchAsync(scope ?? world.OrgWide, criteria)).Items.Select(b => b.Id).ToList();

    // --- Without criteria ------------------------------------------------------------------------------

    [Fact]
    public async Task NoCriteria_EveryBookingOfTheScope_LatestCheckInFirst_WithTheirPropertyAndGuest()
    {
        var world = await SeedAsync();

        var page = await Search(world.Db).SearchAsync(world.OrgWide, new BookingSearchCriteria());

        // November, October (the 12th and the 10th), September; nothing of the other org.
        Assert.Equal([world.B4.Id, world.B2.Id, world.B1.Id, world.B3.Id], page.Items.Select(b => b.Id));
        Assert.Equal(4, page.TotalCount);
        Assert.Equal(1, page.Page);
        Assert.Equal(BookingSearchCriteria.DefaultPageSize, page.PageSize);
        var first = page.Items[0];
        Assert.Equal("Casa Bianca", first.Property.Name);
        Assert.Equal("Anna", first.Guest.FirstName);
    }

    // --- Status -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Status_OneOrMany_AndNoneMeansEvery()
    {
        var world = await SeedAsync();

        Assert.Equal([world.B1.Id], await IdsAsync(world, new BookingSearchCriteria(Statuses: [BookingStatus.Confirmed])));
        Assert.Equal(
            [world.B4.Id, world.B1.Id],
            await IdsAsync(world, new BookingSearchCriteria(Statuses: [BookingStatus.Pending, BookingStatus.Confirmed])));
        Assert.Equal(4, (await Search(world.Db).SearchAsync(world.OrgWide, new BookingSearchCriteria(Statuses: []))).TotalCount);
    }

    // --- Days ------------------------------------------------------------------------------------------

    [Theory]
    // (from, to) as (month, day) pairs, 0 for none; the stays b1 10-13 Oct, b2 12-14 Oct, b3 1-5 Sep, b4 1-3 Nov.
    [InlineData(10, 13, 0, 0, "b4,b2,b1")]
    [InlineData(10, 14, 0, 0, "b4,b2")]
    [InlineData(10, 15, 0, 0, "b4")]
    [InlineData(0, 0, 10, 10, "b1,b3")]
    [InlineData(0, 0, 10, 9, "b3")]
    [InlineData(10, 11, 10, 11, "b1")]
    [InlineData(10, 1, 10, 31, "b2,b1")]
    [InlineData(9, 5, 9, 5, "b3")]
    [InlineData(9, 6, 10, 9, "")]
    public async Task Days_AStayIsInTheRangeWhenItHasADayInIt_ArrivalAndDepartureDaysIncluded(
        int fromMonth, int fromDay, int toMonth, int toDay, string expected)
    {
        var world = await SeedAsync();
        var names = new Dictionary<string, Guid> { ["b1"] = world.B1.Id, ["b2"] = world.B2.Id, ["b3"] = world.B3.Id, ["b4"] = world.B4.Id };
        var criteria = new BookingSearchCriteria(
            From: fromMonth == 0 ? null : new DateOnly(2026, fromMonth, fromDay),
            To: toMonth == 0 ? null : new DateOnly(2026, toMonth, toDay));

        var ids = await IdsAsync(world, criteria);

        Assert.Equal(
            expected.Length == 0 ? [] : expected.Split(',').Select(name => names[name]).ToList(),
            ids);
    }

    // --- Text ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("verdi", "b4,b1")]
    [InlineData("ANNA", "b4,b1")]
    [InlineData("anna verdi", "b4,b1")]
    [InlineData("verdi anna", "b4,b1")]
    [InlineData("  Rossi ", "b2")]
    [InlineData("neri", "b3")]
    [InlineData("anna.verdi@", "b4,b1")]
    [InlineData("casa bia", "b4,b3")]
    [InlineData("trullo", "b2,b1")]
    [InlineData("ABCDE-FGHJK", "b1")]
    [InlineData("abcde fghjk", "b1")]
    [InlineData("fghj", "b1")]
    [InlineData("0123", "b3")]
    [InlineData("nessuno", "")]
    [InlineData("%", "")]
    [InlineData("_", "")]
    public async Task Text_GuestPropertyOrBookingCode_AnyCase(string text, string expected)
    {
        var world = await SeedAsync();
        var names = new Dictionary<string, Guid> { ["b1"] = world.B1.Id, ["b2"] = world.B2.Id, ["b3"] = world.B3.Id, ["b4"] = world.B4.Id };

        var ids = await IdsAsync(world, new BookingSearchCriteria(Query: text));

        Assert.Equal(
            expected.Length == 0 ? [] : expected.Split(',').Select(name => names[name]).ToList(),
            ids);
    }

    [Fact]
    public async Task Text_AVeryLongOne_IsCutAndStillAnswers()
    {
        var world = await SeedAsync();

        var page = await Search(world.Db).SearchAsync(world.OrgWide, new BookingSearchCriteria(Query: new string('a', 5000)));

        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task Filters_PropertyAndGuest_AndAllOfThemTogether()
    {
        var world = await SeedAsync();

        Assert.Equal([world.B2.Id, world.B1.Id], await IdsAsync(world, new BookingSearchCriteria(PropertyId: world.Trullo.Id)));
        Assert.Equal([world.B4.Id, world.B1.Id], await IdsAsync(world, new BookingSearchCriteria(GuestId: world.B1.GuestId)));
        Assert.Equal(
            [world.B1.Id],
            await IdsAsync(
                world,
                new BookingSearchCriteria(
                    From: new DateOnly(2026, 10, 1),
                    To: new DateOnly(2026, 10, 31),
                    Statuses: [BookingStatus.Confirmed, BookingStatus.Pending],
                    Query: "verdi",
                    PropertyId: world.Trullo.Id)));
    }

    // --- Pages -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Pages_AreStable_NoBookingTwiceNorMissing_EvenWithTheSameCheckInDate()
    {
        var world = await SeedAsync();
        var guest = await world.Db.Guests.FirstAsync(g => g.OrgId == world.OrgId);
        // 25 stays that start the same day: only the id can tell them apart.
        var ties = Enumerable.Range(0, 25)
            .Select(i => Stay(world.Trullo, guest, BookingStatus.Confirmed, Day(12, 20), Day(12, 22), $"TIE{i:0000000}"))
            .ToList();
        world.Db.Bookings.AddRange(ties);
        await world.Db.SaveChangesAsync();

        var seen = new List<Guid>();
        for (var page = 1; page <= 4; page++)
        {
            var result = await Search(world.Db).SearchAsync(world.OrgWide, new BookingSearchCriteria(Page: page, PageSize: 10));
            Assert.Equal(29, result.TotalCount);
            Assert.Equal(page, result.Page);
            seen.AddRange(result.Items.Select(b => b.Id));
        }

        Assert.Equal(29, seen.Count);
        Assert.Equal(29, seen.Distinct().Count());
        // The same order as one big page.
        var all = await Search(world.Db).SearchAsync(world.OrgWide, new BookingSearchCriteria(PageSize: 100));
        Assert.Equal(all.Items.Select(b => b.Id), seen);
        // The ties come first (the 20th of December), by id.
        Assert.Equal(ties.Select(t => t.Id).Order(), all.Items.Take(25).Select(b => b.Id));
    }

    [Fact]
    public async Task Pages_TheNumbersAreBroughtWithinTheLimits()
    {
        var world = await SeedAsync();

        var low = await Search(world.Db).SearchAsync(world.OrgWide, new BookingSearchCriteria(Page: -3, PageSize: 0));
        var high = await Search(world.Db).SearchAsync(world.OrgWide, new BookingSearchCriteria(Page: int.MaxValue, PageSize: 100_000));

        Assert.Equal((1, 1), (low.Page, low.PageSize));
        Assert.Single(low.Items);
        Assert.Equal(4, low.TotalCount);
        Assert.Equal((BookingSearchCriteria.MaxPage, BookingSearchCriteria.MaxPageSize), (high.Page, high.PageSize));
        Assert.Empty(high.Items);
        Assert.Equal(4, high.TotalCount);
    }

    // --- The scope --------------------------------------------------------------------------------------

    [Fact]
    public async Task Scope_ACollaboratorLimitedToSomePropertiesSeesOnlyThose_WhateverTheCriteria()
    {
        var world = await SeedAsync();

        // Everything, a text that also matches the other property's bookings, and the other property asked for by id.
        Assert.Equal([world.B2.Id, world.B1.Id], await IdsAsync(world, new BookingSearchCriteria(), world.Restricted));
        Assert.Equal([world.B1.Id], await IdsAsync(world, new BookingSearchCriteria(Query: "anna"), world.Restricted));
        Assert.Empty(await IdsAsync(world, new BookingSearchCriteria(PropertyId: world.CasaBianca.Id), world.Restricted));
        var page = await Search(world.Db).SearchAsync(world.Restricted, new BookingSearchCriteria());
        Assert.Equal(2, page.TotalCount);
    }

    [Fact]
    public async Task Scope_NeverAnotherOrg_EvenForTheSameGuestNameAndTheSameCode()
    {
        var world = await SeedAsync();

        var byName = await IdsAsync(world, new BookingSearchCriteria(Query: "anna verdi"));
        var byCode = await IdsAsync(world, new BookingSearchCriteria(Query: "ABCDEFGH11"));

        Assert.Equal(2, byName.Count);
        Assert.Empty(byCode);
        Assert.Equal(4, (await Search(world.Db).SearchAsync(new HostScope(world.OrgId), new BookingSearchCriteria())).TotalCount);
    }
}
