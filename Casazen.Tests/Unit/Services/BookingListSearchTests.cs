using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SR-03b on the booking list: the arrival days, the chips and their counters, the channel, the state of the online check-in link,
/// the order, and what each row says besides the booking (how much was paid, where the Alloggiati communication stands). Always
/// inside the caller's scope. Today is 9 October 2026 in Rome.
/// </summary>
public class BookingListSearchTests
{
    private const string Collaborator = "auth0|collaboratore-sr03b";

    private static readonly TimeProvider Clock = new Casazen.Tests.Unit.FixedTimeProvider(HostScopeScenario.Now);
    private static readonly DateTime Now = HostScopeScenario.Now.UtcDateTime;

    private static DateTime Day(int month, int day) => new(2026, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static BookingSearchService Search(AppDbContext db) => new(db, new ConfigurationBuilder().Build(), Clock);

    private sealed record Fixture(
        AppDbContext Db,
        Guid OrgId,
        Property Trullo,
        Property CasaBianca,
        IReadOnlyDictionary<string, Booking> B,
        HostScope OrgWide,
        HostScope Restricted);

    private Guid Id(Fixture world, string name) => world.B[name].Id;

    /// <summary>
    ///   name  property      guest             status      source      stay           total   chip
    ///   b1    Trullo        Anna Verdi        Confirmed   Direct      10-13 Oct      300     in arrivo
    ///   b2    Trullo        Mario Rossi       Confirmed   Airbnb       9-12 Oct      500     in corso (arrives today)
    ///   b3    Casa Bianca   Luca Neri         CheckedIn   Manual       7-10 Oct      400     in corso
    ///   b4    Casa Bianca   Sara bianchi      CheckedOut  BookingCom   1-4 Oct       200     concluse
    ///   b5    Trullo        Gino Gialli       Cancelled   Expedia     20-22 Oct      250     cancellate
    ///   b6    Trullo        Elena Gatti       Pending     Direct      25-28 Oct      600     da confermare (a request for the host)
    ///   b7    Casa Bianca   Paolo Zanetti     Pending     Direct      15-17 Oct      350     in arrivo (a hold waiting for the payment)
    ///   b8    Trullo        Marta Ferrari     Confirmed   Local       20-25 Oct      700     in arrivo
    /// plus a confirmed stay of another org. The collaborator reaches the Trullo only.
    /// </summary>
    private static async Task<Fixture> SeedAsync()
    {
        var db = HostScopeScenario.NewInMemoryDb();
        var orgId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();
        var trullo = HostScopeScenario.NewProperty(orgId, "auth0|titolare-sr03b", "Trullo");
        var casaBianca = HostScopeScenario.NewProperty(orgId, "auth0|titolare-sr03b", "Casa Bianca");
        var foreign = HostScopeScenario.NewProperty(otherOrgId, "auth0|altro-sr03b", "Villa Altrove");
        db.Properties.AddRange(trullo, casaBianca, foreign);
        db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = orgId, UserId = Collaborator, PropertyId = trullo.Id });

        Guest Person(Guid org, string first, string last)
        {
            var guest = new Guest { OrgId = org, FirstName = first, LastName = last, Email = $"{Guid.NewGuid():N}@example.com" };
            db.Guests.Add(guest);
            return guest;
        }

        Booking Stay(Property property, Guest guest, BookingStatus status, BookingSource source, DateTime checkIn, DateTime checkOut, decimal total, int createdDay)
        {
            var booking = new Booking
            {
                OrgId = property.OrgId,
                PropertyId = property.Id,
                GuestId = guest.Id,
                Status = status,
                Source = source,
                CheckInDate = checkIn,
                CheckOutDate = checkOut,
                NumberOfGuests = 2,
                BasePrice = total,
                TotalPrice = total,
                CreatedAt = At(9, createdDay, 12),
                UpdatedAt = At(9, createdDay, 12),
            };
            db.Bookings.Add(booking);
            return booking;
        }

