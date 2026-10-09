using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

public class GdprControllerTests
{
    private readonly Mock<IGdprService> _mockGdprService;
    private readonly Mock<IOrgContextResolver> _mockOrgContextResolver;
    private readonly Mock<IGuestAccessService> _mockGuestAccessService;
    private readonly Mock<IAuthorizationService> _mockAuthorization;
    private readonly Mock<IHostScopeResolver> _mockScopeResolver;
    private readonly Mock<IOrgActivityService> _mockOrgActivity = new();
    private readonly GdprController _controller;
    private static readonly Guid OrgId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    public GdprControllerTests()
    {
        _mockGdprService = new Mock<IGdprService>();
        _mockOrgContextResolver = new Mock<IOrgContextResolver>();
        _mockGuestAccessService = new Mock<IGuestAccessService>();
        _mockAuthorization = new Mock<IAuthorizationService>();
        _mockScopeResolver = new Mock<IHostScopeResolver>();
        _controller = new GdprController(
            _mockGdprService.Object,
            _mockOrgContextResolver.Object,
            _mockGuestAccessService.Object,
            _mockAuthorization.Object,
            _mockScopeResolver.Object,
            _mockOrgActivity.Object,
            new Mock<ILogger<GdprController>>().Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        _mockOrgContextResolver
            .Setup(x => x.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(OrgId);
        Authorize(true);
    }

    [Fact]
    public async Task ExportGuestData_GuestNotInOrg_ReturnsNotFound()
    {
        var guestId = GuestAccessible(false);

        var result = await _controller.ExportGuestData(guestId);

        Assert.IsType<NotFoundResult>(result);
        _mockGdprService.Verify(
            x => x.ExportGuestDataAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExportGuestData_WithoutGuestReadOnTheOrg_ReturnsForbidAndExportsNothing()
    {
        var guestId = GuestAccessible(true);
        Authorize(false);

        var result = await _controller.ExportGuestData(guestId);

        Assert.IsType<ForbidResult>(result);
        _mockGdprService.Verify(
            x => x.ExportGuestDataAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExportGuestData_GuestInOrg_ReturnsOkNeverCached()
    {
        var guestId = GuestAccessible(true);

        var result = await _controller.ExportGuestData(guestId);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("private, no-store", _controller.Response.Headers.CacheControl.ToString());
        _mockGdprService.Verify(
            x => x.ExportGuestDataAsync(OrgId, guestId, It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DeleteGuestData_GuestNotInOrg_ReturnsNotFound()
    {
        var guestId = GuestAccessible(false);

        var result = await _controller.DeleteGuestData(guestId);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task AnonymizeGuestData_GuestInOrg_ReturnsNoContent()
    {
        var guestId = GuestAccessible(true);

        var result = await _controller.AnonymizeGuestData(guestId);

        Assert.IsType<NoContentResult>(result);
        _mockGdprService.Verify(
            x => x.AnonymizeGuestDataAsync(OrgId, guestId, It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdateConsent_WithdrawalWithNote_ForwardsTheNoteToTheService()
    {
        var guestId = GuestAccessible(true);

        var result = await _controller.UpdateConsent(guestId, new UpdateConsentRequest(false, "Email del 02/09"));

        Assert.IsType<NoContentResult>(result);
        _mockGdprService.Verify(
            x => x.UpdateMarketingConsentAsync(OrgId, guestId, false, "Email del 02/09", It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ─── The org export carries the activity log, for who may read it (AM-02b) ───────────────────────────

    private static OrgActivityItem ActivityItem() => new(
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        new DateTime(2026, 10, 9, 10, 30, 0, DateTimeKind.Utc),
        "auth0|owner",
        OrgActivityArea.Account,
        OrgActivityType.MemberDeactivated,
        OrgActivitySubjectType.Member,
        "auth0|anna",
        new Dictionary<string, string> { ["role"] = "Collaborator" });

    private static async IAsyncEnumerable<OrgActivityItem> One(OrgActivityItem item)
    {
        yield return item;
        await Task.CompletedTask;
    }

    private void CanReadTheActivity(bool allowed) =>
        _mockAuthorization
            .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), CasazenPolicies.OrgActivityRead))
            .ReturnsAsync(allowed ? AuthorizationResult.Success() : AuthorizationResult.Failed());

    /// <summary>The caller of the export is the holder (AM-03b): it has a scope, the whole org.</summary>
    private void TheCallerIsTheHolder()
    {
        _controller.ControllerContext.HttpContext.User =
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "auth0|owner")], "Test"));
        _mockScopeResolver
            .Setup(r => r.ResolveAsync("auth0|owner", It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostScope(OrgId));
    }

    [Fact]
    public async Task ExportOrgFiscal_ForWhoMayReadTheActivityLog_IncludesTheLogWithIdsAndCodes()
    {
        TheCallerIsTheHolder();
        _mockGdprService.Setup(x => x.ExportOrgFiscalDataAsync(OrgId, It.IsAny<HostScope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, object> { ["fiscalCode"] = "RSSMRA80A01H501U" });
        _mockOrgActivity.Setup(a => a.StreamAsync(OrgId, It.IsAny<OrgActivityFilter>(), It.IsAny<CancellationToken>()))
            .Returns(One(ActivityItem()));
        CanReadTheActivity(true);

        var result = await _controller.ExportOrgFiscal(CancellationToken.None);

        var export = Assert.IsType<Dictionary<string, object>>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("RSSMRA80A01H501U", export["fiscalCode"]);
        var line = Assert.Single(Assert.IsType<List<OrgActivityEntryDto>>(export["activity"]));
        Assert.Equal(
            ("account", OrgActivityType.MemberDeactivated, "auth0|owner", "auth0|anna"),
            (line.Area, line.Type, line.ActorUserId, line.SubjectId));
    }

    [Fact]
    public async Task ExportOrgFiscal_ForWhoMayNotReadTheActivityLog_HasTheExportWithoutIt_AndReadsNothingOfTheLog()
    {
        TheCallerIsTheHolder();
        _mockGdprService.Setup(x => x.ExportOrgFiscalDataAsync(OrgId, It.IsAny<HostScope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, object> { ["fiscalCode"] = "RSSMRA80A01H501U" });
        CanReadTheActivity(false);

        var result = await _controller.ExportOrgFiscal(CancellationToken.None);

        var export = Assert.IsType<Dictionary<string, object>>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("RSSMRA80A01H501U", export["fiscalCode"]);
        Assert.DoesNotContain("activity", export.Keys);
        _mockOrgActivity.Verify(
            a => a.StreamAsync(It.IsAny<Guid>(), It.IsAny<OrgActivityFilter>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private Guid GuestAccessible(bool accessible)
    {
        var guestId = Guid.NewGuid();
        _mockGuestAccessService
            .Setup(x => x.IsGuestAccessibleAsync(guestId, OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(accessible);
        return guestId;
    }

    private void Authorize(bool allowed) =>
        _mockAuthorization
            .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<IEnumerable<IAuthorizationRequirement>>()))
            .ReturnsAsync(allowed ? AuthorizationResult.Success() : AuthorizationResult.Failed());
}
