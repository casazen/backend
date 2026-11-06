using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// NF-99, E07/F03/US03: what the endpoints of the document archive ask before anything is read (the words of the request, the org and the scope
/// of the caller, the types it may read), what they pass to the service, and what they write in the log.
/// </summary>
public class DocumentArchiveControllerTests
{
    private const string UserId = "auth0|host";

    private sealed class Harness
    {
        public Harness(bool hasOrg = true)
        {
            var orgContext = new Mock<IOrgContextResolver>();
            orgContext.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(hasOrg ? OrgId : null);
            var scopes = new Mock<IHostScopeResolver>();
            scopes.Setup(s => s.ResolveAsync(UserId, It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>())).ReturnsAsync(Scope);
            var authorization = new Mock<IContextAuthorizationService>();
            authorization
                .Setup(a => a.HasPermissionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var http = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", UserId)], "test")),
                RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
            };
            Http = http;
            Controller = new DocumentArchiveController(
                Archive.Object,
                [
                    new StubArchiveSource(ArchiveDocumentType.PropertyDocument, "short-rent|long-rent", "property.read"),
                    new StubArchiveSource(ArchiveDocumentType.TouristTax, "short-rent", "payment.read"),
                    new StubArchiveSource(ArchiveDocumentType.AlloggiatiReceipt, "short-rent", "guest.read"),
                ],
                authorization.Object,
                orgContext.Object,
                scopes.Object)
            {
                ControllerContext = new ControllerContext { HttpContext = http },
            };
        }

        public static Guid OrgId { get; } = Guid.NewGuid();

        public static HostScope Scope { get; } = new(OrgId);

        public Mock<IDocumentArchiveService> Archive { get; } = new();

        public HttpContext Http { get; }

        public DocumentArchiveController Controller { get; }
    }

    private static string CodeOf(IActionResult result)
    {
        var problem = Assert.IsAssignableFrom<ProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        return (string)problem.Extensions["code"]!;
    }

    private static int StatusOf(IActionResult result) => Assert.IsType<ObjectResult>(result).StatusCode!.Value;

    private static Task<IActionResult> ListAsync(
        Harness harness,
        string[]? types = null,
        DateOnly? from = null,
        DateOnly? to = null,
        string? search = null,
        int page = 1,
        int pageSize = 25) =>
        harness.Controller.List(types, from, to, null, null, search, page, pageSize);

    // ---------------------------------------------------------------- the words of a request

    [Theory]
    [InlineData("propertyDoc")]
    [InlineData("7")]
    [InlineData("")]
    [InlineData("  ")]
    public async Task List_ATypeThatIsNotAKey_Is400_AndNothingIsRead(string type)
    {
        var harness = new Harness();

        var result = await ListAsync(harness, types: ["propertyDocument", type]);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Equal(DocumentArchiveController.InvalidTypeCode, CodeOf(result));
        harness.Archive.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task List_DaysOutOfOrderOrOutOfTheSupportedYears_Are400()
    {
        var harness = new Harness();

        var backwards = await ListAsync(harness, from: new DateOnly(2026, 5, 2), to: new DateOnly(2026, 5, 1));
        var tooEarly = await ListAsync(harness, from: new DateOnly(1999, 12, 31));
        var tooLate = await ListAsync(harness, to: new DateOnly(2101, 1, 1));

        Assert.All(new[] { backwards, tooEarly, tooLate }, result =>
        {
            Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
            Assert.Equal(DocumentArchiveController.InvalidPeriodCode, CodeOf(result));
        });
        harness.Archive.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(0, 25)]
    [InlineData(-1, 25)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task List_APageBelowOneOrAPageSizeOutOfRange_Is400(int page, int pageSize)
    {
        var harness = new Harness();

        var result = await ListAsync(harness, page: page, pageSize: pageSize);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Equal(DocumentArchiveController.InvalidPageCode, CodeOf(result));
        harness.Archive.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task List_ACallerWithNoOrg_Is403_AndNothingIsRead()
    {
        var harness = new Harness(hasOrg: false);

        var result = await ListAsync(harness);

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(result));
        harness.Archive.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task List_PassesTheFiltersTheScopeAndTheTypesTheCallerMayRead_ToTheService()
    {
        var harness = new Harness();
        DocumentArchiveAccess? access = null;
        DocumentArchiveQuery? query = null;
        harness.Archive
            .Setup(a => a.ListAsync(Harness.Scope, It.IsAny<DocumentArchiveAccess>(), It.IsAny<DocumentArchiveQuery>(), It.IsAny<CancellationToken>()))
            .Callback<HostScope, DocumentArchiveAccess, DocumentArchiveQuery, CancellationToken>((_, granted, asked, _) =>
            {
                access = granted;
                query = asked;
            })
            .ReturnsAsync(new DocumentArchivePage(
                [new ArchiveRow { Type = ArchiveDocumentType.TouristTax, Id = Guid.NewGuid(), Title = "Ostuni", DocumentDate = new DateOnly(2026, 8, 15) }],
                41,
                2,
                10));

        var result = await ListAsync(
            harness,
            types: ["TouristTax", "propertyDocument"],
            from: new DateOnly(2026, 1, 1),
            to: new DateOnly(2026, 12, 31),
            search: "ostuni",
            page: 2,
            pageSize: 10);

        var ok = Assert.IsType<OkObjectResult>(result);
        var page = Assert.IsType<Casazen.Web.DTOs.DocumentArchivePageDto>(ok.Value);
        Assert.Equal(41, page.TotalCount);
        var item = Assert.Single(page.Items);
        Assert.Equal("touristTax", item.Type);
        Assert.Equal(new DateOnly(2026, 8, 15), item.Date);

        Assert.NotNull(query);
        Assert.Equal(
            new[] { ArchiveDocumentType.TouristTax, ArchiveDocumentType.PropertyDocument }.Order(),
            query.Types!.Order());
        Assert.Equal(new DateOnly(2026, 1, 1), query.From);
        Assert.Equal(new DateOnly(2026, 12, 31), query.To);
        Assert.Equal("ostuni", query.Search);
        Assert.Equal((2, 10), (query.Page, query.PageSize));

        // The caller holds every permission here, so it may read every type that has a source (the sources are the three of the harness).
        Assert.NotNull(access);
        Assert.Equal(
            new[] { ArchiveDocumentType.PropertyDocument, ArchiveDocumentType.TouristTax, ArchiveDocumentType.AlloggiatiReceipt }.Order(),
            access.Types.Order());
    }

    [Fact]
    public async Task GetTypes_AnswersTheKeysOfTheTypesWithTheirCount()
    {
        var harness = new Harness();
        harness.Archive
            .Setup(a => a.CountByTypeAsync(Harness.Scope, It.IsAny<DocumentArchiveAccess>(), It.IsAny<DocumentArchiveQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new ArchiveTypeCount(ArchiveDocumentType.TouristTax, 0),
                new ArchiveTypeCount(ArchiveDocumentType.PropertyDocument, 7),
            ]);

        var result = await harness.Controller.GetTypes(null, null, null, null, null);

        var types = Assert.IsType<Casazen.Web.DTOs.DocumentArchiveTypesDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(
            [("touristTax", 0), ("propertyDocument", 7)],
            types.Types.Select(t => (t.Type, t.Count)));
    }
}
