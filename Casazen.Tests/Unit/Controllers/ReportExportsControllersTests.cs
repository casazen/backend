using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Resources;
using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.BankStatements;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// E07/F03/US05/T01 (REP-06, NF-101): every <c>GET api/reports/*</c> and <c>GET api/tourist-tax/summary</c> answers <c>format=xlsx</c> next to <c>json</c> and
/// <c>csv</c>: an Excel file with the content type of an <c>.xlsx</c>, the name of the CSV with <c>.xlsx</c> in its place, never cached; the words of the request
/// are checked before anything is read, with the three formats in the message (Italian and English); the money endpoints stay protected by the policies they
/// had (nothing anonymous), and the file is read with the scope of the caller, never one from the request.
/// </summary>
public class ReportExportsControllersTests
{
    private static readonly Guid OrgId = Guid.Parse("5d1b8c3e-7a24-4f0b-9e61-3c8a2d4f7b10");
    private static readonly DateOnly First = new(2026, 10, 1);
    private static readonly DateOnly Last = new(2026, 10, 31);
    private static readonly HostScope Scope = new(OrgId, GrantedToUserId: "auth0|collaboratore");
    private static readonly HostDashboardPeriod Period = HostReportRules.PeriodOf(First, Last);

    private readonly Mock<IHostDashboardService> _dashboard = new();
    private readonly Mock<IHostCollectionsReportService> _collections = new();
    private readonly Mock<IChannelCostsReportService> _channelCosts = new();
    private readonly Mock<IOwnerReportService> _owners = new();
    private readonly Mock<ITouristTaxSummaryService> _touristTax = new();
    private readonly Mock<IOrgContextResolver> _org = new();
    private readonly Mock<IHostScopeResolver> _scopes = new();
    private readonly Mock<IStringLocalizer<SharedResources>> _localizer = new();
    private readonly DefaultHttpContext _http;

