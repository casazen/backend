using System.Globalization;
using System.Security.Claims;
using System.Text;
using Casazen.Core.Authorization;
using Casazen.Core.BankStatements;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Services;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// E06/F03/US03/T01 (FIS-07, NF-87): <c>GET api/fiscal/documents/export</c> asks for a format it knows and days it supports before anything is read, answers
/// 404 for a booking or a property the caller may not read the payments of, reads the documents in the scope of the caller and no wider, and serves them
/// as a CSV or an Excel file named after the month, never cached.
/// </summary>
public class FiscalDocumentExportsControllerTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private const string UserId = "auth0|collaborator-solo-alcuni";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("pdf")]
    [InlineData("json")]
    [InlineData("xls")]
    public async Task Export_AFormatThatIsMissingOrUnknown_Is400_AndNothingIsRead(string? format)
    {
        var documents = new Mock<IFiscalDocumentService>(MockBehavior.Strict);

        var answer = await Controller(documents.Object).Export(format);

        AssertProblem(answer, StatusCodes.Status400BadRequest, FiscalDocumentExportsController.InvalidFormatCode);
        documents.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Export_DaysThatAreBackwardsOrOutOfTheSupportedYears_Are400_AndNothingIsRead()
    {
        var documents = new Mock<IFiscalDocumentService>(MockBehavior.Strict);
        var controller = Controller(documents.Object);

        AssertProblem(await controller.Export("csv", new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 1)), StatusCodes.Status400BadRequest, FiscalDocumentExportsController.InvalidRangeCode);
        AssertProblem(await controller.Export("csv", new DateOnly(1999, 12, 31), null), StatusCodes.Status400BadRequest, FiscalDocumentExportsController.InvalidRangeCode);
        AssertProblem(await controller.Export("csv", new DateOnly(1, 1, 1), null), StatusCodes.Status400BadRequest, FiscalDocumentExportsController.InvalidRangeCode);
        AssertProblem(await controller.Export("xlsx", null, new DateOnly(2101, 1, 1)), StatusCodes.Status400BadRequest, FiscalDocumentExportsController.InvalidRangeCode);
        documents.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Export_ACallerWithoutAnOrganization_IsUnauthorized_AndNothingIsRead()
    {
        var documents = new Mock<IFiscalDocumentService>(MockBehavior.Strict);

        var answer = await Controller(documents.Object, hasOrg: false).Export("csv");

        Assert.IsType<UnauthorizedResult>(answer);
        documents.VerifyNoOtherCalls();
    }

    /// <summary>Permission rule: a booking or a property the caller may not read the payments of (or that is not there) is a 404, and no document is read.</summary>
    [Fact]
    public async Task Export_ABookingOrAPropertyTheCallerMayNotRead_IsNotFound_AndNothingIsRead()
    {
        var bookingId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var documents = new Mock<IFiscalDocumentService>(MockBehavior.Strict);
        var resources = new Mock<IHostResourceLookup>();
        resources.Setup(r => r.ForBookingAsync(bookingId, It.IsAny<CancellationToken>())).ReturnsAsync(new HostResource(OrgId, "auth0|owner", propertyId));
        resources.Setup(r => r.ForPropertyAsync(propertyId, It.IsAny<CancellationToken>())).ReturnsAsync(new HostResource(OrgId, "auth0|owner", propertyId));
        var controller = Controller(documents.Object, resources.Object, Authorization(allowed: false));

        Assert.IsType<NotFoundResult>(await controller.Export("csv", bookingId: bookingId));
        Assert.IsType<NotFoundResult>(await controller.Export("csv", bookingId: Guid.NewGuid()));
        Assert.IsType<NotFoundResult>(await controller.Export("xlsx", propertyId: propertyId));
        Assert.IsType<NotFoundResult>(await controller.Export("xlsx", propertyId: Guid.NewGuid()));
        documents.VerifyNoOtherCalls();
    }

    /// <summary>Permission rule: the documents are read in the scope of the caller (never a wider one), with the filters of the request, and served as the file asked for.</summary>
    [Fact]
    public async Task Export_ServesTheFileOfTheScopeOfTheCaller_AsCsvAndAsExcel_NeverCached()
    {
        var bookingId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var scope = new HostScope(OrgId, GrantedToUserId: UserId);
        var criteria = new FiscalDocumentCriteria(bookingId, propertyId, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), FiscalDocumentKind.Invoice, null, FiscalDocumentSource.CasaZen);
        IReadOnlyList<FiscalDocumentExportRow> rows =
        [
            Row("2026/1", new DateOnly(2026, 10, 5)),
            Row("2026/2", new DateOnly(2026, 10, 20)),
        ];
        var documents = new Mock<IFiscalDocumentService>(MockBehavior.Strict);
        documents.Setup(d => d.ListForExportAsync(scope, criteria, FiscalDocumentExports.Limit, It.IsAny<CancellationToken>())).ReturnsAsync(rows);
        var resources = new Mock<IHostResourceLookup>();
        resources.Setup(r => r.ForBookingAsync(bookingId, It.IsAny<CancellationToken>())).ReturnsAsync(new HostResource(OrgId, "auth0|owner", propertyId));
        resources.Setup(r => r.ForPropertyAsync(propertyId, It.IsAny<CancellationToken>())).ReturnsAsync(new HostResource(OrgId, "auth0|owner", propertyId));

        var csvController = Controller(documents.Object, resources.Object, Authorization(allowed: true), scope);
        var csv = Assert.IsType<FileContentResult>(await csvController.Export("csv", criteria.From, criteria.To, criteria.Kind, criteria.Source, bookingId, propertyId));
        var xlsxController = Controller(documents.Object, resources.Object, Authorization(allowed: true), scope);
        var xlsx = Assert.IsType<FileContentResult>(await xlsxController.Export("XLSX", criteria.From, criteria.To, criteria.Kind, criteria.Source, bookingId, propertyId));

        Assert.Equal("text/csv", csv.ContentType);
        Assert.Equal("documenti-2026-10.csv", csv.FileDownloadName);
        Assert.Equal(3, Encoding.UTF8.GetString(csv.FileContents, 3, csv.FileContents.Length - 3).TrimEnd('\r', '\n').Split("\r\n").Length);
        Assert.Equal(XlsxWriter.ContentType, xlsx.ContentType);
        Assert.Equal("documenti-2026-10.xlsx", xlsx.FileDownloadName);
        Assert.Equal(3, XlsxTableReader.Read(xlsx.FileContents).Count);
        Assert.Equal("no-store", csvController.Response.Headers.CacheControl.ToString());
        Assert.Equal("no-store", xlsxController.Response.Headers.CacheControl.ToString());
        documents.Verify(d => d.ListForExportAsync(scope, criteria, FiscalDocumentExports.Limit, It.IsAny<CancellationToken>()), Times.Exactly(2));
        documents.VerifyNoOtherCalls();
    }

    /// <summary>The file is named after the month of <c>from</c>, else of <c>to</c>, else of today in Rome (a Rome calendar month, not a UTC one).</summary>
    [Fact]
    public async Task Export_IsNamedAfterTheMonth_OfFrom_ElseOfTo_ElseOfTodayInRome()
    {
        var documents = new Mock<IFiscalDocumentService>();
        documents.Setup(d => d.ListForExportAsync(It.IsAny<HostScope>(), It.IsAny<FiscalDocumentCriteria>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FiscalDocumentExportRow>());
        var newYearInRome = new DateTimeOffset(2026, 12, 31, 23, 30, 0, TimeSpan.Zero);

        Assert.Equal("documenti-2026-03.csv", Assert.IsType<FileContentResult>(await Controller(documents.Object).Export("csv", new DateOnly(2026, 3, 10), new DateOnly(2026, 6, 30))).FileDownloadName);
        Assert.Equal("documenti-2026-06.xlsx", Assert.IsType<FileContentResult>(await Controller(documents.Object).Export("xlsx", null, new DateOnly(2026, 6, 30))).FileDownloadName);
        Assert.Equal("documenti-2026-11.csv", Assert.IsType<FileContentResult>(await Controller(documents.Object).Export("csv")).FileDownloadName);
        Assert.Equal("documenti-2027-01.csv", Assert.IsType<FileContentResult>(await Controller(documents.Object, now: newYearInRome).Export("csv")).FileDownloadName);
    }

    [Fact]
    public async Task Export_SaysItsWordsInTheLanguageOfTheRequest()
    {
        var documents = new Mock<IFiscalDocumentService>();
        documents.Setup(d => d.ListForExportAsync(It.IsAny<HostScope>(), It.IsAny<FiscalDocumentCriteria>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FiscalDocumentExportRow>());
        var culture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            var english = Assert.IsType<FileContentResult>(await Controller(documents.Object).Export("csv"));
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("it");
            var italian = Assert.IsType<FileContentResult>(await Controller(documents.Object).Export("csv"));

            Assert.StartsWith("Type;Number;Issue date;", Encoding.UTF8.GetString(english.FileContents, 3, english.FileContents.Length - 3));
            Assert.StartsWith("Tipo;Numero;Data di emissione;", Encoding.UTF8.GetString(italian.FileContents, 3, italian.FileContents.Length - 3));
        }
        finally
        {
            CultureInfo.CurrentUICulture = culture;
        }
    }

    /// <summary>A list longer than a file holds is a 422 (the writers refuse it) and no file is served.</summary>
    [Theory]
    [InlineData("csv", "csv_too_many_rows")]
    [InlineData("xlsx", "xlsx_too_many_rows")]
    public async Task Export_AListLongerThanAFile_IsRefused(string format, string code)
    {
        var documents = new Mock<IFiscalDocumentService>();
        documents.Setup(d => d.ListForExportAsync(It.IsAny<HostScope>(), It.IsAny<FiscalDocumentCriteria>(), FiscalDocumentExports.Limit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Repeat(Row("2026/1", new DateOnly(2026, 10, 5)), FiscalDocumentExports.Limit).ToList());

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => Controller(documents.Object).Export(format));

        Assert.Equal(code, refused.Code);
    }

    private static FiscalDocumentExportRow Row(string number, DateOnly issuedOn) => new(
        Guid.NewGuid(),
        FiscalDocumentKind.Invoice,
        number,
        issuedOn,
        "ABCDEFGHJK",
        "TEST Casa",
        "TEST Cliente Srl",
        330.18m,
        31.82m,
        0m,
        362m,
        "EUR",
        FiscalDocumentPaymentState.ToCollect,
        FiscalDocumentSdiStatuses.ToSend,
        FiscalDocumentSource.CasaZen);

    private static void AssertProblem(ActionResult answer, int status, string code)
    {
        var problem = Assert.IsType<ObjectResult>(answer);
        Assert.Equal(status, problem.StatusCode);
        Assert.Equal(code, Assert.IsAssignableFrom<ProblemDetails>(problem.Value).Extensions["code"]);
    }

    private static FiscalDocumentExportsController Controller(
        IFiscalDocumentService documents,
        IHostResourceLookup? resources = null,
        IAuthorizationService? authorization = null,
        HostScope? scope = null,
        bool hasOrg = true,
        DateTimeOffset? now = null)
    {
        var organization = new Mock<IOrgContextResolver>();
        Guid? orgId = hasOrg ? OrgId : null;
        organization.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(orgId);
        var resolver = new Mock<IHostScopeResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>())).ReturnsAsync(scope ?? new HostScope(OrgId));
        return new FiscalDocumentExportsController(
            documents,
            new ResxDocumentTexts(),
            resources ?? Mock.Of<IHostResourceLookup>(),
            authorization ?? Authorization(allowed: true),
            organization.Object,
            resolver.Object,
            new FakeTimeProvider(now ?? Now),
            NullLogger<FiscalDocumentExportsController>.Instance)
        {
            ControllerContext = Context(),
        };
    }

    private static IAuthorizationService Authorization(bool allowed)
    {
        var service = new Mock<IAuthorizationService>();
        service
            .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<IEnumerable<IAuthorizationRequirement>>()))
            .ReturnsAsync(allowed ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        return service.Object;
    }

    /// <summary>A signed-in caller, and the services the problem answer of a refusal needs.</summary>
    private static ControllerContext Context()
    {
        var localizer = new Mock<IStringLocalizer<SharedResources>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string name) => new LocalizedString(name, name, resourceNotFound: true));
        localizer.Setup(l => l[It.IsAny<string>(), It.IsAny<object[]>()]).Returns((string name, object[] _) => new LocalizedString(name, name, resourceNotFound: true));
        var services = new ServiceCollection().AddSingleton(localizer.Object).BuildServiceProvider();
        var identity = new ClaimsIdentity([new Claim("sub", UserId), new Claim(ClaimTypes.NameIdentifier, UserId)], "test");
        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity), RequestServices = services },
        };
    }
}
