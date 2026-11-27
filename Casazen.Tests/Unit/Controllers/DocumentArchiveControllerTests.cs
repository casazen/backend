using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Tests.Unit.Logging;
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
                scopes.Object,
                Log)
            {
                ControllerContext = new ControllerContext { HttpContext = http },
            };
        }

        public static Guid OrgId { get; } = Guid.NewGuid();

        public static HostScope Scope { get; } = new(OrgId);

        public Mock<IDocumentArchiveService> Archive { get; } = new();

        public CapturingLogger<DocumentArchiveController> Log { get; } = new();

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

    // ---------------------------------------------------------------- the download

    [Fact]
    public async Task Download_AnUnknownType_Is400()
    {
        var harness = new Harness();

        var result = await harness.Controller.Download("passport", Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Equal(DocumentArchiveController.InvalidTypeCode, CodeOf(result));
        harness.Archive.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Download_SendsTheFileUncached_WithItsNameAndType_AndLogsNothingForAPropertyDocument()
    {
        var harness = new Harness();
        var id = Guid.NewGuid();
        harness.Archive
            .Setup(a => a.OpenAsync(Harness.Scope, It.IsAny<DocumentArchiveAccess>(), ArchiveDocumentType.PropertyDocument, id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ArchiveFile(new MemoryStream([1, 2, 3]), "APE.pdf", "application/pdf"));

        var result = await harness.Controller.Download("propertyDocument", id, CancellationToken.None);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal("APE.pdf", file.FileDownloadName);
        Assert.Equal("private, no-store", harness.Http.Response.Headers.CacheControl.ToString());
        Assert.Equal("nosniff", harness.Http.Response.Headers.XContentTypeOptions.ToString());
        Assert.Empty(harness.Log.Entries);
        await file.FileStream.DisposeAsync();
    }

    /// <summary>
    /// PROOF TEST: privacy rule. Opening a compliance proof (it carries data about guests) writes one line in the log with the org, the user and the
    /// type, and nothing that names a document: no title, no file name.
    /// </summary>
    [Fact]
    public async Task Download_OfAComplianceProof_WritesOneLineWithIdsOnly()
    {
        var harness = new Harness();
        var id = Guid.NewGuid();
        harness.Archive
            .Setup(a => a.OpenAsync(Harness.Scope, It.IsAny<DocumentArchiveAccess>(), ArchiveDocumentType.AlloggiatiReceipt, id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ArchiveFile(new MemoryStream([1]), "ricevuta-mario-rossi.pdf", "application/pdf"));

        var result = await harness.Controller.Download("alloggiatiReceipt", id, CancellationToken.None);

        await Assert.IsType<FileStreamResult>(result).FileStream.DisposeAsync();
        var line = Assert.Single(harness.Log.Entries);
        Assert.Equal(Harness.OrgId, line.Values["OrgId"]);
        Assert.Equal(UserId, line.Values["UserId"]);
        Assert.Equal(ArchiveDocumentType.AlloggiatiReceipt, line.Values["Type"]);
        Assert.DoesNotContain("mario", harness.Log.AllOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(id.ToString(), harness.Log.AllOutput, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the export

    [Fact]
    public async Task Export_WithoutDaysOrBeyondTwentyFourMonths_IsRefusedWithTheCodesOfTheReports()
    {
        var harness = new Harness();

        var missing = await harness.Controller.Export(null, new DateOnly(2026, 12, 31), null, null, CancellationToken.None);
        var tooLong = await harness.Controller.Export(new DateOnly(2024, 1, 1), new DateOnly(2026, 12, 31), null, null, CancellationToken.None);

        Assert.Equal((400, "report_invalid_period"), (StatusOf(missing), CodeOf(missing)));
        Assert.Equal((422, "report_period_too_long"), (StatusOf(tooLong), CodeOf(tooLong)));
        harness.Archive.Verify(
            a => a.ExportAsync(It.IsAny<HostScope>(), It.IsAny<DocumentArchiveAccess>(), It.IsAny<DocumentArchiveQuery>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Export_AnUnknownType_Is400()
    {
        var harness = new Harness();

        var result = await harness.Controller.Export(new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31), ["receipts"], null, CancellationToken.None);

        Assert.Equal((400, DocumentArchiveController.InvalidTypeCode), (StatusOf(result), CodeOf(result)));
    }

    /// <summary>
    /// PROOF TEST: privacy rule. An export writes one line in the log with the org, the user and the counts, and nothing that names a document or
    /// a property. The service gets the days, the types and the property of the request and the response as the destination.
    /// </summary>
    [Fact]
    public async Task Export_WritesOneLineWithTheIdsAndTheCounts_AndGivesTheServiceTheResponse()
    {
        var harness = new Harness();
        var propertyId = Guid.NewGuid();
        DocumentArchiveQuery? query = null;
        Stream? destination = null;
        harness.Archive
            .Setup(a => a.ExportAsync(Harness.Scope, It.IsAny<DocumentArchiveAccess>(), It.IsAny<DocumentArchiveQuery>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Callback<HostScope, DocumentArchiveAccess, DocumentArchiveQuery, Stream, CancellationToken>((_, _, asked, to, _) =>
            {
                query = asked;
                destination = to;
            })
            .ReturnsAsync(new DocumentArchiveExportResult(3, 2, 1234));

        var result = await harness.Controller.Export(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31), ["contract"], propertyId, CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.Same(harness.Http.Response.Body, destination);
        Assert.NotNull(query);
        Assert.Equal([ArchiveDocumentType.Contract], query.Types);
        Assert.Equal(new DateOnly(2026, 1, 1), query.From);
        Assert.Equal(new DateOnly(2026, 3, 31), query.To);
        Assert.Equal(propertyId, query.PropertyId);
        Assert.Null(query.Search);

        var line = Assert.Single(harness.Log.Entries);
        Assert.Equal(Harness.OrgId, line.Values["OrgId"]);
        Assert.Equal(UserId, line.Values["UserId"]);
        Assert.Equal((3, 2, 1234L), ((int)line.Values["Documents"]!, (int)line.Values["Files"]!, (long)line.Values["Bytes"]!));
        Assert.DoesNotContain(propertyId.ToString(), harness.Log.AllOutput, StringComparison.Ordinal);
        Assert.Equal("private, no-store", harness.Http.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public void TheZipHeaders_AreSetForA200_AndNeverForAProblem()
    {
        var ok = new DefaultHttpContext();
        var refused = new DefaultHttpContext();
        refused.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;

        ZipDownloadHeaders.Apply(ok.Response, "casazen-documenti-20260101-20261231.zip");
        ZipDownloadHeaders.Apply(refused.Response, "casazen-documenti-20260101-20261231.zip");

        Assert.Equal("application/zip", ok.Response.ContentType);
        Assert.Contains("attachment", ok.Response.Headers.ContentDisposition.ToString(), StringComparison.Ordinal);
        Assert.Contains("casazen-documenti-20260101-20261231.zip", ok.Response.Headers.ContentDisposition.ToString(), StringComparison.Ordinal);
        Assert.Null(refused.Response.ContentType);
        Assert.Equal(string.Empty, refused.Response.Headers.ContentDisposition.ToString());
    }
}
