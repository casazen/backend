using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Http;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Configuration;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// SR-03b on the booking list endpoints (<c>GET /api/bookings/search</c>, <c>/counts</c> and the plain list): what the request may
/// say (the chip, the channel, the state of the check-in link, the order, the arrival days), the 400 each word that is not understood
/// gets, the money a caller without <c>payment.read</c> never receives, and what a row says besides the booking.
/// </summary>
public class BookingsControllerListTests
{
    private const string OwnerId = "auth0|owner_123";
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly Guid OrgId = Guid.NewGuid();

    private readonly Mock<IBookingSearchService> _search = new();
    private readonly Mock<IPropertyService> _propertyService = new();

    private BookingsController CreateController()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:ApiBaseUrl"] = "https://api.test" })
            .Build();
        return new BookingsController(
            Mock.Of<IBookingService>(),
            Mock.Of<IAlloggiatiWebService>(),
            _propertyService.Object,
            Mock.Of<IPropertyAuthorizationService>(),
            ICalTestServices.PropertySync(db, Mock.Of<ISafeExternalHttpClient>(), configuration),
            Mock.Of<IOtaStayService>(),
            Mock.Of<ILogger<BookingsController>>(),
            Options.Create(new BookingsOptions { DefaultPageSize = 10 }))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = HostAuthorizationTestHarness.User(OwnerId),
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };
    }

    private static IOrgContextResolver OrgResolver(Guid? orgId = null)
    {
        var resolver = new Mock<IOrgContextResolver>();
        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(orgId);
        return resolver.Object;
    }

    private static IAuthorizationService Authorization(bool canReadPayments = true) =>
        HostAuthorizationTestHarness.Create(OrgId, (_, permission) => permission != "payment.read" || canReadPayments);

    private Task<ActionResult<PagedResultDto<BookingListItemDto>>> SearchAsync(
        BookingsController controller,
        DateOnly? arrivalFrom = null,
        DateOnly? arrivalTo = null,
        string? view = null,
        string[]? channel = null,
        string[]? checkInStatus = null,
        string? sort = null,
        bool canReadPayments = true,
        Guid? orgId = null,
        Guid? propertyId = null,
        int page = 1,
        int? pageSize = null) =>
        controller.Search(
            _search.Object,
            OrgResolver(orgId ?? OrgId),
            Authorization(canReadPayments),
            HostAuthorizationTestHarness.ScopeResolver(),
            propertyId: propertyId,
            page: page,
            pageSize: pageSize,
            arrivalFrom: arrivalFrom,
            arrivalTo: arrivalTo,
            view: view,
            channel: channel,
            checkInStatus: checkInStatus,
            sort: sort);

    private Task<ActionResult<BookingCountsDto>> CountsAsync(
        BookingsController controller,
        string[]? status = null,
        string? q = null,
        Guid? propertyId = null,
        DateOnly? arrivalFrom = null,
        DateOnly? arrivalTo = null,
        string[]? channel = null,
        string[]? checkInStatus = null,
        Guid? orgId = null) =>
        controller.Counts(
            _search.Object,
            OrgResolver(orgId ?? OrgId),
            Authorization(),
            HostAuthorizationTestHarness.ScopeResolver(),
            status: status,
            q: q,
            propertyId: propertyId,
            arrivalFrom: arrivalFrom,
            arrivalTo: arrivalTo,
            channel: channel,
            checkInStatus: checkInStatus);

    private static Booking MakeBooking(BookingSource source = BookingSource.Direct, BookingStatus status = BookingStatus.Confirmed) => new()
    {
        Id = Guid.NewGuid(),
        OrgId = OrgId,
        PropertyId = PropertyId,
        Property = new Property { Id = PropertyId, OrgId = OrgId, OwnerId = OwnerId, Name = "Trullo" },
        Guest = new Guest { FirstName = "Anna", LastName = "Verdi", Email = "anna@example.com" },
        CheckInDate = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc),
        CheckOutDate = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc),
        NumberOfGuests = 2,
        Status = status,
        Source = source,
        TotalPrice = 300m,
        BookingCode = "ABCDEFGHJK",
    };

    private void SearchAnswers(BookingSearchPage page) => _search
        .Setup(s => s.SearchAsync(It.IsAny<HostScope>(), It.IsAny<BookingSearchCriteria>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(page);

    private void NothingWasSearched() => _search.Verify(
        s => s.SearchAsync(It.IsAny<HostScope>(), It.IsAny<BookingSearchCriteria>(), It.IsAny<CancellationToken>()), Times.Never);

    // --- The words of the request ---------------------------------------------------------------------------

    [Theory]
    [InlineData("view", "done", BookingsController.InvalidViewCode)]
    [InlineData("view", "1", BookingsController.InvalidViewCode)]
    [InlineData("view", "upcoming,completed", BookingsController.InvalidViewCode)]
    [InlineData("channel", "Expedia", BookingsController.InvalidChannelCode)]
    [InlineData("channel", "Airbnb,Foo", BookingsController.InvalidChannelCode)]
    [InlineData("checkInStatus", "Done", BookingsController.InvalidCheckInStatusCode)]
    [InlineData("checkInStatus", "3", BookingsController.InvalidCheckInStatusCode)]
    [InlineData("sort", "price", BookingsController.InvalidSortCode)]
    [InlineData("sort", "checkIn:up", BookingsController.InvalidSortCode)]
    public async Task Search_AWordTheListDoesNotKnow_Is400WithItsOwnCode_AndSearchesNothing(string parameter, string value, string code)
    {
        var result = await SearchAsync(
            CreateController(),
            view: parameter == "view" ? value : null,
            channel: parameter == "channel" ? [value] : null,
            checkInStatus: parameter == "checkInStatus" ? [value] : null,
            sort: parameter == "sort" ? value : null);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Equal(code, Assert.IsAssignableFrom<ProblemDetails>(problem.Value).Extensions["code"]);
        NothingWasSearched();
    }

    [Fact]
    public async Task Search_ArrivalFromAfterArrivalTo_Is400WithTheCodeOfEveryRange()
    {
        var result = await SearchAsync(CreateController(), arrivalFrom: new DateOnly(2026, 11, 1), arrivalTo: new DateOnly(2026, 10, 1));

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Equal(BookingsController.InvalidRangeCode, Assert.IsAssignableFrom<ProblemDetails>(problem.Value).Extensions["code"]);
        NothingWasSearched();
    }

    [Fact]
    public async Task Search_TheNewFiltersArePassedToTheSearch_TheChipsInAnyCase_TheListsRepeatedOrWithCommas()
    {
        SearchAnswers(new BookingSearchPage([], 0, 2, 25));

        await SearchAsync(
            CreateController(),
            arrivalFrom: new DateOnly(2026, 10, 9),
            arrivalTo: new DateOnly(2026, 10, 16),
            view: "UpComing",
            channel: ["airbnb", "Direct,bookingcom"],
            checkInStatus: ["notsent", "EXPIRED"],
            sort: "total:desc",
            page: 2,
            pageSize: 25);

        _search.Verify(
            s => s.SearchAsync(
                It.Is<HostScope>(scope => scope.OrgId == OrgId),
                It.Is<BookingSearchCriteria>(c =>
                    c.ArrivalFrom == new DateOnly(2026, 10, 9)
                    && c.ArrivalTo == new DateOnly(2026, 10, 16)
                    && c.View == BookingListView.Upcoming
                    && c.Channels != null
                    && c.Channels.SequenceEqual(new[] { BookingChannel.Airbnb, BookingChannel.Direct, BookingChannel.BookingCom })
                    && c.CheckInStates != null
                    && c.CheckInStates.SequenceEqual(new[] { HostTodayCheckInState.NotSent, HostTodayCheckInState.Expired })
                    && c.Sort == new BookingSort(BookingSortKey.Total, true)
                    && c.Page == 2
                    && c.PageSize == 25),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Search_WithoutTheNewParameters_AsksForNoneOfThem_SoItIsTheSearchOfBefore()
    {
        SearchAnswers(new BookingSearchPage([], 0, 1, 10));

        await SearchAsync(CreateController());

        _search.Verify(
            s => s.SearchAsync(
                It.IsAny<HostScope>(),
                It.Is<BookingSearchCriteria>(c =>
                    c.ArrivalFrom == null
                    && c.ArrivalTo == null
                    && c.View == BookingListView.All
                    && c.Channels != null
                    && c.Channels.Count == 0
                    && c.CheckInStates != null
                    && c.CheckInStates.Count == 0
                    && c.Sort == null),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // --- The money --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Search_ThePaidAmountIsAskedForOnlyForACallerWhoMayReadPayments(bool canReadPayments)
    {
        SearchAnswers(new BookingSearchPage([], 0, 1, 10));

        await SearchAsync(CreateController(), canReadPayments: canReadPayments);

        _search.Verify(
            s => s.SearchAsync(
                It.IsAny<HostScope>(), It.Is<BookingSearchCriteria>(c => c.IncludePaidAmount == canReadPayments), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetAll_ThePlainListAsksForThePaidAmountOnlyForACallerWhoMayReadPayments(bool canReadPayments)
    {
        SearchAnswers(new BookingSearchPage([], 0, 1, 10));

        await CreateController().GetAll(
            _search.Object, OrgResolver(OrgId), Authorization(canReadPayments), HostAuthorizationTestHarness.ScopeResolver());

        _search.Verify(
            s => s.SearchAsync(
                It.IsAny<HostScope>(), It.Is<BookingSearchCriteria>(c => c.IncludePaidAmount == canReadPayments), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // --- What a row says ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Search_EachRowSaysItsChannel_HowMuchWasPaid_WhereItsAlloggiatiStands_AndNothingMoreThanTheyAreTold()
    {
        var airbnb = MakeBooking(BookingSource.Airbnb);
        var expedia = MakeBooking(BookingSource.Expedia);
        var withoutDetails = MakeBooking(BookingSource.Manual);
        SearchAnswers(new BookingSearchPage([airbnb, expedia, withoutDetails], 3, 1, 10)
        {
            Details = new Dictionary<Guid, BookingRowDetails>
            {
                [airbnb.Id] = new(BookingPaymentState.Partial, 120m, AlloggiatiWebStatus.DaInviareManualmente),
                [expedia.Id] = new(BookingPaymentState.Paid, null, null),
            },
        });

        var page = Assert.IsType<PagedResultDto<BookingListItemDto>>(
            Assert.IsType<OkObjectResult>((await SearchAsync(CreateController())).Result).Value);

        var rows = page.Items.ToList();
        Assert.Equal(("Airbnb", "Partial", 120m, "DaInviareManualmente"), (rows[0].Channel, rows[0].PaymentState, rows[0].PaidAmount, rows[0].AlloggiatiStatus));
        // The exact source stays; the channel is the group the filter speaks of.
        Assert.Equal(("Expedia", "Other", "Paid", (decimal?)null, (string?)null), (rows[1].Source, rows[1].Channel, rows[1].PaymentState, rows[1].PaidAmount, rows[1].AlloggiatiStatus));
        Assert.Equal(("Manual", "None", (decimal?)null, (string?)null), (rows[2].Channel, rows[2].PaymentState, rows[2].PaidAmount, rows[2].AlloggiatiStatus));
        // Still the booking, as everywhere.
        Assert.Equal("ABCDE-FGHJK", rows[0].BookingCode);
        Assert.Equal("Trullo", rows[0].PropertyName);
        Assert.Equal(300m, rows[0].TotalPrice);
    }

    [Fact]
    public void BookingMapper_RespondBy_IsTheTermOfARequestOnlyOnceTheGuestConfirmedTheEmail()
    {
        var now = new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc);
        var forTheHost = MakeBooking(status: BookingStatus.Pending);
        forTheHost.PaymentOption = PaymentOption.OnSite;
        forTheHost.GuestEmailVerifiedAt = now.AddHours(-2);
        forTheHost.RequestExpiresAt = now.AddHours(20);
        var waitingForTheGuest = MakeBooking(status: BookingStatus.Pending);
        waitingForTheGuest.PaymentOption = PaymentOption.OnSite;
        waitingForTheGuest.RequestExpiresAt = now.AddMinutes(25);
        var expired = MakeBooking(status: BookingStatus.Pending);
        expired.PaymentOption = PaymentOption.OnSite;
        expired.GuestEmailVerifiedAt = now.AddHours(-30);
        expired.RequestExpiresAt = now.AddHours(-1);
        var confirmed = MakeBooking();

        Assert.Equal(now.AddHours(20), BookingMapper.ToListItem(forTheHost, null, now).RespondBy);
        Assert.Null(BookingMapper.ToListItem(waitingForTheGuest, null, now).RespondBy);
        Assert.Null(BookingMapper.ToListItem(expired, null, now).RespondBy);
        Assert.Null(BookingMapper.ToListItem(confirmed, null, now).RespondBy);
        // The state of the request itself keeps saying who has to answer.
        Assert.Equal("AwaitingHostApproval", BookingMapper.ToListItem(forTheHost, null, now).OnSiteRequestState);
        Assert.Equal("AwaitingGuestEmail", BookingMapper.ToListItem(waitingForTheGuest, null, now).OnSiteRequestState);
    }

    // --- The counters of the chips --------------------------------------------------------------------------------

    [Fact]
    public async Task Counts_AnswersTheCountersOfTheChips_FilteredByTheScopeOfTheCaller()
    {
        _search
            .Setup(s => s.CountAsync(It.IsAny<HostScope>(), It.IsAny<BookingSearchCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingCounts(All: 12, ToConfirm: 2, Upcoming: 5, InProgress: 1, Completed: 3, Cancelled: 1));

        var result = await CountsAsync(
            CreateController(),
            status: ["Confirmed,Pending"],
            q: "verdi",
            arrivalFrom: new DateOnly(2026, 10, 9),
            arrivalTo: new DateOnly(2026, 10, 16),
            channel: ["airbnb"],
            checkInStatus: ["sent"]);

        var counts = Assert.IsType<BookingCountsDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal((12, 2, 5, 1, 3, 1), (counts.All, counts.ToConfirm, counts.Upcoming, counts.InProgress, counts.Completed, counts.Cancelled));
        _search.Verify(
            s => s.CountAsync(
                It.Is<HostScope>(scope => scope.OrgId == OrgId && scope.OwnerId == OwnerId),
                It.Is<BookingSearchCriteria>(c =>
                    c.Statuses != null
                    && c.Statuses.SequenceEqual(new[] { BookingStatus.Confirmed, BookingStatus.Pending })
                    && c.Query == "verdi"
                    && c.ArrivalFrom == new DateOnly(2026, 10, 9)
                    && c.ArrivalTo == new DateOnly(2026, 10, 16)
                    && c.Channels != null
                    && c.Channels.SequenceEqual(new[] { BookingChannel.Airbnb })
                    && c.CheckInStates != null
                    && c.CheckInStates.SequenceEqual(new[] { HostTodayCheckInState.Sent })
                    // The chip, the order and the page are the list's, not the counters'.
                    && c.View == BookingListView.All
                    && c.Sort == null),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Counts_TheSameWordsAreRefusedAsInTheSearch()
    {
        var invalidStatus = await CountsAsync(CreateController(), status: ["Done"]);
        var invalidChannel = await CountsAsync(CreateController(), channel: ["Expedia"]);
        var invalidCheckIn = await CountsAsync(CreateController(), checkInStatus: ["Done"]);
        var invalidRange = await CountsAsync(CreateController(), arrivalFrom: new DateOnly(2026, 11, 1), arrivalTo: new DateOnly(2026, 10, 1));

        Assert.Equal(
            [BookingsController.InvalidStatusCode, BookingsController.InvalidChannelCode, BookingsController.InvalidCheckInStatusCode, BookingsController.InvalidRangeCode],
            new[] { invalidStatus, invalidChannel, invalidCheckIn, invalidRange }
                .Select(r => Assert.IsAssignableFrom<ProblemDetails>(Assert.IsType<ObjectResult>(r.Result).Value).Extensions["code"]));
        _search.Verify(s => s.CountAsync(It.IsAny<HostScope>(), It.IsAny<BookingSearchCriteria>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Counts_ACallerWithoutAnOrg_GetsZeroes_NeverAnotherOrgsBookings()
    {
        var noOrg = new Mock<IOrgContextResolver>();
        noOrg.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);

        var result = await CreateController().Counts(
            _search.Object, noOrg.Object, Authorization(), HostAuthorizationTestHarness.ScopeResolver());

        var counts = Assert.IsType<BookingCountsDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal((0, 0, 0, 0, 0, 0), (counts.All, counts.ToConfirm, counts.Upcoming, counts.InProgress, counts.Completed, counts.Cancelled));
        _search.Verify(s => s.CountAsync(It.IsAny<HostScope>(), It.IsAny<BookingSearchCriteria>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Counts_APropertyOfAnotherOrgIs404_APropertyTheCallerMayNotReadIs403_LikeTheSearch()
    {
        _propertyService.Setup(s => s.GetPropertyRecordAsync(PropertyId)).ReturnsAsync((Property?)null);
        var notFound = await CountsAsync(CreateController(), propertyId: PropertyId);

        _propertyService
            .Setup(s => s.GetPropertyRecordAsync(PropertyId))
            .ReturnsAsync(new Property { Id = PropertyId, OrgId = OrgId, OwnerId = "auth0|other_member", Name = "Casa Bianca" });
        var forbidden = await CountsAsync(CreateController(), propertyId: PropertyId);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ObjectResult>(notFound.Result).StatusCode);
        Assert.IsType<ForbidResult>(forbidden.Result);
        _search.Verify(s => s.CountAsync(It.IsAny<HostScope>(), It.IsAny<BookingSearchCriteria>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