        var b1 = Stay(trullo, Person(orgId, "Anna", "Verdi"), BookingStatus.Confirmed, BookingSource.Direct, Day(10, 10), Day(10, 13), 300m, 11);
        var b2 = Stay(trullo, Person(orgId, "Mario", "Rossi"), BookingStatus.Confirmed, BookingSource.Airbnb, Day(10, 9), Day(10, 12), 500m, 12);
        var b3 = Stay(casaBianca, Person(orgId, "Luca", "Neri"), BookingStatus.CheckedIn, BookingSource.Manual, Day(10, 7), Day(10, 10), 400m, 13);
        var b4 = Stay(casaBianca, Person(orgId, "Sara", "bianchi"), BookingStatus.CheckedOut, BookingSource.BookingCom, Day(10, 1), Day(10, 4), 200m, 14);
        var b5 = Stay(trullo, Person(orgId, "Gino", "Gialli"), BookingStatus.Cancelled, BookingSource.Expedia, Day(10, 20), Day(10, 22), 250m, 15);
        var b6 = Stay(trullo, Person(orgId, "Elena", "Gatti"), BookingStatus.Pending, BookingSource.Direct, Day(10, 25), Day(10, 28), 600m, 16);
        b6.PaymentOption = PaymentOption.OnSite;
        b6.GuestEmailVerifiedAt = At(10, 9, 8);
        b6.RequestExpiresAt = At(10, 10, 8);
        var b7 = Stay(casaBianca, Person(orgId, "Paolo", "Zanetti"), BookingStatus.Pending, BookingSource.Direct, Day(10, 15), Day(10, 17), 350m, 17);
        b7.StripeSetupIntentId = "seti_test";
        b7.CreatedAt = At(10, 9, 9, 50);
        var b8 = Stay(trullo, Person(orgId, "Marta", "Ferrari"), BookingStatus.Confirmed, BookingSource.Local, Day(10, 20), Day(10, 25), 700m, 18);
        Stay(foreign, Person(otherOrgId, "Anna", "Verdi"), BookingStatus.Confirmed, BookingSource.Direct, Day(10, 10), Day(10, 13), 999m, 19);

