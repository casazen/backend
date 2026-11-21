using System.Reflection;
using System.Text;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.SwissCompliance;
using Casazen.Web.Infrastructure;
using Casazen.Web.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
        "guest-register", "guest-register-info",
    };

    private readonly Mock<ITicinoNotificationService> _ticino = new();
    private readonly Mock<IOtrTaxService> _otr = new();
    private readonly Mock<ISwissComplianceReceiptService> _receipts = new();
    private readonly Mock<IGuestRegisterService> _register = new();

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
        SetUpRegister();

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
        _register.Verify(
            s => s.GenerateAsync(OtherProperty, It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<GuestRegisterRequest?>(), It.IsAny<CancellationToken>()), Times.Never);
        _register.Verify(s => s.GetInfoAsync(OtherProperty, It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
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

    // ─── The guest register (E31/F01/US01/T01, NF-130) ───

    [Fact]
    public async Task GuestRegister_OtherOrganizationForbidden_Test()
    {
        // PROOF TEST (permission): the register holds the names, the birth dates, the documents and the homes of the guests. A property of another org is not even visible to the
        // caller (404, nothing built), one of the caller's org outside its scope is refused (403, nothing built), and the property it was given is served, with a file that is never
        // cached. The info (the lines it has, no guest) follows the same rule.
        var otherOrgProperty = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
        SetUpRegister();
        var resources = new Mock<IHostResourceLookup>();
        resources
            .Setup(r => r.ForPropertyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => id == otherOrgProperty ? null : new HostResource(OrgId, "auth0|creator", id));
        var controller = NewController(GivenPropertyOnly(), resources.Object);
        var query = new GuestRegisterQuery { From = Night, To = Night.AddDays(30) };

        var fileOtherOrg = await controller.DownloadGuestRegister(otherOrgProperty, query, CancellationToken.None);
        var fileOutOfScope = await controller.DownloadGuestRegister(OtherProperty, query, CancellationToken.None);
        var infoOtherOrg = (await controller.GetGuestRegisterInfo(otherOrgProperty, query, CancellationToken.None)).Result;
        var infoOutOfScope = (await controller.GetGuestRegisterInfo(OtherProperty, query, CancellationToken.None)).Result;
        var servedFile = await controller.DownloadGuestRegister(GivenProperty, query, CancellationToken.None);
        var servedInfo = (await controller.GetGuestRegisterInfo(GivenProperty, query, CancellationToken.None)).Result;

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<ObjectResult>(fileOtherOrg).StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<ObjectResult>(infoOtherOrg).StatusCode);
        Assert.IsType<ForbidResult>(fileOutOfScope);
        Assert.IsType<ForbidResult>(infoOutOfScope);
        Assert.IsType<FileContentResult>(servedFile);
        Assert.IsType<OkObjectResult>(servedInfo);
        Assert.Equal("private, no-store", controller.Response.Headers.CacheControl.ToString());
        foreach (var refused in new[] { otherOrgProperty, OtherProperty })
        {
            _register.Verify(
                s => s.GenerateAsync(refused, It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<GuestRegisterRequest?>(), It.IsAny<CancellationToken>()), Times.Never);
            _register.Verify(s => s.GetInfoAsync(refused, It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public void GuestRegister_AsksTheGuestPermission_IsBehindTheFlag_AndTheFlagGatesNothingElseOfTheController_Test()
    {
        // The register holds guest data, so it asks guest.read on top of the booking.read of the class (as the other two files that list the guests); both actions answer 404 before
        // authentication while Features:GuestRegistrationRegimes is off; and the flag gates nothing else here: Ticino and the OTR report work with it off.
        var type = typeof(SwissComplianceController);
        string[] registerActions = [nameof(SwissComplianceController.DownloadGuestRegister), nameof(SwissComplianceController.GetGuestRegisterInfo)];

        foreach (var name in registerActions)
        {
            var method = type.GetMethod(name)!;
            Assert.Equal([FeatureFlags.GuestRegistrationRegimes], method.GetCustomAttributes<FeatureGateAttribute>().Select(a => a.Flag));
            Assert.Equal(CasazenPolicies.GuestRead, Assert.Single(method.GetCustomAttributes<AuthorizeAttribute>()).Policy);
            Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>());
        }

        Assert.Equal("guest-register", type.GetMethod(registerActions[0])!.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal("guest-register/info", type.GetMethod(registerActions[1])!.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal("api/properties/{propertyId:guid}/swiss", type.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal(CasazenPolicies.BookingRead, Assert.Single(type.GetCustomAttributes<AuthorizeAttribute>()).Policy);
        Assert.Empty(type.GetCustomAttributes<FeatureGateAttribute>());
        var others = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any() && !registerActions.Contains(m.Name))
            .ToList();
        Assert.NotEmpty(others);
        Assert.All(others, m => Assert.Empty(m.GetCustomAttributes<FeatureGateAttribute>()));
    }

    [Theory]
    [InlineData(nameof(SwissComplianceController.DownloadGuestRegister))]
    [InlineData(nameof(SwissComplianceController.GetGuestRegisterInfo))]
    public async Task GuestRegister_WhileTheFlagIsOff_AnswersLikeARouteThatDoesNotExist_AndRunsWhenItIsOn_Test(string action)
    {
        var flags = new TestFeatureFlags();
        var reached = 0;
        var middleware = new FeatureGateMiddleware(
            _ =>
            {
                reached++;
                return Task.CompletedTask;
            },
            flags,
            NullLogger<FeatureGateMiddleware>.Instance);
        var gates = typeof(SwissComplianceController).GetMethod(action)!.GetCustomAttributes<FeatureGateAttribute>().Cast<object>().ToArray();

        var off = new DefaultHttpContext();
        off.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(gates), action));
        await middleware.InvokeAsync(off);

        flags.Set(FeatureFlags.GuestRegistrationRegimes, true);
        var on = new DefaultHttpContext();
        on.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(gates), action));
        await middleware.InvokeAsync(on);

        Assert.Equal(StatusCodes.Status404NotFound, off.Response.StatusCode);
        Assert.Equal(1, reached);
        Assert.Equal(StatusCodes.Status200OK, on.Response.StatusCode);
    }

    [Theory]
    [InlineData("2026-10-01", "2026-09-30", "swiss_period_invalid")]
    [InlineData("2026-01-01", "2027-01-02", "swiss_period_too_long")]
    [InlineData("2028-01-01", "2029-01-01", "swiss_period_too_long")]
    public async Task GuestRegister_AnInvalidPeriodOrOneOverTheMaximum_Is400_AndNothingIsGenerated_Test(string from, string to, string code)
    {
        // The maximum is 366 days, both ends counted (the OTR report has the same rule and the same codes): one day more is refused before anybody is asked.
        var controller = NewController(GivenPropertyOnly());
        var query = new GuestRegisterQuery { From = DateOnly.Parse(from), To = DateOnly.Parse(to) };

        var file = await controller.DownloadGuestRegister(GivenProperty, query, CancellationToken.None);
        var info = (await controller.GetGuestRegisterInfo(GivenProperty, query, CancellationToken.None)).Result;

        foreach (var refused in new[] { file, info })
        {
            var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(refused).Value);
            Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
            Assert.Equal(code, (string?)problem.Extensions["code"]);
        }

        _register.Verify(
            s => s.GenerateAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<GuestRegisterRequest?>(), It.IsAny<CancellationToken>()), Times.Never);
        _register.Verify(s => s.GetInfoAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("2026-01-01", "2026-12-31")]
    [InlineData("2026-01-01", "2027-01-01")]
    [InlineData("2028-01-01", "2028-12-31")]
    [InlineData("2026-03-05", "2026-03-05")]
    public async Task GuestRegister_AYearBothEndsCounted_AndOneDay_AreServed_Test(string from, string to)
    {
        SetUpRegister();
        var controller = NewController(GivenPropertyOnly());
        var query = new GuestRegisterQuery { From = DateOnly.Parse(from), To = DateOnly.Parse(to) };

        Assert.IsType<FileContentResult>(await controller.DownloadGuestRegister(GivenProperty, query, CancellationToken.None));
        Assert.IsType<OkObjectResult>((await controller.GetGuestRegisterInfo(GivenProperty, query, CancellationToken.None)).Result);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("csv", true)]
    [InlineData(" CSV ", true)]
    [InlineData("xlsx", false)]
    [InlineData("xml", false)]
    [InlineData("pdf", false)]
    public async Task GuestRegister_TheOnlyFormatIsCsv_AnotherOneIs400_Test(string? format, bool served)
    {
        SetUpRegister();
        var controller = NewController(GivenPropertyOnly());

        var answer = await controller.DownloadGuestRegister(
            GivenProperty, new GuestRegisterQuery { From = Night, To = Night.AddDays(6), Format = format }, CancellationToken.None);

        if (served)
        {
            Assert.IsType<FileContentResult>(answer);
            return;
        }

        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(answer).Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Equal("guest_register_format_invalid", (string?)problem.Extensions["code"]);
        _register.Verify(
            s => s.GenerateAsync(It.IsAny<Guid>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<GuestRegisterRequest?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GuestRegister_Download_CarriesTheRetentionHintOfTheRegime_NeverCached_AndPassesTheCallerAndTheLanguage()
    {
        GuestRegisterRequest? asked = null;
        _register
            .Setup(s => s.GenerateAsync(GivenProperty, Night, Night.AddDays(30), It.IsAny<GuestRegisterRequest?>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, DateOnly _, DateOnly _, GuestRegisterRequest? request, CancellationToken _) => asked = request)
            .ReturnsAsync(RegisterDocument(retentionYears: 5));
        var controller = NewController(GivenPropertyOnly());

        var served = Assert.IsType<FileContentResult>(
            await controller.DownloadGuestRegister(GivenProperty, new GuestRegisterQuery { From = Night, To = Night.AddDays(30) }, CancellationToken.None));

        Assert.Equal("5", controller.Response.Headers[SwissComplianceController.RetentionYearsHeader].ToString());
        Assert.Equal("private, no-store", controller.Response.Headers.CacheControl.ToString());
        Assert.Equal(("registro-ospiti-ch-2026-10-25-2026-11-24.csv", "text/csv; charset=utf-8"), (served.FileDownloadName, served.ContentType));
        Assert.Equal(new GuestRegisterRequest(Member, DocumentLanguages.Current), asked);

        // The sources of a regime that give no number: the header is absent, not zero and not empty.
        _register
            .Setup(s => s.GenerateAsync(GivenProperty, Night, Night.AddDays(7), It.IsAny<GuestRegisterRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RegisterDocument(retentionYears: null));
        var withoutHint = NewController(GivenPropertyOnly());
        Assert.IsType<FileContentResult>(
            await withoutHint.DownloadGuestRegister(GivenProperty, new GuestRegisterQuery { From = Night, To = Night.AddDays(7) }, CancellationToken.None));
        Assert.False(withoutHint.Response.Headers.ContainsKey(SwissComplianceController.RetentionYearsHeader));
        Assert.Equal("private, no-store", withoutHint.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public void GuestRegister_TheInfoKeepsTheNamesOfTheBrief_AndHoldsNoGuest()
    {
        // The screen (E31/F02/US02/T01) reads rowCount, retentionYears and regimeCode before it downloads: the names are the contract.
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var property = Guid.NewGuid();

        var withHint = JsonSerializer.SerializeToElement(
            GuestRegisterInfoResponse.Of(new GuestRegisterInfo(property, Night, Night.AddDays(30), 12, 5, "CH-TI")), web);
        var withoutHint = JsonSerializer.SerializeToElement(
            GuestRegisterInfoResponse.Of(new GuestRegisterInfo(property, Night, Night.AddDays(30), 0, null, "CH-UNKNOWN")), web);

        Assert.Equal(["from", "propertyId", "regimeCode", "retentionYears", "rowCount", "to"], withHint.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal((12, 5, "CH-TI"), (withHint.GetProperty("rowCount").GetInt32(), withHint.GetProperty("retentionYears").GetInt32(), withHint.GetProperty("regimeCode").GetString()));
        Assert.Equal("2026-10-25", withHint.GetProperty("from").GetString());
        Assert.Equal("2026-11-24", withHint.GetProperty("to").GetString());
        Assert.Equal(JsonValueKind.Null, withoutHint.GetProperty("retentionYears").ValueKind);
        Assert.Equal((0, "CH-UNKNOWN"), (withoutHint.GetProperty("rowCount").GetInt32(), withoutHint.GetProperty("regimeCode").GetString()));
    }

    private void SetUpRegister()
    {
        _register
            .Setup(s => s.GenerateAsync(GivenProperty, It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<GuestRegisterRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RegisterDocument(retentionYears: 5));
        _register
            .Setup(s => s.GetInfoAsync(GivenProperty, It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuestRegisterInfo(GivenProperty, Night, Night.AddDays(6), 1, 5, "CH-TI"));
    }

    private static GuestRegisterDocument RegisterDocument(int? retentionYears) =>
        new([1, 2, 3], "registro-ospiti-ch-2026-10-25-2026-11-24.csv", "text/csv; charset=utf-8", new string('a', 64), 2, retentionYears, "CH-TI");

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
            _ticino.Object, _otr.Object, _receipts.Object, _register.Object, lookup ?? resources.Object, authorization, Mock.Of<ILogger<SwissComplianceController>>())
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
        "guest-register" => await controller.DownloadGuestRegister(
            propertyId, new GuestRegisterQuery { From = Night, To = Night.AddDays(6) }, CancellationToken.None),
        "guest-register-info" => (await controller.GetGuestRegisterInfo(
            propertyId, new GuestRegisterQuery { From = Night, To = Night.AddDays(6) }, CancellationToken.None)).Result,
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