    public ReportExportsControllersTests()
    {
        _org.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        _scopes
            .Setup(s => s.ResolveAsync("auth0|collaboratore", It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Scope);
        _localizer.Setup(l => l[It.IsAny<string>()]).Returns((string key) => new LocalizedString(key, key));

        var figures = new HostPerformanceFigures(0, 0, 0, 0m, 0, 0);
        _dashboard.Setup(r => r.GetRevenueByPropertyAsync(Scope, It.IsAny<HostReportQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostRevenueByProperty(Period, [], 0m, 0, 0));
        _dashboard.Setup(r => r.GetOccupancyByPropertyAsync(Scope, It.IsAny<HostReportQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostOccupancyByProperty(Period, [], new HostDashboardOccupancy(0, 0, 0)));
        _dashboard.Setup(r => r.GetBookingsByChannelAsync(Scope, It.IsAny<HostReportQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostBookingsByChannel(Period, [], 0, 0, 0m));
        _dashboard.Setup(r => r.GetPerformanceAsync(Scope, It.IsAny<HostPerformanceQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostPerformanceReport(Period, null, [], new HostPerformanceRow(HostPerformanceRules.TotalKey, null, "Totale", figures, null)));
        _dashboard.Setup(r => r.GetTrendAsync(Scope, It.IsAny<HostTrendQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostTrendReport(Period, null, [], []));
        _collections.Setup(r => r.GetCollectionsAsync(Scope, It.IsAny<HostReportQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostCollectionsReport(
                Period,
                new HostDashboardCollected(0m, 0),
                HostCollectionsRules.EmptyBucketsOf(Period),
                [],
                [],
                new HostCredits(new HostStripeCredits(false, [], 0, 0), new HostChannelCredits(false, [], 0))));
        _collections.Setup(r => r.GetBalancesToCollectAsync(Scope, It.IsAny<HostBalancesQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostBalancesToCollect(new DateOnly(2026, 10, 9), [], []));
        _channelCosts.Setup(r => r.GetChannelCostsAsync(Scope, It.IsAny<HostReportQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChannelCostsReport(Period, [], new ChannelCostRow(null, null, 0, 0m, null, null, null, null, 0, false)));
        _owners.Setup(r => r.GetByOwnerAsync(Scope, It.IsAny<HostPerformanceQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OwnerPerformanceReport(
                Period, [], new OwnerPerformanceRow(null, null, false, 0, OwnerPerformanceFigures.Zero), OwnerPerformanceFigures.Zero));
        _touristTax.Setup(s => s.GetSummaryAsync(Scope, It.IsAny<FiscalReportPeriod>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TouristTaxSummary(new FiscalReportPeriod(First, Last), [], new TouristTaxSummaryTotals(0, 0, 0, 0m, 0, 0m, 0m)));

        _http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "auth0|collaboratore")], "TestAuth")),
            RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
        };
    }

    private T WithContext<T>(T controller)
        where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = _http };
        return controller;
    }

    /// <summary>The endpoints, each with the stem of the file name of its CSV (the extension is the format).</summary>
    private static readonly Dictionary<string, string> Stems = new()
    {
        ["revenue-by-property"] = "casazen-ricavo-per-immobile-competenza-20261001-20261031",
        ["occupancy-by-property"] = "casazen-occupazione-per-immobile-20261001-20261031",
        ["bookings-by-channel"] = "casazen-prenotazioni-per-canale-competenza-20261001-20261031",
        ["performance"] = "casazen-prestazioni-competenza-20261001-20261031",
        ["trend"] = "casazen-andamento-competenza-20261001-20261031",
        ["collections"] = "casazen-incassato-per-cassa-20261001-20261031",
        ["balances-to-collect"] = "casazen-saldi-da-incassare-20261009",
        ["channel-costs"] = "casazen-costi-dei-canali-competenza-20261001-20261031",
        ["by-owner"] = "casazen-prestazioni-per-proprietario-competenza-20261001-20261031",
        ["tourist-tax-summary"] = "casazen-tassa-soggiorno-riepilogo-20261001-20261031",
    };

    private Task<IActionResult> Call(string endpoint, string? format)
    {
        var propertyId = (Guid?)null;
        return endpoint switch
        {
            "revenue-by-property" => Reports().RevenueByProperty(First, Last, propertyId, format, CancellationToken.None),
            "occupancy-by-property" => Reports().OccupancyByProperty(First, Last, propertyId, format, CancellationToken.None),
            "bookings-by-channel" => Reports().BookingsByChannel(First, Last, propertyId, format, CancellationToken.None),
            "performance" => Reports().Performance(First, Last, propertyId, null, null, format, CancellationToken.None),
            "trend" => Reports().Trend(First, Last, propertyId, null, null, null, format, CancellationToken.None),
            "collections" => WithContext(new CollectionsReportsController(_collections.Object, _org.Object, _scopes.Object))
                .Collections(First, Last, propertyId, format, CancellationToken.None),
            "balances-to-collect" => WithContext(new CollectionsReportsController(_collections.Object, _org.Object, _scopes.Object))
                .BalancesToCollect(propertyId, format, CancellationToken.None),
            "channel-costs" => WithContext(new ChannelCostsReportsController(_channelCosts.Object, _org.Object, _scopes.Object))
                .ChannelCosts(First, Last, propertyId, format, CancellationToken.None),
            "by-owner" => WithContext(new OwnerReportsController(_owners.Object, _org.Object, _scopes.Object, _localizer.Object))
                .Get(First, Last, format, CancellationToken.None),
            "tourist-tax-summary" => WithContext(new TouristTaxSummaryController(_touristTax.Object, _org.Object, _scopes.Object, _localizer.Object))
                .GetSummary(First, Last, format, CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, null),
        };
    }

    private ReportsController Reports() => WithContext(new ReportsController(_dashboard.Object, _org.Object, _scopes.Object));

    private static int SheetCount(byte[] workbook)
    {
        using var zip = new ZipArchive(new MemoryStream(workbook, writable: false), ZipArchiveMode.Read);
        return zip.Entries.Count(entry => entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal));
    }

    public static IEnumerable<object[]> Endpoints() => Stems.Keys.Select(endpoint => new object[] { endpoint });

    // ---------------------------------------------------------------- the formats

    /// <summary>Excel: the content type of an <c>.xlsx</c>, the name of the CSV with the new extension, never cached, a workbook the reader opens; read with the scope of the caller.</summary>
    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Endpoint_AsXlsx_IsAnExcelFileNamedLikeTheCsv_NeverCached(string endpoint)
    {
        var result = await Call(endpoint, "XLSX");

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal(XlsxWriter.ContentType, file.ContentType);
        Assert.Equal(Stems[endpoint] + ".xlsx", file.FileDownloadName);
        Assert.Equal("no-store", _http.Response.Headers.CacheControl.ToString());
        Assert.True(XlsxTableReader.LooksLikeWorkbook(file.FileContents));
    }

    /// <summary>The CSV is the one it was: its type, its name, and the same header that keeps it from being stored.</summary>
    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Endpoint_AsCsv_IsStillTheCsvFileItWas(string endpoint)
    {
        var result = await Call(endpoint, "csv");

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/csv", file.ContentType);
        Assert.Equal(Stems[endpoint] + ".csv", file.FileDownloadName);
        Assert.Equal("no-store", _http.Response.Headers.CacheControl.ToString());
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, file.FileContents.Take(3));
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Endpoint_WithJsonOrNoFormat_StillAnswersJson(string endpoint)
    {
        Assert.IsType<OkObjectResult>(await Call(endpoint, "json"));
        Assert.IsType<OkObjectResult>(await Call(endpoint, null));
        Assert.IsType<OkObjectResult>(await Call(endpoint, " "));
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Endpoint_WithAnyOtherFormat_IsA400_BeforeAnythingIsRead(string endpoint)
    {
        foreach (var format in new[] { "pdf", "xls", "xlsx2", "docx", "csv;xlsx" })
        {
            var result = await Call(endpoint, format);

            var problem = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
            Assert.Equal(ReportRequestChecks.InvalidFormatCode, Assert.IsType<ProblemDetails>(problem.Value).Extensions[ApiProblemDetails.CodeExtension]);
        }

        _dashboard.VerifyNoOtherCalls();
        _collections.VerifyNoOtherCalls();
        _channelCosts.VerifyNoOtherCalls();
        _owners.VerifyNoOtherCalls();
        _touristTax.VerifyNoOtherCalls();
    }

    /// <summary>The message of <c>report_invalid_format</c> names the three formats, in Italian (the default) and in English.</summary>
    [Fact]
    public void TheInvalidFormatMessage_NamesTheThreeFormats_InItalianAndInEnglish()
    {
        var manager = new ResourceManager(typeof(SharedResources).FullName!, typeof(SharedResources).Assembly);

        foreach (var culture in new[] { CultureInfo.InvariantCulture, new CultureInfo("en") })
        {
            var set = manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!;
            var message = set.Cast<DictionaryEntry>().Single(entry => (string)entry.Key == "ReportInvalidFormat").Value as string;

            Assert.NotNull(message);
            Assert.Contains("format=json", message, StringComparison.Ordinal);
            Assert.Contains("format=csv", message, StringComparison.Ordinal);
            Assert.Contains("format=xlsx", message, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- the scope

    /// <summary>The file is read with the scope the controller resolved for the caller (a service that gets another one is not set up, and the call fails): never the org of the request.</summary>
    [Fact]
    public async Task Xlsx_OfACallerWhoReachesSomeOfThePropertiesOnly_IsReadWithThatScope_AndTheTouristTaxPaymentsStayHidden()
    {
        var owedOnly = new TouristTaxSummary(
            new FiscalReportPeriod(First, Last),
            [new ComuneTaxSummary("Roma", 2, 5, 6, 12.5m, 0, null, null, null, null)],
            new TouristTaxSummaryTotals(2, 5, 6, 12.5m, 0, null, null),
            PaymentsHidden: true);
        _touristTax.Setup(s => s.GetSummaryAsync(Scope, new FiscalReportPeriod(First, Last), It.IsAny<CancellationToken>())).ReturnsAsync(owedOnly);

        var result = await Call("tourist-tax-summary", "xlsx");

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal(1, SheetCount(file.FileContents));
        _touristTax.Verify(s => s.GetSummaryAsync(Scope, new FiscalReportPeriod(First, Last), It.IsAny<CancellationToken>()), Times.Once);
        _touristTax.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Xlsx_OfACallerWithoutAnOrgOrAScope_Is403_AndNothingIsRead()
    {
        _org.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);

        foreach (var endpoint in Stems.Keys)
        {
            var problem = Assert.IsAssignableFrom<ObjectResult>(await Call(endpoint, "xlsx"));
            Assert.Equal(StatusCodes.Status403Forbidden, problem.StatusCode);
        }

        _dashboard.VerifyNoOtherCalls();
        _collections.VerifyNoOtherCalls();
        _channelCosts.VerifyNoOtherCalls();
        _owners.VerifyNoOtherCalls();
        _touristTax.VerifyNoOtherCalls();
    }

    // ---------------------------------------------------------------- the permissions

    /// <summary>
    /// Money is protected as it was: <c>payment.read</c> on every class (the owner report asks <c>agency.statement.view</c> too), nothing anonymous and nothing
    /// but a read, so the Excel file is behind the same doors as the CSV and the JSON.
    /// </summary>
    [Theory]
    [InlineData(typeof(ReportsController), "api/reports", false)]
    [InlineData(typeof(CollectionsReportsController), "api/reports", false)]
    [InlineData(typeof(ChannelCostsReportsController), "api/reports/channel-costs", false)]
    [InlineData(typeof(OwnerReportsController), "api/reports/by-owner", true)]
    [InlineData(typeof(TouristTaxSummaryController), "api/tourist-tax", false)]
    public void TheReportControllers_StayBehindTheirPolicies_AndNothingIsAnonymous(Type controller, string route, bool needsAgencyStatementView)
    {
        var policies = controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Select(a => a.Policy).ToList();

        Assert.Contains(CasazenPolicies.PaymentRead, policies);
        Assert.Equal(needsAgencyStatementView, policies.Contains(CasazenPolicies.AgencyStatementView));
        Assert.Null(controller.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal(route, controller.GetCustomAttribute<RouteAttribute>()!.Template);
        foreach (var action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            Assert.Null(action.GetCustomAttribute<AllowAnonymousAttribute>());
    }
}