        db.Payments.AddRange(
            Pay(b1, PaymentStatus.Completed, 300m),
            Pay(b2, PaymentStatus.Completed, 150m),
            Pay(b2, PaymentStatus.Failed, 350m),
            Pay(b4, PaymentStatus.Refunded, 200m, refunded: 200m),
            Pay(b6, PaymentStatus.Pending, 600m));
        db.AlloggiatiWebReports.Add(new AlloggiatiWebReport
        {
            BookingId = b3.Id,
            OrgId = orgId,
            GuestId = b3.GuestId,
            Status = AlloggiatiWebStatus.Inviato,
            ConfirmationNumber = "RICEVUTA-1",
            ReportedAt = At(10, 8, 7),
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var bookings = new Dictionary<string, Booking> { ["b1"] = b1, ["b2"] = b2, ["b3"] = b3, ["b4"] = b4, ["b5"] = b5, ["b6"] = b6, ["b7"] = b7, ["b8"] = b8 };
        return new Fixture(db, orgId, trullo, casaBianca, bookings, new HostScope(orgId), new HostScope(orgId, GrantedToUserId: Collaborator));
    }

    private static Payment Pay(Booking booking, PaymentStatus status, decimal amount, decimal refunded = 0m) => new()
    {
        OrgId = booking.OrgId,
        BookingId = booking.Id,
        Status = status,
        Amount = amount,
        RefundedAmount = refunded,
        ProcessedAt = At(10, 5, 10),
        CreatedAt = At(10, 5, 10),
        UpdatedAt = At(10, 5, 10),
    };

    private static async Task<List<string>> NamesAsync(Fixture world, BookingSearchCriteria criteria, HostScope? scope = null)
    {
        var page = await Search(world.Db).SearchAsync(scope ?? world.OrgWide, criteria with { PageSize = 100 });
        var names = world.B.ToDictionary(entry => entry.Value.Id, entry => entry.Key);
        return page.Items.Select(booking => names[booking.Id]).ToList();
    }

    // --- Arrival days ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Arrival_TheStaysThatArriveInTheRange_BothDaysIncluded()
    {
        var world = await SeedAsync();

        // Latest check-in first: b1 arrives on the 10th, b2 on the 9th; b3 arrived on the 7th, b7 arrives on the 15th.
        Assert.Equal(["b1", "b2"], await NamesAsync(world, new BookingSearchCriteria(ArrivalFrom: new DateOnly(2026, 10, 9), ArrivalTo: new DateOnly(2026, 10, 12))));
        Assert.Equal(["b1"], await NamesAsync(world, new BookingSearchCriteria(ArrivalFrom: new DateOnly(2026, 10, 10), ArrivalTo: new DateOnly(2026, 10, 10))));
        // b5 and b8 arrive the same day (the 20th): the id decides between them, and the ids are random.
        var fromTheTenth = await NamesAsync(world, new BookingSearchCriteria(ArrivalFrom: new DateOnly(2026, 10, 10)));
        Assert.Equal(["b6", "b7", "b1"], fromTheTenth.Where(name => name is not ("b5" or "b8")));
        Assert.Equal(["b5", "b8"], fromTheTenth.Skip(1).Take(2).Order());
        Assert.Equal(["b3", "b4"], await NamesAsync(world, new BookingSearchCriteria(ArrivalTo: new DateOnly(2026, 10, 8))));
    }

    [Fact]
    public async Task Arrival_TheLastDayTheCalendarHas_IsNoUpperBound_NotAnError()
    {
        var world = await SeedAsync();

        var everything = await NamesAsync(world, new BookingSearchCriteria());

        Assert.Equal(everything, await NamesAsync(world, new BookingSearchCriteria(ArrivalTo: DateOnly.MaxValue)));
        Assert.Equal(everything, await NamesAsync(world, new BookingSearchCriteria(To: DateOnly.MaxValue)));
        var counts = await Search(world.Db).CountAsync(world.OrgWide, new BookingSearchCriteria(ArrivalTo: DateOnly.MaxValue));
        Assert.Equal(everything.Count, counts.All);
    }

    [Fact]
    public async Task Arrival_IsNotTheOverlapOfFromAndTo_WhichKeepsTheStaysThatHaveADayInTheRange()
    {
        var world = await SeedAsync();

        // The 12th: b1 (10 to 13) and b2 (9 to 12) have a day in it; nobody arrives that day.
        Assert.Equal(["b1", "b2"], await NamesAsync(world, new BookingSearchCriteria(From: new DateOnly(2026, 10, 12), To: new DateOnly(2026, 10, 12))));
        Assert.Empty(await NamesAsync(world, new BookingSearchCriteria(ArrivalFrom: new DateOnly(2026, 10, 12), ArrivalTo: new DateOnly(2026, 10, 12))));
    }

    // --- The chips ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(BookingListView.All, "b6,b5,b8,b7,b1,b2,b3,b4")]
    [InlineData(BookingListView.ToConfirm, "b6")]
    [InlineData(BookingListView.Upcoming, "b8,b7,b1")]
    [InlineData(BookingListView.InProgress, "b2,b3")]
    [InlineData(BookingListView.Completed, "b4")]
    [InlineData(BookingListView.Cancelled, "b5")]
    public async Task View_EachChipIsItsBookings_LatestCheckInFirst(BookingListView view, string expected)
    {
        var world = await SeedAsync();

        var names = await NamesAsync(world, new BookingSearchCriteria(View: view));

        Assert.Equal(expected.Split(',').Order(), names.Order());
    }

    [Fact]
    public async Task View_TheChipsAreFiltersLikeAnyOther_ThatCombineWithTheRest()
    {
        var world = await SeedAsync();

        Assert.Equal(["b7"], await NamesAsync(world, new BookingSearchCriteria(View: BookingListView.Upcoming, Statuses: [BookingStatus.Pending])));
        Assert.Equal(["b1"], await NamesAsync(world, new BookingSearchCriteria(View: BookingListView.Upcoming, Query: "verdi")));
        Assert.Equal(["b2"], await NamesAsync(world, new BookingSearchCriteria(View: BookingListView.InProgress, Channels: [BookingChannel.Airbnb])));
        Assert.Equal(["b2", "b3"], (await NamesAsync(world, new BookingSearchCriteria(View: BookingListView.InProgress))).Order());
    }

    [Fact]
    public async Task View_TodayIsTheRomeDay_TheCheckInOfTodayIsInProgress_TheNextDayIsUpcoming()
    {
        var world = await SeedAsync();
        // 23:30 UTC of the 9th is half past one on the 10th in Rome: b1 (arrives the 10th) arrives today, b2 arrived yesterday.
        var late = new Casazen.Tests.Unit.FixedTimeProvider(new DateTimeOffset(2026, 10, 9, 23, 30, 0, TimeSpan.Zero));
        var service = new BookingSearchService(world.Db, new ConfigurationBuilder().Build(), late);

        var inProgress = await service.SearchAsync(world.OrgWide, new BookingSearchCriteria(View: BookingListView.InProgress));
        var upcoming = await service.SearchAsync(world.OrgWide, new BookingSearchCriteria(View: BookingListView.Upcoming));

        Assert.Contains(Id(world, "b1"), inProgress.Items.Select(b => b.Id));
        Assert.DoesNotContain(Id(world, "b1"), upcoming.Items.Select(b => b.Id));
        Assert.DoesNotContain(Id(world, "b2"), inProgress.Items.Select(b => b.Id));
    }

    [Fact]
    public async Task View_AnExpiredHoldIsNotUpcoming_AndARequestPastItsTermIsNotToConfirm()
    {
        var world = await SeedAsync();
        // A day later the hold of the 9th is long expired and the request (term: the 10th at 08:00Z) ran out.
        var later = new Casazen.Tests.Unit.FixedTimeProvider(new DateTimeOffset(2026, 10, 11, 10, 0, 0, TimeSpan.Zero));
        var service = new BookingSearchService(world.Db, new ConfigurationBuilder().Build(), later);

        var upcoming = (await service.SearchAsync(world.OrgWide, new BookingSearchCriteria(View: BookingListView.Upcoming))).Items.Select(b => b.Id);
        var toConfirm = (await service.SearchAsync(world.OrgWide, new BookingSearchCriteria(View: BookingListView.ToConfirm))).Items;

        Assert.DoesNotContain(Id(world, "b7"), upcoming);
        Assert.Empty(toConfirm);
    }

    // --- The counters of the chips -----------------------------------------------------------------------------

    [Fact]
    public async Task Counts_EachChipHasItsBookings_AndNoBookingIsCountedTwice()
    {
        var world = await SeedAsync();

        var counts = await Search(world.Db).CountAsync(world.OrgWide, new BookingSearchCriteria());

        Assert.Equal(new BookingCounts(All: 8, ToConfirm: 1, Upcoming: 3, InProgress: 2, Completed: 1, Cancelled: 1), counts);
        // The chips add up to the bookings, none being in two (here every booking is in one).
        Assert.Equal(counts.All, counts.ToConfirm + counts.Upcoming + counts.InProgress + counts.Completed + counts.Cancelled);
    }

    [Theory]
    [InlineData(BookingListView.ToConfirm)]
    [InlineData(BookingListView.Upcoming)]
    [InlineData(BookingListView.InProgress)]
    [InlineData(BookingListView.Completed)]
    [InlineData(BookingListView.Cancelled)]
    public async Task Counts_EachCounterIsTheTotalOfThatChipInTheList_WithTheSameFilters(BookingListView view)
    {
        var world = await SeedAsync();
        // Any filter but the chip: the counters and the list read the same predicates.
        var filters = new BookingSearchCriteria(From: new DateOnly(2026, 10, 1), To: new DateOnly(2026, 10, 31), Query: "a");

        var counts = await Search(world.Db).CountAsync(world.OrgWide, filters);
        var list = await Search(world.Db).SearchAsync(world.OrgWide, filters with { View = view, PageSize = 100 });

        var counter = view switch
        {
            BookingListView.ToConfirm => counts.ToConfirm,
            BookingListView.Upcoming => counts.Upcoming,
            BookingListView.InProgress => counts.InProgress,
            BookingListView.Completed => counts.Completed,
            _ => counts.Cancelled,
        };
        Assert.Equal(list.TotalCount, counter);
        Assert.Equal((await Search(world.Db).SearchAsync(world.OrgWide, filters with { PageSize = 100 })).TotalCount, counts.All);
    }

    [Fact]
    public async Task Counts_TheChipOfTheRequestIsLeftOut_ThePageAndTheOrderToo_ButNotTheOtherFilters()
    {
        var world = await SeedAsync();
        var service = Search(world.Db);

        var withAChip = await service.CountAsync(world.OrgWide, new BookingSearchCriteria(View: BookingListView.Cancelled, Page: 5, PageSize: 1, Sort: new BookingSort(BookingSortKey.Total)));
        var plain = await service.CountAsync(world.OrgWide, new BookingSearchCriteria());
        var byChannel = await service.CountAsync(world.OrgWide, new BookingSearchCriteria(Channels: [BookingChannel.Direct]));
        var byText = await service.CountAsync(world.OrgWide, new BookingSearchCriteria(Query: "verdi"));

        Assert.Equal(plain, withAChip);
        // b1, b6 and b7 came from the booking site.
        Assert.Equal(new BookingCounts(3, 1, 2, 0, 0, 0), byChannel);
        Assert.Equal(new BookingCounts(1, 0, 1, 0, 0, 0), byText);
    }

    [Fact]
    public async Task Counts_ACollaboratorLimitedToSomePropertiesCountsItsPropertyOnly_AndNothingIsZeroes()
    {
        var world = await SeedAsync();
        var service = Search(world.Db);

        var restricted = await service.CountAsync(world.Restricted, new BookingSearchCriteria());
        var nobody = await service.CountAsync(new HostScope(Guid.NewGuid()), new BookingSearchCriteria());

        // b1, b2, b5, b6, b8: the Trullo's.
        Assert.Equal(new BookingCounts(5, 1, 2, 1, 0, 1), restricted);
        Assert.Equal(new BookingCounts(0, 0, 0, 0, 0, 0), nobody);
    }

    // --- The channel -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(BookingChannel.Direct, "b1,b6,b7")]
    [InlineData(BookingChannel.Manual, "b3")]
    [InlineData(BookingChannel.Airbnb, "b2")]
    [InlineData(BookingChannel.BookingCom, "b4")]
    [InlineData(BookingChannel.Other, "b5,b8")]
    public async Task Channel_EachChannelIsItsSources(BookingChannel channel, string expected)
    {
        var world = await SeedAsync();

        var names = await NamesAsync(world, new BookingSearchCriteria(Channels: [channel]));

        Assert.Equal(expected.Split(',').Order(), names.Order());
    }

    [Fact]
    public async Task Channel_SeveralAtOnce_AreAnyOfThem_AndNoneMeansEveryChannel()
    {
        var world = await SeedAsync();

        Assert.Equal(
            new[] { "b1", "b3", "b6", "b7" }.Order(),
            (await NamesAsync(world, new BookingSearchCriteria(Channels: [BookingChannel.Direct, BookingChannel.Manual]))).Order());
        Assert.Equal(8, (await NamesAsync(world, new BookingSearchCriteria(Channels: []))).Count);
    }

    // --- The online check-in link ----------------------------------------------------------------------------

    private static GuestCheckInSession Session(
        Booking booking,
        GuestCheckInSessionStatus status,
        DateTime expiresAt,
        DateTime createdAt,
        GuestCheckInLinkEmailStatus? email = GuestCheckInLinkEmailStatus.Sent)
    {
        return new GuestCheckInSession
        {
            BookingId = booking.Id,
            OrgId = booking.OrgId,
            TokenHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            Status = status,
            ExpiresAt = expiresAt,
            LinkEmailStatus = email,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
    }

    [Fact]
    public async Task CheckInStatus_IsTheStateOfTheLinkAsTheHomeReadsIt_ForEveryKindOfHistory()
    {
        var world = await SeedAsync();
        var future = At(10, 15, 8);
        var past = At(10, 8, 8);
        // b4: a submitted link wins over a newer open one. b5: the newest link tells, it replaced an expired one. b6: open but past
        // its end, so expired before the job marks it so. b7: the email failed, the guest has nothing to open. b8: no link at all.
        world.Db.GuestCheckInSessions.AddRange(
            Session(world.B["b1"], GuestCheckInSessionStatus.Inviato, future, At(10, 6, 8)),
            Session(world.B["b2"], GuestCheckInSessionStatus.InCompilazione, future, At(10, 6, 8)),
            Session(world.B["b3"], GuestCheckInSessionStatus.Completo, future, At(10, 6, 8)),
            Session(world.B["b4"], GuestCheckInSessionStatus.Completo, future, At(10, 2, 8)),
            Session(world.B["b4"], GuestCheckInSessionStatus.Inviato, future, At(10, 5, 8)),
            Session(world.B["b5"], GuestCheckInSessionStatus.Scaduto, past, At(10, 1, 8)),
            Session(world.B["b5"], GuestCheckInSessionStatus.Inviato, future, At(10, 5, 8)),
            Session(world.B["b6"], GuestCheckInSessionStatus.Inviato, past, At(10, 6, 8)),
            Session(world.B["b7"], GuestCheckInSessionStatus.Inviato, future, At(10, 6, 8), GuestCheckInLinkEmailStatus.Failed));
        await world.Db.SaveChangesAsync();
        var sessions = (await world.Db.GuestCheckInSessions.ToListAsync()).ToLookup(s => s.BookingId);
        var expectedStates = world.B.ToDictionary(entry => entry.Key, entry => HostTodayService.StateOf(sessions[entry.Value.Id], Now));

        // The expectations by hand, so the test does not only repeat the rule it compares with.
        Assert.Equal(HostTodayCheckInState.Sent, expectedStates["b1"]);
        Assert.Equal(HostTodayCheckInState.InProgress, expectedStates["b2"]);
        Assert.Equal(HostTodayCheckInState.Completed, expectedStates["b3"]);
        Assert.Equal(HostTodayCheckInState.Completed, expectedStates["b4"]);
        Assert.Equal(HostTodayCheckInState.Sent, expectedStates["b5"]);
        Assert.Equal(HostTodayCheckInState.Expired, expectedStates["b6"]);
        Assert.Equal(HostTodayCheckInState.NotSent, expectedStates["b7"]);
        Assert.Equal(HostTodayCheckInState.NotSent, expectedStates["b8"]);

        foreach (var state in Enum.GetValues<HostTodayCheckInState>())
        {
            var names = await NamesAsync(world, new BookingSearchCriteria(CheckInStates: [state]));

            Assert.Equal(
                expectedStates.Where(entry => entry.Value == state).Select(entry => entry.Key).Order(),
                names.Order());
        }
    }

    [Fact]
    public async Task CheckInStatus_SeveralStatesAreAnyOfThem_AndWaitingForTheGuestIsSentOrInProgress()
    {
        var world = await SeedAsync();
        var future = At(10, 15, 8);
        world.Db.GuestCheckInSessions.AddRange(
            Session(world.B["b1"], GuestCheckInSessionStatus.Inviato, future, At(10, 6, 8)),
            Session(world.B["b2"], GuestCheckInSessionStatus.InCompilazione, future, At(10, 6, 8)),
            Session(world.B["b3"], GuestCheckInSessionStatus.Completo, future, At(10, 6, 8)));
        await world.Db.SaveChangesAsync();

        var waiting = await NamesAsync(world, new BookingSearchCriteria(CheckInStates: [HostTodayCheckInState.Sent, HostTodayCheckInState.InProgress]));
        var toSend = await NamesAsync(world, new BookingSearchCriteria(CheckInStates: [HostTodayCheckInState.NotSent, HostTodayCheckInState.Expired]));

        Assert.Equal(["b1", "b2"], waiting.Order());
        Assert.Equal(["b4", "b5", "b6", "b7", "b8"], toSend.Order());
    }

    [Fact]
    public async Task CheckInStatus_TheLinksOfAnotherOrgAreNotConsidered_NorAreTheirBookings()
    {
        var world = await SeedAsync();
        var foreignBooking = await world.Db.Bookings.IgnoreQueryFilters().FirstAsync(b => b.OrgId != world.OrgId);
        world.Db.GuestCheckInSessions.Add(Session(foreignBooking, GuestCheckInSessionStatus.Completo, At(10, 15, 8), At(10, 6, 8)));
        await world.Db.SaveChangesAsync();

        var completed = await NamesAsync(world, new BookingSearchCriteria(CheckInStates: [HostTodayCheckInState.Completed]));

        Assert.Empty(completed);
    }

    // --- The order ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Sort_WithoutOne_IsTheLatestCheckInFirst_AsTheListAlwaysWas()
    {
        var world = await SeedAsync();

        var plain = (await Search(world.Db).SearchAsync(world.OrgWide, new BookingSearchCriteria())).Items.Select(b => b.Id).ToList();
        var asked = (await Search(world.Db).SearchAsync(world.OrgWide, new BookingSearchCriteria(Sort: new BookingSort(BookingSortKey.CheckIn, Descending: true)))).Items.Select(b => b.Id).ToList();

        Assert.Equal(plain, asked);
        Assert.Equal(Id(world, "b6"), plain[0]);
        Assert.Equal(Id(world, "b4"), plain[^1]);
    }

    [Theory]
    [InlineData(BookingSortKey.CheckIn, false, "b4,b3,b2,b1,b7,b5|b8,b6")]
    [InlineData(BookingSortKey.CheckIn, true, "b6,b5|b8,b7,b1,b2,b3,b4")]
    [InlineData(BookingSortKey.CreatedAt, false, "b1,b2,b3,b4,b5,b6,b8,b7")]
    [InlineData(BookingSortKey.CreatedAt, true, "b7,b8,b6,b5,b4,b3,b2,b1")]
    [InlineData(BookingSortKey.Total, false, "b4,b5,b1,b7,b3,b2,b6,b8")]
    [InlineData(BookingSortKey.Total, true, "b8,b6,b2,b3,b7,b1,b5,b4")]
    [InlineData(BookingSortKey.Status, false, "b6|b7,b1|b2|b8,b3,b4,b5")]
    [InlineData(BookingSortKey.Status, true, "b5,b4,b3,b1|b2|b8,b6|b7")]
    [InlineData(BookingSortKey.Guest, false, "b4,b8,b6,b5,b3,b2,b1,b7")]
    [InlineData(BookingSortKey.Guest, true, "b7,b1,b2,b3,b5,b6,b8,b4")]
    public async Task Sort_EachKeyInBothDirections_TheIdBreakingTheTies(BookingSortKey key, bool descending, string expected)
    {
        var world = await SeedAsync();

        var names = await NamesAsync(world, new BookingSearchCriteria(Sort: new BookingSort(key, descending)));

        // "a|b" is two bookings that tie on the key: either order, the id decides (and the ids are random), the same one every time.
        var groups = expected.Split(',').Select(group => group.Split('|')).ToList();
        var position = 0;
        foreach (var group in groups)
        {
            Assert.Equal(group.Order(), names.Skip(position).Take(group.Length).Order());
            position += group.Length;
        }

        Assert.Equal(8, names.Count);
        // Twice the same: a total order.
        Assert.Equal(names, await NamesAsync(world, new BookingSearchCriteria(Sort: new BookingSort(key, descending))));
    }

    [Fact]
    public async Task Sort_TiesAreBrokenByTheBookingId_Ascending_WhateverTheDirection()
    {
        var world = await SeedAsync();
        var tied = new[] { "b5", "b8" }.Select(name => Id(world, name)).Order().ToList();

        var ascending = (await Search(world.Db).SearchAsync(world.OrgWide, new BookingSearchCriteria(Sort: new BookingSort(BookingSortKey.CheckIn)))).Items.Select(b => b.Id).Where(tied.Contains);
        var descending = (await Search(world.Db).SearchAsync(world.OrgWide, new BookingSearchCriteria(Sort: new BookingSort(BookingSortKey.CheckIn, true)))).Items.Select(b => b.Id).Where(tied.Contains);

        Assert.Equal(tied, ascending);
        Assert.Equal(tied, descending);
    }

    [Fact]
    public async Task Sort_PagesOfAnyOrderNeverRepeatNorSkipABooking()
    {
        var world = await SeedAsync();
        foreach (var key in Enum.GetValues<BookingSortKey>())
        {
            var all = await Search(world.Db).SearchAsync(world.OrgWide, new BookingSearchCriteria(PageSize: 100, Sort: new BookingSort(key, true)));
            var seen = new List<Guid>();
            for (var page = 1; page <= 4; page++)
            {
                var result = await Search(world.Db).SearchAsync(world.OrgWide, new BookingSearchCriteria(Page: page, PageSize: 3, Sort: new BookingSort(key, true)));
                Assert.Equal(8, result.TotalCount);
                seen.AddRange(result.Items.Select(b => b.Id));
            }

            Assert.Equal(all.Items.Select(b => b.Id), seen);
        }
    }

    // --- What each row says besides the booking ---------------------------------------------------------------------

    [Fact]
    public async Task Rows_HowMuchWasPaid_AndWhereTheAlloggiatiCommunicationStands()
    {
        var world = await SeedAsync();

        var page = await Search(world.Db).SearchAsync(world.OrgWide, new BookingSearchCriteria(PageSize: 100, IncludePaidAmount: true));

        BookingRowDetails Row(string name) => page.Details[Id(world, name)];
        Assert.Equal(8, page.Details.Count);
        Assert.Equal(new BookingRowDetails(BookingPaymentState.Paid, 300m, AlloggiatiWebStatus.DaInviare), Row("b1"));
        // 150 of 500 came in; the failed payment of 350 is nothing. It arrives today: to send on the portal.
        Assert.Equal(new BookingRowDetails(BookingPaymentState.Partial, 150m, AlloggiatiWebStatus.DaInviareManualmente), Row("b2"));
        Assert.Equal(new BookingRowDetails(BookingPaymentState.None, 0m, AlloggiatiWebStatus.Inviato), Row("b3"));
        // Refunded in full; it left on the 4th with nothing sent: still to send.
        Assert.Equal(new BookingRowDetails(BookingPaymentState.Refunded, 0m, AlloggiatiWebStatus.DaInviareManualmente), Row("b4"));
        // A cancelled booking, a request and a hold have no communication.
        Assert.Equal(new BookingRowDetails(BookingPaymentState.None, 0m, null), Row("b5"));
        Assert.Equal(new BookingRowDetails(BookingPaymentState.None, 0m, null), Row("b6"));
        Assert.Equal(new BookingRowDetails(BookingPaymentState.None, 0m, null), Row("b7"));
        Assert.Equal(new BookingRowDetails(BookingPaymentState.None, 0m, AlloggiatiWebStatus.DaInviare), Row("b8"));
    }

    [Fact]
    public async Task Rows_WithoutTheRightToReadPayments_TheStateIsThere_TheAmountIsNot()
    {
        var world = await SeedAsync();

        var page = await Search(world.Db).SearchAsync(world.OrgWide, new BookingSearchCriteria(PageSize: 100));

        Assert.All(page.Details.Values, details => Assert.Null(details.PaidAmount));
        Assert.Equal(BookingPaymentState.Paid, page.Details[Id(world, "b1")].PaymentState);
        Assert.Equal(BookingPaymentState.Partial, page.Details[Id(world, "b2")].PaymentState);
    }

    [Fact]
    public async Task Rows_ACollaboratorLimitedToSomePropertiesGetsTheRowsOfItsPropertyOnly()
    {
        var world = await SeedAsync();

        var page = await Search(world.Db).SearchAsync(world.Restricted, new BookingSearchCriteria(PageSize: 100));

        Assert.Equal(5, page.TotalCount);
        Assert.Equal(page.Items.Select(b => b.Id).Order(), page.Details.Keys.Order());
        Assert.All(page.Items, booking => Assert.Equal(world.Trullo.Id, booking.PropertyId));
    }

    [Fact]
    public async Task Rows_AnEmptyPage_HasNoDetails_AndAsksNothing()
    {
        var world = await SeedAsync();

        var page = await Search(world.Db).SearchAsync(world.OrgWide, new BookingSearchCriteria(Query: "zzzz-nessuno"));

        Assert.Empty(page.Items);
        Assert.Empty(page.Details);
    }

    [Fact]
    public async Task Scope_NothingOfAnotherOrg_UnderAnyOfTheNewFilters()
    {
        var world = await SeedAsync();

        var names = await world.Db.Bookings.IgnoreQueryFilters().Where(b => b.OrgId != world.OrgId).Select(b => b.Id).ToListAsync();
        foreach (var view in Enum.GetValues<BookingListView>())
        {
            var page = await Search(world.Db).SearchAsync(world.OrgWide, new BookingSearchCriteria(View: view, PageSize: 100));
            Assert.Empty(page.Items.Select(b => b.Id).Intersect(names));
        }

        var arriving = await Search(world.Db).SearchAsync(world.OrgWide, new BookingSearchCriteria(ArrivalFrom: new DateOnly(2026, 10, 10), ArrivalTo: new DateOnly(2026, 10, 10)));
        Assert.Empty(arriving.Items.Select(b => b.Id).Intersect(names));
    }
}
