using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Configuration;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
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
/// SR-03: <c>GET /api/bookings/search</c>, the booking list that holds up with many bookings, and the booking code in the
/// booking DTO. The query itself is <c>BookingSearchServiceTests</c>; here what the endpoint decides: the words of the status,
/// the range, the property the caller may read, the page it answers.
/// </summary>
public class BookingsControllerSearchTests
{
    private const string OwnerId = "auth0|owner_123";
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly Guid OrgId = Guid.NewGuid();

    private readonly Mock<IBookingSearchService> _search = new();
    private readonly Mock<IPropertyService> _propertyService = new();

    private BookingsController CreateController(int defaultPageSize = 10)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:ApiBaseUrl"] = "https://api.test" })
            .Build();
        var controller = new BookingsController(
            Mock.Of<IBookingService>(),
            Mock.Of<IAlloggiatiWebService>(),
            _propertyService.Object,
            Mock.Of<IPropertyAuthorizationService>(),
            ICalTestServices.PropertySync(db, Mock.Of<ISafeExternalHttpClient>(), configuration),
            Mock.Of<IOtaStayService>(),
            Mock.Of<ILogger<BookingsController>>(),
            Options.Create(new BookingsOptions { DefaultPageSize = defaultPageSize }))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = HostAuthorizationTestHarness.User(OwnerId),
                    // ApiProblem (FD-05 contract) localizes the detail through SharedResources.
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };
        return controller;
    }

    private static IOrgContextResolver OrgResolver(Guid? orgId = null)
    {
        var resolver = new Mock<IOrgContextResolver>();
        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(orgId ?? OrgId);
        return resolver.Object;
    }

    private Task<ActionResult<PagedResultDto<BookingResponseDto>>> CallAsync(
        BookingsController controller,
        DateOnly? from = null,
        DateOnly? to = null,
        string[]? status = null,
        string? q = null,
        Guid? propertyId = null,
        Guid? guestId = null,
        int page = 1,
        int? pageSize = null,
        IOrgContextResolver? orgResolver = null) =>
        controller.Search(
            _search.Object,
            orgResolver ?? OrgResolver(),
            HostAuthorizationTestHarness.Create(OrgId),
            HostAuthorizationTestHarness.ScopeResolver(),
            from,
            to,
            status,
            q,
            propertyId,
            guestId,
            page,
            pageSize);

    private static Booking MakeBooking() => new()
    {
        Id = Guid.NewGuid(),
        OrgId = OrgId,
        PropertyId = PropertyId,
        Property = new Property { Id = PropertyId, OrgId = OrgId, OwnerId = OwnerId, Name = "Trullo" },
        Guest = new Guest { FirstName = "Anna", LastName = "Verdi", Email = "anna@example.com" },
        CheckInDate = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc),
        CheckOutDate = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc),
        NumberOfGuests = 2,
        Status = BookingStatus.Confirmed,
        Source = BookingSource.Direct,
        BookingCode = "ABCDEFGHJK",
    };

    // --- What the request says --------------------------------------------------------------------------

    [Theory]
    [InlineData("Done")]
    [InlineData("7")]
    [InlineData("1")]
    [InlineData("Confirmed,Foo")]
    [InlineData("confirmed pending")]
    public async Task Search_AStatusThatIsNotTheNameOfOne_Returns400WithAStableCode_AndSearchesNothing(string status)
    {
        var result = await CallAsync(CreateController(), status: [status]);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Equal(BookingsController.InvalidStatusCode, Assert.IsAssignableFrom<ProblemDetails>(problem.Value).Extensions["code"]);
        _search.Verify(s => s.SearchAsync(It.IsAny<HostScope>(), It.IsAny<BookingSearchCriteria>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Search_FromAfterTo_Returns400WithAStableCode_AndSearchesNothing()
    {
        var result = await CallAsync(CreateController(), from: new DateOnly(2026, 11, 1), to: new DateOnly(2026, 10, 1));

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Equal(BookingsController.InvalidRangeCode, Assert.IsAssignableFrom<ProblemDetails>(problem.Value).Extensions["code"]);
        _search.Verify(s => s.SearchAsync(It.IsAny<HostScope>(), It.IsAny<BookingSearchCriteria>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Search_TheSameDayAsFromAndTo_IsAValidRange()
    {
        _search
            .Setup(s => s.SearchAsync(It.IsAny<HostScope>(), It.IsAny<BookingSearchCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingSearchPage([], 0, 1, 20));

        var result = await CallAsync(CreateController(), from: new DateOnly(2026, 10, 9), to: new DateOnly(2026, 10, 9));

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Theory]
    [InlineData(new[] { "Confirmed" }, new[] { BookingStatus.Confirmed })]
    [InlineData(new[] { "confirmed", "PENDING" }, new[] { BookingStatus.Confirmed, BookingStatus.Pending })]
    [InlineData(new[] { "Confirmed,Pending" }, new[] { BookingStatus.Confirmed, BookingStatus.Pending })]
    [InlineData(new[] { " CheckedIn , CheckedOut ", "CheckedIn" }, new[] { BookingStatus.CheckedIn, BookingStatus.CheckedOut })]
    [InlineData(new[] { "" }, new BookingStatus[0])]
    [InlineData(new string[0], new BookingStatus[0])]
    public void TryParseStatuses_ByNameWhateverTheCase_RepeatedOrSeparatedByCommas(string[] values, BookingStatus[] expected)
    {
        Assert.True(BookingsController.TryParseStatuses(values, out var statuses));

        Assert.Equal(expected, statuses);
    }

    [Fact]
    public void TryParseStatuses_NothingAtAll_IsNoFilter()
    {
        Assert.True(BookingsController.TryParseStatuses(null, out var statuses));
        Assert.Empty(statuses);
    }

    // --- What it answers --------------------------------------------------------------------------------

    [Fact]
    public async Task Search_PassesTheCriteriaAndTheScopeOfTheCaller_AndAnswersThePageWithTheCodeOfEachBooking()
    {
        var booking = MakeBooking();
        _search
            .Setup(s => s.SearchAsync(It.IsAny<HostScope>(), It.IsAny<BookingSearchCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingSearchPage([booking], 41, 3, 10));
        var guestId = Guid.NewGuid();

        var result = await CallAsync(
            CreateController(),
            from: new DateOnly(2026, 10, 1),
            to: new DateOnly(2026, 10, 31),
            status: ["Confirmed,Pending"],
            q: "verdi",
            guestId: guestId,
            page: 3,
            pageSize: 10);

        var page = Assert.IsType<PagedResultDto<BookingResponseDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal((41, 3, 10), (page.TotalCount, page.Page, page.PageSize));
        var dto = Assert.Single(page.Items);
        Assert.Equal(booking.Id, dto.Id);
        // The code the guest knows, as shown to people.
        Assert.Equal("ABCDE-FGHJK", dto.BookingCode);
        Assert.Equal("Trullo", dto.PropertyName);
        Assert.Equal("anna@example.com", dto.Guest.Email);
        _search.Verify(
            s => s.SearchAsync(
                It.Is<HostScope>(scope => scope.OrgId == OrgId && scope.OwnerId == OwnerId),
                It.Is<BookingSearchCriteria>(c =>
                    c.From == new DateOnly(2026, 10, 1)
                    && c.To == new DateOnly(2026, 10, 31)
                    && c.Statuses != null && c.Statuses.SequenceEqual(new[] { BookingStatus.Confirmed, BookingStatus.Pending })
                    && c.Query == "verdi"
                    && c.PropertyId == null
                    && c.GuestId == guestId
                    && c.Page == 3
                    && c.PageSize == 10),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Search_ACallerWithoutAnOrg_GetsAnEmptyPage_NeverAnotherOrgsBookings()
    {
        var resolver = new Mock<IOrgContextResolver>();
        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);

        var result = await CallAsync(CreateController(), page: 2, pageSize: 5, orgResolver: resolver.Object);

        var page = Assert.IsType<PagedResultDto<BookingResponseDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Empty(page.Items);
        Assert.Equal((0, 2, 5), (page.TotalCount, page.Page, page.PageSize));
        _search.Verify(s => s.SearchAsync(It.IsAny<HostScope>(), It.IsAny<BookingSearchCriteria>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Search_WithoutAPageSize_TakesTheOneOfThePlainList_AndASizeOfTheCallerWins()
    {
        _search
            .Setup(s => s.SearchAsync(It.IsAny<HostScope>(), It.IsAny<BookingSearchCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HostScope _, BookingSearchCriteria criteria, CancellationToken _) => new BookingSearchPage([], 0, criteria.Page, criteria.PageSize));
        var noOrg = new Mock<IOrgContextResolver>();
        noOrg.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);

        var byDefault = await CallAsync(CreateController(defaultPageSize: 7));
        var chosen = await CallAsync(CreateController(defaultPageSize: 7), pageSize: 25);
        var withoutAnOrg = await CallAsync(CreateController(defaultPageSize: 7), orgResolver: noOrg.Object);

        Assert.Equal(7, PageOf(byDefault).PageSize);
        Assert.Equal(25, PageOf(chosen).PageSize);
        Assert.Equal(7, PageOf(withoutAnOrg).PageSize);
        _search.Verify(
            s => s.SearchAsync(It.IsAny<HostScope>(), It.Is<BookingSearchCriteria>(c => c.PageSize == 7), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static PagedResultDto<BookingResponseDto> PageOf(ActionResult<PagedResultDto<BookingResponseDto>> result) =>
        Assert.IsType<PagedResultDto<BookingResponseDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);

    // --- The property the caller asks about ---------------------------------------------------------------

    [Fact]
    public async Task Search_APropertyOfAnotherOrg_Is404_AndSearchesNothing()
    {
        _propertyService.Setup(s => s.GetPropertyRecordAsync(PropertyId)).ReturnsAsync((Property?)null);

        var result = await CallAsync(CreateController(), propertyId: PropertyId);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        _search.Verify(s => s.SearchAsync(It.IsAny<HostScope>(), It.IsAny<BookingSearchCriteria>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Search_APropertyTheCallerMayNotRead_Is403_AndSearchesNothing()
    {
        _propertyService
            .Setup(s => s.GetPropertyRecordAsync(PropertyId))
            .ReturnsAsync(new Property { Id = PropertyId, OrgId = OrgId, OwnerId = "auth0|other_member", Name = "Casa Bianca" });

        var result = await CallAsync(CreateController(), propertyId: PropertyId);

        Assert.IsType<ForbidResult>(result.Result);
        _search.Verify(s => s.SearchAsync(It.IsAny<HostScope>(), It.IsAny<BookingSearchCriteria>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Search_APropertyTheCallerReads_IsPassedToTheSearch()
    {
        _propertyService
            .Setup(s => s.GetPropertyRecordAsync(PropertyId))
            .ReturnsAsync(new Property { Id = PropertyId, OrgId = OrgId, OwnerId = OwnerId, Name = "Trullo" });
        _search
            .Setup(s => s.SearchAsync(It.IsAny<HostScope>(), It.IsAny<BookingSearchCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingSearchPage([], 0, 1, 20));

        var result = await CallAsync(CreateController(), propertyId: PropertyId);

        Assert.IsType<OkObjectResult>(result.Result);
        _search.Verify(
            s => s.SearchAsync(It.IsAny<HostScope>(), It.Is<BookingSearchCriteria>(c => c.PropertyId == PropertyId), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // --- The booking code in every booking answer -----------------------------------------------------

    [Fact]
    public void BookingMapper_TheBookingCodeIsShownAsThePeopleReadIt()
    {
        var dto = BookingMapper.ToResponse(MakeBooking());

        Assert.Equal("ABCDE-FGHJK", dto.BookingCode);
    }
}
