using System.Text;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.SwissCompliance;
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

    public static TheoryData<string> Endpoints => new() { "ticino-notification", "otr-report", "upload-receipt" };

    private readonly Mock<ITicinoNotificationService> _ticino = new();
    private readonly Mock<IOtrTaxService> _otr = new();
    private readonly Mock<ISwissComplianceReceiptService> _receipts = new();

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Endpoint_ACallerWhoseScopeDoesNotReachTheProperty_IsRefusedAndNothingIsGeneratedNorStored(string endpoint)
    {
        _ticino
            .Setup(s => s.GenerateDailyNotificationAsync(GivenProperty, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TicinoNotificationResult("<TicinoNotification />", "TI-ref", "hash"));
        _otr
            .Setup(s => s.GeneratePeriodReportAsync(GivenProperty, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OtrTaxResult("PropertyId", "OTR-ref", "hash"));
        _receipts
            .Setup(s => s.RecordAsync(
                GivenProperty, SwissComplianceFileType.Police, It.IsAny<DateTime>(), "REF-1", It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SwissComplianceReceipt { OrgId = OrgId, PropertyId = GivenProperty, Type = "Police", Reference = "REF-1" });

        // A collaborator «Solo alcuni» of the org, given GivenProperty only; every permission of its role is held.
        var controller = NewController(GivenPropertyOnly());

        // The property it was given is served: the denial below comes from the scope, not from the wiring.
        Assert.IsNotType<ForbidResult>(await CallAsync(controller, endpoint, GivenProperty));

        // Another property of the same org, created by somebody else, is not reached: 403 and nobody is called for it.
        var refused = await CallAsync(controller, endpoint, OtherProperty);

        Assert.IsType<ForbidResult>(refused);
        _ticino.Verify(s => s.GenerateDailyNotificationAsync(OtherProperty, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        _otr.Verify(s => s.GeneratePeriodReportAsync(OtherProperty, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        _receipts.Verify(
            s => s.RecordAsync(OtherProperty, It.IsAny<SwissComplianceFileType>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static UserAuthorizationSnapshot GivenPropertyOnly() => new(
        Exists: true,
        IsActive: true,
        Role: UserRole.None,
        SupplierOrgId: null,
        Memberships: [],
        OrgMember: new OrgMemberSnapshot(
            OrgId, OrgRole.Collaborator, OrgMemberStatus.Active, PropertyScope.Selected, new HashSet<Guid> { GivenProperty }));

    private SwissComplianceController NewController(UserAuthorizationSnapshot snapshot)
    {
        var resources = new Mock<IHostResourceLookup>();
        resources
            .Setup(r => r.ForPropertyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => new HostResource(OrgId, "auth0|creator", id));

        var authorization = HostAuthorizationTestHarness.Create(
            OrgId, scopeResolver: HostAuthorizationTestHarness.ScopeResolver(snapshot));

        return new SwissComplianceController(
            _ticino.Object, _otr.Object, _receipts.Object, resources.Object, authorization, Mock.Of<ILogger<SwissComplianceController>>())
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
        "ticino-notification" => (await controller.GenerateTicinoNotification(
            propertyId, new TicinoNotificationRequest { ReportDate = Night }, CancellationToken.None)).Result,
        "otr-report" => (await controller.GenerateOtrReport(
            propertyId, new OtrReportRequest { FromDate = Night, ToDate = Night.AddDays(6) }, CancellationToken.None)).Result,
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
            File = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "receipt.pdf"),
        };
    }
}
