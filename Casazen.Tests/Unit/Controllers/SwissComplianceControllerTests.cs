using System.Text;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.SwissCompliance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// E08 (NF-15, ADR-016): the Swiss compliance endpoints hold guest data and declare a legal communication done, so a caller
/// whose scope does not reach the property cannot use them. The authorization is the production one (the resource handler and the
/// scope resolver over the caller's snapshot): a collaborator «Solo alcuni» given one property is served on it and refused (403)
/// on another property of the same org, and nothing is generated or stored for the refused one.
/// </summary>
public class SwissComplianceControllerTests
{
    private const string Member = "auth0|member";
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly Guid GivenProperty = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherProperty = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly DateOnly Night = new(2026, 10, 25);

    public static TheoryData<string> Endpoints => new()
    {
        "ticino-notification", "ticino-day", "otr-report", "upload-receipt", "list-receipts", "download-receipt", "group-sheet", "declare", "undeclare",
    };

    private readonly Mock<ITicinoNotificationService> _ticino = new();
    private readonly Mock<IOtrTaxService> _otr = new();
    private readonly Mock<ISwissComplianceReceiptService> _receipts = new();

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Endpoint_ACallerWhoseScopeDoesNotReachTheProperty_IsRefusedAndNothingIsGeneratedNorStored(string endpoint)
    {
        _ticino
            .Setup(s => s.GenerateFileAsync(
                GivenProperty, It.IsAny<DateOnly>(), It.IsAny<TicinoPoliceFileFormat>(), It.IsAny<TicinoFileRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TicinoNotificationFile([1], "notifiche-ti.csv", "text/csv", "TI-ref", "hash", 1));
        _ticino
            .Setup(s => s.GetDayAsync(GivenProperty, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TicinoNotificationDay(GivenProperty, Night, "NL-1234", 0, new TicinoDayPlan([], []), 0, DateTime.UtcNow, []));
        _otr
            .Setup(s => s.GeneratePeriodReportAsync(GivenProperty, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OtrTaxResult("PropertyId", "OTR-ref", "hash"));
        _receipts
            .Setup(s => s.RecordAsync(
                GivenProperty, SwissComplianceFileType.Police, It.IsAny<DateTime>(), "REF-1", It.IsAny<Stream>(),
                "receipt.pdf", "application/pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SwissComplianceReceipt { OrgId = OrgId, PropertyId = GivenProperty, Type = "Police", Reference = "REF-1" });
        _receipts.Setup(s => s.ListAsync(GivenProperty, 1, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SwissComplianceReceiptPage([], 1, 50, 0, IsTicinoProperty: true));
        _receipts.Setup(s => s.OpenFileAsync(GivenProperty, GivenProperty, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SwissComplianceReceiptFile(Stream.Null, "receipt.pdf", "application/pdf"));
        _ticino
            .Setup(s => s.GetGroupSheetAsync(GivenProperty, It.IsAny<Guid>(), It.IsAny<TicinoFileRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TicinoGroupSheet([1], "scheda-gruppo-ti.csv", "text/csv; charset=utf-8", 7));
        _ticino
            .Setup(s => s.DeclareDayAsync(GivenProperty, It.IsAny<DateOnly>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TicinoDeclaration(GivenProperty, Night, new DateTime(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc), "Anna Test", null, true));

        // A collaborator «Solo alcuni» of the org, given GivenProperty only; every permission of its role is held.
        var controller = NewController(GivenPropertyOnly());

        // The property it was given is served: the denial below comes from the scope, not from the wiring.
        Assert.IsNotType<ForbidResult>(await CallAsync(controller, endpoint, GivenProperty));

        // Another property of the same org, created by somebody else, is not reached: 403 and nobody is called for it.
        var refused = await CallAsync(controller, endpoint, OtherProperty);

        Assert.IsType<ForbidResult>(refused);
        _ticino.Verify(
            s => s.GenerateFileAsync(
                OtherProperty, It.IsAny<DateOnly>(), It.IsAny<TicinoPoliceFileFormat>(), It.IsAny<TicinoFileRequest?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _ticino.Verify(s => s.GetDayAsync(OtherProperty, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
        _otr.Verify(s => s.GeneratePeriodReportAsync(OtherProperty, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        _receipts.Verify(
            s => s.RecordAsync(OtherProperty, It.IsAny<SwissComplianceFileType>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<Stream>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _receipts.Verify(s => s.ListAsync(OtherProperty, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _receipts.Verify(s => s.OpenFileAsync(OtherProperty, It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _ticino.Verify(s => s.GetGroupSheetAsync(OtherProperty, It.IsAny<Guid>(), It.IsAny<TicinoFileRequest?>(), It.IsAny<CancellationToken>()), Times.Never);
        _ticino.Verify(
            s => s.DeclareDayAsync(OtherProperty, It.IsAny<DateOnly>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        _ticino.Verify(s => s.UndeclareDayAsync(OtherProperty, It.IsAny<DateOnly>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Declare_OtherOrganizationForbidden_Test()
    {
        // PROOF TEST (permission): a declaration closes a legal duty (the day is no longer one to notify, and the alerts stop), so a person of another org
        // must neither see the property (404) nor declare or undo a day on it, and one of the caller's org outside its scope is refused (403): the service is
        // never called for either. The property the caller was given is served, and the declaration is made in the name of the caller's own subject.
        var otherOrgProperty = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
        var declaredAt = new DateTime(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc);
        _ticino
            .Setup(s => s.DeclareDayAsync(GivenProperty, Night, Member, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TicinoDeclaration(GivenProperty, Night, declaredAt, "Anna Test", null, true));
        _ticino.Setup(s => s.UndeclareDayAsync(GivenProperty, Night, Member, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var resources = new Mock<IHostResourceLookup>();
        resources
            .Setup(r => r.ForPropertyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => id == otherOrgProperty ? null : new HostResource(OrgId, "auth0|creator", id));
        var controller = NewController(GivenPropertyOnly(), resources.Object);
        var request = new DeclareTicinoDayRequest { ArrivalDay = Night };

        var declareOtherOrg = (await controller.DeclareTicinoDay(otherOrgProperty, request, CancellationToken.None)).Result;
        var declareOutOfScope = (await controller.DeclareTicinoDay(OtherProperty, request, CancellationToken.None)).Result;
        var undoOtherOrg = await controller.UndeclareTicinoDay(otherOrgProperty, Night, CancellationToken.None);
        var undoOutOfScope = await controller.UndeclareTicinoDay(OtherProperty, Night, CancellationToken.None);
        var declared = (await controller.DeclareTicinoDay(GivenProperty, request, CancellationToken.None)).Result;
        var undone = await controller.UndeclareTicinoDay(GivenProperty, Night, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<ObjectResult>(declareOtherOrg).StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<ObjectResult>(undoOtherOrg).StatusCode);
        Assert.IsType<ForbidResult>(declareOutOfScope);
        Assert.IsType<ForbidResult>(undoOutOfScope);
        var answer = Assert.IsType<TicinoDeclarationResponse>(Assert.IsType<OkObjectResult>(declared).Value);
        Assert.Equal((GivenProperty, Night, declaredAt, "Anna Test", true), (answer.PropertyId, answer.ArrivalDay, answer.DeclaredAt, answer.DeclaredBy, answer.Created));
        Assert.Equal("private, no-store", controller.Response.Headers.CacheControl.ToString());
        Assert.IsType<NoContentResult>(undone);
        foreach (var refused in new[] { otherOrgProperty, OtherProperty })
        {
            _ticino.Verify(
                s => s.DeclareDayAsync(refused, It.IsAny<DateOnly>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
            _ticino.Verify(s => s.UndeclareDayAsync(refused, It.IsAny<DateOnly>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Theory]
    [InlineData(nameof(SwissComplianceController.DeclareTicinoDay))]
    [InlineData(nameof(SwissComplianceController.UndeclareTicinoDay))]
    public void Declare_AndUndo_AskTheSamePermissionAsRecordingAReceipt_Test(string action)
    {
        // The permission that records a receipt (alloggiati.submit: the owner and the property managers) is the one that declares a day and undoes it: a person who can only read
        // the bookings or the guests cannot close a legal duty. The class keeps asking booking.read on top.
        var policy = (typeof(SwissComplianceController).GetMethod(action)!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>())
            .Single().Policy;
        var receiptPolicy = typeof(SwissComplianceController).GetMethod(nameof(SwissComplianceController.UploadReceipt))!
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>().Single().Policy;

        Assert.Equal(CasazenPolicies.AlloggiatiSubmit, policy);
        Assert.Equal(receiptPolicy, policy);
        Assert.Equal(
            CasazenPolicies.BookingRead,
            typeof(SwissComplianceController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>().Single().Policy);
    }

    [Fact]
    public async Task Generate_PassesTheConfirmationAndTheCaller_AndTheGroupSheetPassesTheCaller_Test()
    {
        // The guard against duplicates lives in the service, which never reads the caller: the controller says who asks and whether they confirmed the regeneration.
        TicinoFileRequest? generated = null;
        TicinoFileRequest? sheetRequest = null;
        _ticino
            .Setup(s => s.GenerateFileAsync(
                GivenProperty, Night, TicinoPoliceFileFormat.Csv, It.IsAny<TicinoFileRequest?>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, DateOnly _, TicinoPoliceFileFormat _, TicinoFileRequest? request, CancellationToken _) => generated = request)
            .ReturnsAsync(new TicinoNotificationFile([1], "notifiche-ti.csv", "text/csv", "TI-ref", "hash", 1));
        _ticino
            .Setup(s => s.GetGroupSheetAsync(GivenProperty, It.IsAny<Guid>(), It.IsAny<TicinoFileRequest?>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, Guid _, TicinoFileRequest? request, CancellationToken _) => sheetRequest = request)
            .ReturnsAsync(new TicinoGroupSheet([1], "scheda-gruppo-ti.csv", "text/csv; charset=utf-8", 7));
        var controller = NewController(GivenPropertyOnly());

        await controller.GenerateTicinoNotification(
            GivenProperty, new TicinoNotificationRequest { ReportDate = Night, Format = "csv", ConfirmRegenerate = true }, CancellationToken.None);
        Assert.Equal(new TicinoFileRequest(Member, ConfirmRegenerate: true), generated);

        await controller.GenerateTicinoNotification(GivenProperty, new TicinoNotificationRequest { ReportDate = Night }, CancellationToken.None);
        Assert.Equal(new TicinoFileRequest(Member, ConfirmRegenerate: false), generated);

        await controller.DownloadGroupSheet(GivenProperty, Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(new TicinoFileRequest(Member), sheetRequest);
    }

    [Fact]
    public async Task Undeclare_WithoutTheDay_Is400_AndNothingIsUndone_Test()
    {
        var controller = NewController(GivenPropertyOnly());

        var result = await controller.UndeclareTicinoDay(GivenProperty, null, CancellationToken.None);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Equal("validation_error", (string?)problem.Extensions["code"]);
        _ticino.Verify(s => s.UndeclareDayAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GroupSheet_OtherOrganizationForbidden_Test()
    {
        // The sheet holds the names, birth dates and nationalities of the members: a property of another org is not even visible to the caller (404,
        // nothing built), one of the caller's org outside its scope is refused (403, nothing built), and the property it was given is served with
        // a file that is never cached.
        var otherOrgProperty = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
        _ticino
            .Setup(s => s.GetGroupSheetAsync(GivenProperty, It.IsAny<Guid>(), It.IsAny<TicinoFileRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TicinoGroupSheet([1], "scheda-gruppo-ti.csv", "text/csv; charset=utf-8", 7));
        var resources = new Mock<IHostResourceLookup>();
        resources
            .Setup(r => r.ForPropertyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => id == otherOrgProperty ? null : new HostResource(OrgId, "auth0|creator", id));
        var controller = NewController(GivenPropertyOnly(), resources.Object);

        var otherOrg = await controller.DownloadGroupSheet(otherOrgProperty, Guid.NewGuid(), CancellationToken.None);
        var outOfScope = await controller.DownloadGroupSheet(OtherProperty, Guid.NewGuid(), CancellationToken.None);
        var served = await controller.DownloadGroupSheet(GivenProperty, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<ObjectResult>(otherOrg).StatusCode);
        Assert.IsType<ForbidResult>(outOfScope);
        Assert.IsType<FileContentResult>(served);
        Assert.Equal("private, no-store", controller.Response.Headers.CacheControl.ToString());
        _ticino.Verify(s => s.GetGroupSheetAsync(otherOrgProperty, It.IsAny<Guid>(), It.IsAny<TicinoFileRequest?>(), It.IsAny<CancellationToken>()), Times.Never);
        _ticino.Verify(s => s.GetGroupSheetAsync(OtherProperty, It.IsAny<Guid>(), It.IsAny<TicinoFileRequest?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("Police")]
    [InlineData("ParentalAuthorization")]
    [InlineData("GroupSheet")]
    public async Task UploadReceipt_ADocumentKeptFiveYearsWithADateAlreadyPastThat_IsRefusedWith400_AndNothingIsRecorded_Test(string type)
    {
        // The daily retention job deletes such a document (file and row) the night it is recorded: a mistyped date must never destroy a legal proof in silence.
        var controller = NewController(GivenPropertyOnly());
        var form = ReceiptForm();
        form.Type = type;
        form.ReportDate = new DateOnly(2000, 1, 1);

        var result = (await controller.UploadReceipt(GivenProperty, form, CancellationToken.None)).Result;

        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Equal(SwissComplianceController.ReceiptDateTooOldCode, (string?)problem.Extensions["code"]);
        Assert.Equal("swiss_receipt_date_too_old", SwissComplianceController.ReceiptDateTooOldCode);
        _receipts.Verify(
            s => s.RecordAsync(
                It.IsAny<Guid>(), It.IsAny<SwissComplianceFileType>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<Stream>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UploadReceipt_AnOtrReportOfAnyDate_AndAYoungDocumentOfAKeptType_AreRecorded_Test()
    {
        // An OTR report is never deleted by the retention, so its date is never refused; a document of a kept type dated today is far from its time.
        _receipts
            .Setup(s => s.RecordAsync(
                GivenProperty, It.IsAny<SwissComplianceFileType>(), It.IsAny<DateTime>(), "REF-1", It.IsAny<Stream>(),
                "receipt.pdf", "application/pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SwissComplianceReceipt { OrgId = OrgId, PropertyId = GivenProperty, Type = "OTR", Reference = "REF-1" });
        var controller = NewController(GivenPropertyOnly());
        var oldOtr = ReceiptForm();
        oldOtr.Type = "OTR";
        oldOtr.ReportDate = new DateOnly(2000, 1, 1);
        var youngSheet = ReceiptForm();
        youngSheet.Type = "GroupSheet";
        youngSheet.ReportDate = TimeProvider.System.TodayInRomeAsDateOnly();

        var recordedOtr = (await controller.UploadReceipt(GivenProperty, oldOtr, CancellationToken.None)).Result;
        var recordedSheet = (await controller.UploadReceipt(GivenProperty, youngSheet, CancellationToken.None)).Result;

        Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<ObjectResult>(recordedOtr).StatusCode);
        Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<ObjectResult>(recordedSheet).StatusCode);
        _receipts.Verify(
            s => s.RecordAsync(
                GivenProperty, SwissComplianceFileType.Otr, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), "REF-1", It.IsAny<Stream>(),
                "receipt.pdf", "application/pdf", It.IsAny<CancellationToken>()),
            Times.Once);
        _receipts.Verify(
            s => s.RecordAsync(
                GivenProperty, SwissComplianceFileType.GroupSheet, It.IsAny<DateTime>(), "REF-1", It.IsAny<Stream>(),
                "receipt.pdf", "application/pdf", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static UserAuthorizationSnapshot GivenPropertyOnly() => new(
        Exists: true,
        IsActive: true,
        Role: UserRole.None,
        SupplierOrgId: null,
        Memberships: [],
        OrgMember: new OrgMemberSnapshot(
            OrgId, OrgRole.Collaborator, OrgMemberStatus.Active, PropertyScope.Selected, new HashSet<Guid> { GivenProperty }));

    private SwissComplianceController NewController(UserAuthorizationSnapshot snapshot, IHostResourceLookup? lookup = null)
    {
        var resources = new Mock<IHostResourceLookup>();
        resources
            .Setup(r => r.ForPropertyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => new HostResource(OrgId, "auth0|creator", id));

        var authorization = HostAuthorizationTestHarness.Create(
            OrgId, scopeResolver: HostAuthorizationTestHarness.ScopeResolver(snapshot));

        return new SwissComplianceController(
            _ticino.Object, _otr.Object, _receipts.Object, lookup ?? resources.Object, authorization, Mock.Of<ILogger<SwissComplianceController>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = HostAuthorizationTestHarness.User(Member),
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };
    }

    private static async Task<ActionResult?> CallAsync(SwissComplianceController controller, string endpoint, Guid propertyId) => endpoint switch
    {
        "ticino-notification" => await controller.GenerateTicinoNotification(
            propertyId, new TicinoNotificationRequest { ReportDate = Night, Format = "xml" }, CancellationToken.None),
        "ticino-day" => (await controller.GetTicinoNotificationDay(propertyId, Night, CancellationToken.None)).Result,
        "otr-report" => (await controller.GenerateOtrReport(
            propertyId, new OtrReportRequest { FromDate = Night, ToDate = Night.AddDays(6) }, CancellationToken.None)).Result,
        "list-receipts" => (await controller.ListReceipts(propertyId)).Result,
        "download-receipt" => await controller.DownloadReceipt(propertyId, GivenProperty),
        "group-sheet" => await controller.DownloadGroupSheet(propertyId, Guid.NewGuid(), CancellationToken.None),
        "declare" => (await controller.DeclareTicinoDay(propertyId, new DeclareTicinoDayRequest { ArrivalDay = Night }, CancellationToken.None)).Result,
        "undeclare" => await controller.UndeclareTicinoDay(propertyId, Night, CancellationToken.None),
        _ => (await controller.UploadReceipt(propertyId, ReceiptForm(), CancellationToken.None)).Result,
    };

    private static UploadSwissReceiptForm ReceiptForm()
    {
        var bytes = Encoding.UTF8.GetBytes("receipt of the portal");
        return new UploadSwissReceiptForm
        {
            Type = "police",
            ReportDate = Night,
            Reference = "REF-1",
            File = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "receipt.pdf")
            {
                Headers = new HeaderDictionary(),
                ContentType = "application/pdf",
            },
        };
    }
}
